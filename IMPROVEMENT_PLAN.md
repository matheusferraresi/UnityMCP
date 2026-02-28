# UnixxtyMCP — Improvement Plan (from BattleYa Field Testing)

## Context

These improvements were identified during a full Phase 0.3 polish session on BattleYa (Pangya x Gunbound artillery game). Claude Code used UnixxtyMCP extensively for scene setup, compilation, play mode testing, and ScriptableObject tuning. The issues below are real friction points encountered during production use — not hypotheticals.

**Source repo**: `D:\GameDev\UnityMCP`
**Package**: `Package/Editor/Tools/`
**Test project**: `D:\GameDev\BattleYa`

---

## Improvement 1: `vision_capture` — Add `output_path` parameter

**File**: `Package/Editor/Tools/VisionCapture.cs`
**Class**: `VisionCapture` — method `Capture(view, width, height)`

### Problem
`vision_capture` returns the screenshot as inline base64 PNG. For a 960x540 capture this is ~200KB+ of text, which floods the AI assistant's context window and can cause buffer overflow / context degradation. In practice, `scene_screenshot` (which saves to disk and returns a file path) is always preferred.

### Solution
Add an optional `output_path` parameter. When provided, save the PNG to disk and return the file path instead of base64. When omitted, keep current base64 behavior for backwards compatibility.

```csharp
[MCPTool("vision_capture", "Capture Game View or Scene View screenshot as base64 PNG for AI vision analysis. Use output_path to save to disk instead.", Category = "Scene", ReadOnlyHint = true)]
public static object Capture(
    [MCPParam("view", "Which view to capture", Required = false, Enum = new[] { "game", "scene", "both" })] string view = "game",
    [MCPParam("width", "Capture width in pixels", Required = false, Minimum = 64, Maximum = 1920)] int width = 960,
    [MCPParam("height", "Capture height in pixels", Required = false, Minimum = 0, Maximum = 1080)] int height = 540,
    [MCPParam("output_path", "If provided, save PNG to this path and return file path instead of base64. Relative paths resolve from project root.", Required = false)] string outputPath = null)
```

When `outputPath` is non-null:
1. Resolve relative paths against `Application.dataPath + "/.."`
2. Ensure parent directory exists (`Directory.CreateDirectory`)
3. Write bytes via `File.WriteAllBytes(resolvedPath, pngBytes)`
4. Return `new { success = true, path = resolvedPath, view = view, width = texture.width, height = texture.height }` instead of the base64 data

### Verification
1. Call `vision_capture` with no `output_path` → should return base64 as before (backwards compatible)
2. Call `vision_capture` with `output_path = "Screenshots/test.png"` → should save file and return path
3. Call with absolute path → should work
4. Call with invalid path → should return clear error

---

## Improvement 2: `playmode_exit` — Wait for domain reload

**File**: `Package/Editor/Tools/PlayModeTools.cs`
**Class**: `PlayModeTools` — method `Exit()`

### Problem
After `playmode_exit` returns success, Unity 6 triggers a domain reload that takes ~500ms-2s. Any MCP call made during this window gets an `InvalidOperationException: Request interrupted by Unity domain reload`. The caller has no way to know when it's safe to make the next call.

Currently `Exit()` just does:
```csharp
EditorApplication.isPlaying = false;
return new { success = true };
```

### Solution
Add an optional `wait_for_reload` parameter (default `true`). When true, use `EditorApplication.delayCall` or poll `EditorApplication.isCompiling` / `EditorApplication.isPlaying` to wait until the domain reload completes before returning the response.

Implementation approach — use the async job pattern already established by `compile_and_watch`:

```csharp
[MCPTool("playmode_exit", "Exit play mode", Category = "Editor", DestructiveHint = true)]
public static object Exit(
    [MCPParam("wait_for_reload", "Wait for Unity domain reload to complete before returning (default true). Set false for fire-and-forget.", Required = false)] bool waitForReload = true)
```

**Option A (simpler)**: Poll-based with timeout
- After setting `isPlaying = false`, enter a polling loop (on main thread via `MainThreadDispatcher`) checking `!EditorApplication.isPlaying && !EditorApplication.isCompiling`
- Timeout after 10 seconds with a warning
- Return `{ success: true, waited: true, reload_time_ms: elapsed }`

