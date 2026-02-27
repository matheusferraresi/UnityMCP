# Hot Reload Research - Technical Deep Dive

**Last Updated**: February 2026
**Purpose**: Document all research on implementing method-level hot reload within the MCP.

---

## The Opportunity

No Unity MCP has AI-integrated hot reload. The workflow today:

```
Agent edits script → Unity recompiles (2-10s) → Domain reload (5-30s) → Play mode restarts → Test again
```

With hot_patch:
```
Agent edits method body → Patched in 15ms → Game continues running → Instant feedback
```

This is the single biggest productivity gain possible for AI-assisted Unity development.

---

## Existing Solutions

### Hot Reload for Unity ($50, Immersive VR Tools)

**Website**: https://hotreload.net
**Approach**: C# compiler extension

| Feature | Supported |
|---------|-----------|
| Method body changes | ✅ |
| New methods | ✅ |
| New properties/events | ✅ |
| New fields | ✅ (limited) |
| New classes | ✅ |
| Generic type changes | ✅ |
| Static field changes | ✅ |
| Field type changes | ❌ (requires full recompile) |
| Attribute changes | ❌ |
| Signature changes | ❌ |

**Technical Approach** (from changelog analysis):
- Works at IL level - "IL being generated" for patches
- Compiles only the changed method (milliseconds)
- Swaps function pointer to new version
- Handles inlined methods in release mode
- Supports both Edit and Play mode
- v1.13.15 (Dec 2026): C# 14 support, Unity 6 support

**Pricing**: ~$50 one-time, Unity Asset Store

**Why we can't just bundle it**: Proprietary, closed source. But we can build a simpler version.

---

### Harmony Library (MIT, Open Source)

**Repository**: https://github.com/pardeike/Harmony
**Version**: 2.3+ (latest)
**Size**: ~200KB DLL
**License**: MIT
**Used by**: BepInEx, MelonLoader, SMAPI (Stardew Valley), thousands of Unity mods

**What Harmony Does**:
- Patches .NET methods at runtime via IL manipulation
- Supports prefix (before), postfix (after), transpiler (rewrite IL), and finalizer (exception handling) patches
- Works with Mono runtime (Unity's default scripting backend)
- Thread-safe patching
- Reversible patches

**How It Works**:
```csharp
// 1. Create Harmony instance
var harmony = new Harmony("com.unitymcp.hotpatch");

// 2. Get the original method
var original = AccessTools.Method(typeof(PlayerController), "Update");

// 3. Create replacement (transpiler rewrites IL)
var transpiler = AccessTools.Method(typeof(HotPatcher), "TranspileMethod");

// 4. Apply patch
harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

// 5. Later, unpatch
harmony.UnpatchAll("com.unitymcp.hotpatch");
```

**Transpiler Example** (replace method body):
```csharp
static IEnumerable<CodeInstruction> TranspileMethod(
    IEnumerable<CodeInstruction> instructions,
    ILGenerator generator)
{
    // Return entirely new IL instructions
    return newInstructions;
}
```

**Limitations**:
- Cannot add new fields to existing types
- Cannot change method signatures
- Cannot add new types (reflection can, but instances won't persist)
- Mono backend only (IL2CPP doesn't support dynamic method generation)
- Patches are lost on domain reload

---

### UnityHotSwap (MIT, Reference Implementation)

**Repository**: https://github.com/zapu/UnityHotSwap
**Approach**: Direct method body swap

**Technical Details**:
1. Build new assembly with Roslyn (or load modified DLL)
2. Use `Mono.Cecil` to read method body from new assembly
3. Create `DynamicMethod` with `ILGenerator`
4. Copy IL instructions from Cecil `MethodDefinition` to `DynamicMethod`
5. Force both original and new method to JIT compile: `RuntimeHelpers.PrepareMethod(handle)`
6. Insert jump code "gadget" at original method's native code address to redirect to new code

**Key Code** (simplified):
```csharp
// Get method handles
RuntimeMethodHandle originalHandle = originalMethod.MethodHandle;
RuntimeMethodHandle newHandle = dynamicMethod.MethodHandle;

// Force JIT compilation
RuntimeHelpers.PrepareMethod(originalHandle);
RuntimeHelpers.PrepareMethod(newHandle);

// Get native code pointers
IntPtr originalPtr = originalHandle.GetFunctionPointer();
IntPtr newPtr = newHandle.GetFunctionPointer();

// Redirect: write jump instruction at originalPtr → newPtr
unsafe {
    byte* ptr = (byte*)originalPtr.ToPointer();
    // x64: mov rax, newPtr; jmp rax
    *ptr = 0x48; *(ptr+1) = 0xB8; // mov rax, imm64
    *(long*)(ptr+2) = newPtr.ToInt64();
    *(ptr+10) = 0xFF; *(ptr+11) = 0xE0; // jmp rax
}
```

**Pros**: No dependencies, works at lowest level
**Cons**: Architecture-specific (x86/x64/ARM), fragile, no IL2CPP support

---

### Mono Internal API (Most Powerful, Most Fragile)

**Approach**: Use Mono's internal `mono_method_set_header` to replace IL body

```csharp
[DllImport("__Internal")]
static extern void mono_method_set_header(IntPtr method, IntPtr header);
```

**Pros**: Can replace any method body, official Mono API
**Cons**: Only works on Mono backend, requires P/Invoke to internal symbols, may not be exposed in Unity's Mono build

---

## Our Recommended Approach

### Use Harmony as Primary, with Roslyn for Diffing

**Why Harmony**:
- Battle-tested in thousands of Unity mods
- MIT license (can bundle freely)
- Clean API - patches are reversible
- Handles edge cases (virtual methods, generic methods, constructors)
- ~200KB footprint
- Active maintenance

**Why NOT UnityHotSwap approach**:
- Architecture-specific native code patching
- Much more fragile
- No built-in support for virtual/interface methods

### Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    hot_patch MCP Tool                        │
│                                                             │
│  Input: script_path, new_source_code                        │
│                                                             │
│  1. Check: Is Play Mode active?                             │
│     └─ NO → Return error, suggest manage_script instead     │
│                                                             │
│  2. Parse old source (from disk) with Roslyn SyntaxTree     │
│  3. Parse new source with Roslyn SyntaxTree                 │
│  4. Diff at method level:                                   │
│     - Find methods with changed bodies                       │
│     - Detect new methods (can add)                          │
│     - Detect new fields/types (cannot patch)                │
│                                                             │
│  5. For each changed method:                                │
│     a. Find MethodInfo via reflection in loaded assembly    │
│     b. Compile new method body to IL using Roslyn           │
│     c. Apply Harmony transpiler patch                       │
│     d. Record patch for potential rollback                  │
│                                                             │
│  6. Save new source to disk (for next domain reload)        │
│                                                             │
│  7. Return:                                                 │
│     - methods_patched: ["Update", "TakeDamage"]             │
│     - methods_skipped: [] (with reasons)                    │
│     - requires_recompile: false                             │
│     - warnings: ["New field '_armor' detected, ...]         │
│                                                             │
│  Rollback: Call with action="rollback" to unpatch all       │
└─────────────────────────────────────────────────────────────┘
```

### Simplified MVP (without Roslyn diffing)

If we want to ship faster, the MVP can skip Roslyn diffing:

```
1. Agent provides: method_name, class_name, new_method_body
2. Find method via reflection
3. Compile new body with Roslyn (or DynamicMethod + ILGenerator)
4. Apply Harmony patch
5. Return result
```

This is simpler but requires the agent to specify exactly which method to patch.

---

## Roslyn Dependency Considerations

### Option A: Full Roslyn (best experience)
- Add `Microsoft.CodeAnalysis.CSharp` NuGet (~5MB)
- Can parse/diff/compile C# at runtime
- Already optional in our `validate_script_advanced` tool
- Guarded by `#if USE_ROSLYN` preprocessor define

### Option B: Without Roslyn (lighter weight)
- Use regex/text diff to detect changed methods
- Compile replacement using `CSharpCodeProvider` (older, limited)
- Or require agent to specify method name explicitly
- Less magic, but fewer dependencies

### Option C: Hybrid (recommended)
- Use Roslyn for diffing when available (`USE_ROSLYN` defined)
- Fall back to explicit method name when Roslyn not available
- hot_patch works either way, just smarter with Roslyn

---

## IL2CPP Considerations

**IL2CPP does NOT support**:
- `System.Reflection.Emit` (no DynamicMethod)
- `System.CodeDom.Compiler`
- Runtime IL generation of any kind

**Therefore**: hot_patch only works with Mono scripting backend. This is fine because:
- Editor always uses Mono
- Play Mode in editor always uses Mono
- Only standalone builds can use IL2CPP
- hot_patch is a development tool, not a runtime feature

**We should**: Document this clearly and check `#if !ENABLE_IL2CPP` or `PlayerSettings.GetScriptingBackend()`.

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Harmony patch crashes Unity | High | Wrap in try/catch, provide rollback |
| Patched method has wrong locals | Medium | Roslyn compilation handles local variables |
| Mono internals change in Unity 7 | Low | Harmony abstracts this away |
| IL2CPP users confused | Low | Clear error message + docs |
| Complex generics fail | Medium | Document limitations, fall back to recompile |
| Thread safety during patch | Medium | Dispatch to main thread via EditorApplication.update |

---

## Testing Plan

### Unit Tests
1. Patch a simple static method → verify new behavior
2. Patch a MonoBehaviour.Update → verify new behavior in play mode
3. Patch a virtual method → verify override still works
4. Attempt to patch with new field → verify error message
5. Rollback patch → verify original behavior restored
6. Patch during play mode, exit play mode → verify clean state

### Integration Tests
1. Full workflow: manage_script → hot_patch → verify in console
2. Multiple patches in sequence → verify all applied
3. Patch → screenshot → verify visual change
4. Patch → crash → verify Unity doesn't hang

---

## References

- [Harmony Documentation](https://harmony.pardeike.net/articles/intro.html)
- [Harmony GitHub](https://github.com/pardeike/Harmony) - MIT License
- [UnityHotSwap](https://github.com/zapu/UnityHotSwap) - Method swapping reference
- [Unity Hotswapping Notes](https://gist.github.com/cobbpg/a74c8a5359554eb3daa5) - Technical notes
- [Hot Reload for Unity](https://hotreload.net) - Commercial reference ($50)
- [Mono Method Patching](https://github.com/dotnet/runtime/issues/65663) - .NET runtime discussion
- [Roslyn Scripting API](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/) - C# compilation at runtime