**Option B (job-based, consistent with compile_and_watch)**:
- Return a job ID immediately
- Caller polls with `playmode_exit(action: "get_job", job_id: "...")`
- Job completes when `EditorApplication.isPlaying == false && !EditorApplication.isCompiling`

Option A is recommended — simpler for callers and the wait is short enough to block.

### Verification
1. Enter play mode via `playmode_enter`
2. Call `playmode_exit` → should return only after domain reload completes
3. Immediately call any other tool (e.g., `scene_get_hierarchy`) → should succeed without error
4. Call `playmode_exit(wait_for_reload=false)` → should return immediately (fire-and-forget)

---

## Improvement 3: `recompile_scripts` — Document preferred alternative or add job tracking

**File**: `Package/Editor/Tools/RecompileScripts.cs`
**Class**: `RecompileScripts` — method `Execute(returnLogs, logLimit)`

### Problem
`recompile_scripts` triggers compilation and immediately returns whatever logs exist. But compilation is async — the logs returned are often from the *previous* compilation, not the one just triggered. The caller has no way to know when the triggered compilation finishes or if it succeeded.

Meanwhile `compile_and_watch` (in `CompileWatch.cs`) solves this perfectly with job tracking. But nothing tells callers to prefer it.

### Solution (pick one or both)

**Option A — Documentation only (minimal change)**:
Update the tool description to recommend `compile_and_watch`:

```csharp
[MCPTool("recompile_scripts",
    "Force Unity to recompile all scripts and return recent compilation logs. " +
    "NOTE: Compilation is async - returned logs may be from a previous compilation. " +
    "For reliable compilation tracking, use compile_and_watch instead.",
    Category = "Editor", DestructiveHint = true)]
```

**Option B — Add job tracking to recompile_scripts too**:
Add an optional `wait` parameter that, when true, uses the same `CompileJobManager` from `CompileWatch.cs` to wait for completion before returning. This would make both tools equally reliable.

Option A is recommended — it's simple and avoids duplicating logic.

### Verification
1. Check that tool description shows the recommendation in MCP tool listings
2. Callers who read the description should naturally gravitate to `compile_and_watch`

---

## Improvement 4: `scene_screenshot` vs `vision_capture` — Clarify in descriptions

**Files**: Both `VisionCapture.cs` and wherever `scene_screenshot` is defined

### Problem
Two tools capture screenshots with different return formats. AI assistants don't know which to pick without trial and error.

### Solution
Update both tool descriptions to cross-reference each other:

- `vision_capture`: Add "Returns base64 by default (large). Use output_path to save to disk, or use scene_screenshot for file-based capture."
- `scene_screenshot`: Add "Saves to disk and returns file path. For inline base64 (e.g., for direct AI vision analysis), use vision_capture."

---

## Improvement 5: Better error context on domain reload interruptions

**File**: `Package/Editor/Core/MCPServer.cs` or `MCPProxy.cs` (wherever HTTP responses are sent)

### Problem
When a domain reload interrupts an in-flight MCP request, the caller gets a raw `InvalidOperationException` with no guidance. This happened multiple times during our session.

### Solution
Add a try-catch around tool invocation that detects domain-reload-related exceptions and returns a structured error with recovery guidance:

```json
{
  "error": {
    "code": -32000,
    "message": "Request interrupted by Unity domain reload",
    "data": {
      "recoverable": true,
      "suggestion": "Wait 1-2 seconds and retry. Domain reloads occur after play mode changes and script recompilation."
    }
  }
}
```

This helps AI assistants auto-recover instead of treating it as a fatal error.

### Verification
1. Trigger a domain reload (enter/exit play mode)
2. Immediately send an MCP request during the reload
3. Verify the structured error response with recovery guidance

---

## Summary

| # | Tool | Change | Effort | Impact |
|---|------|--------|--------|--------|
| 1 | `vision_capture` | Add `output_path` param | Medium | High — eliminates context overflow |
| 2 | `playmode_exit` | Wait for domain reload | Medium | High — eliminates most common error |
| 3 | `recompile_scripts` | Update description | Trivial | Low — documentation only |
| 4 | Both screenshot tools | Cross-reference descriptions | Trivial | Low — better discoverability |
| 5 | MCP server | Structured domain reload errors | Medium | Medium — better error recovery |

**Recommended implementation order**: 3 → 4 → 1 → 2 → 5 (trivial wins first, then high-impact changes)
