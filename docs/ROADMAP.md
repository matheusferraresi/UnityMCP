# UnixxtyMCP Roadmap

**Last Updated**: February 2026
**Current Version**: 52 tools + 28 resources
**Goal**: Build the most powerful Unixxty MCP with killer features no competitor has

---

## Current State (v1.0 - Shipped)

### 52 Tools Across 13 Categories

| Category | Tools | Count |
|----------|-------|-------|
| Animation | `animation_controller`, `animation_clip` | 2 |
| Asset Management | `asset_manage`, `manage_script`, `manage_scriptable_object`, `manage_shader`, `manage_texture`, `manage_material`, `prefab_manage` | 7 |
| Scene Management | `scene_create`, `scene_load`, `scene_save`, `scene_get_active`, `scene_get_hierarchy`, `scene_screenshot` | 6 |
| GameObject & Components | `gameobject_manage`, `gameobject_find`, `component_manage` | 3 |
| VFX | `manage_vfx` | 1 |
| Console | `console_read`, `console_write` | 2 |
| Play Mode | `playmode_enter`, `playmode_exit`, `playmode_pause`, `playmode_step` | 4 |
| Build | `build_start`, `build_get_job` | 2 |
| Testing | `tests_run`, `tests_get_job` | 2 |
| Profiler | `profiler_start`, `profiler_stop`, `profiler_get_job` | 3 |
| UIToolkit | `uitoolkit_query`, `uitoolkit_get_styles`, `uitoolkit_click`, `uitoolkit_get_value`, `uitoolkit_set_value`, `uitoolkit_navigate` | 6 |
| Editor Control | `manage_editor`, `selection_get`, `selection_set`, `execute_menu_item`, `unity_refresh`, `recompile_scripts` | 6 |
| Package & Utility | `package_manage`, `batch_execute`, `search_tools`, `validate_script_advanced` | 4 |
| Debug/Test | `test_echo`, `test_add`, `test_unity_info`, `test_list_scenes` | 4 |

### 28 Built-in Resources
Scene state, project settings, console output, hierarchy, and more.

---

## Phase 1: Killer Features (Next Sprint)

### 1. `smart_edit` - Diff-Based Script Editing
**Priority**: Very High | **Effort**: 1 day | **File**: `Package/Editor/Tools/SmartEdit.cs`

**What**: Apply targeted edits to scripts using search/replace or line ranges, instead of replacing entire files. Validates with RoslynValidator before saving.

**Why**: The #1 pain point across ALL competing MCPs. Agents replace entire scripts and introduce bugs. CoplayDev's `apply_text_edits` exists but has known bugs (duplicate non-idempotent edits on domain reload).

**Actions**:
- `search_replace` - Find text and replace (supports regex)
- `insert_at_line` - Insert code at specific line number
- `delete_lines` - Remove line range
- `replace_lines` - Replace line range with new content
- `patch` - Apply unified diff format

**Flow**:
```
1. Read current script content
2. Apply edit operation
3. Validate result with RoslynValidator (basic + standard level)
4. If valid → write file + AssetDatabase.ImportAsset(ForceUpdate)
5. If invalid → return validation errors WITHOUT saving
6. Return: success, old/new line count, validation warnings
```

---

### 2. `compile_and_watch` - Async Compilation Pipeline
**Priority**: Very High | **Effort**: 1 day | **File**: `Package/Editor/Tools/CompileWatch.cs`

**What**: Async job that validates → triggers compilation → watches for errors → reports structured results. Agent polls for status like `tests_get_job`.

**Why**: Current flow is: edit → recompile_scripts (fire-and-forget) → manually check console. No structured feedback loop.

**Actions**:
- `start` - Trigger compilation, return job_id
- `get_job` - Poll job status (compiling/succeeded/failed)

**Job Result**:
```json
{
  "job_id": "compile_abc123",
  "status": "failed",
  "duration_ms": 2340,
  "errors": [
    {
      "file": "Assets/_Project/Scripts/Player/PlayerInput.cs",
      "line": 45,
      "column": 12,
      "code": "CS1061",
      "message": "'Transform' does not contain a definition for 'positon'",
      "severity": "error"
    }
  ],
  "warnings": [...],
  "assembly_count": 12
}
```

**Implementation**:
- Use `SessionState` persistence pattern (proven in `TestJobManager`)
- Subscribe to `CompilationPipeline.assemblyCompilationFinished`
- Collect errors via `LogEntries` reflection API (already built in `ConsoleErrors.cs`)
- Handle domain reload gracefully (native proxy keeps connection alive)

---

### 3. `hot_patch` - Play Mode Method Hot Reload (THE Flagship)
**Priority**: Killer | **Effort**: 3-5 days | **Files**: `Package/Editor/Tools/HotPatch.cs`, `Package/Editor/Utilities/MethodPatcher.cs`

**What**: When an agent modifies a method body during Play Mode, patch it in-memory without domain reload. The agent can edit → see result → iterate, all while the game is running.

**Why**: NO MCP has this. "Hot Reload for Unity" ($50) does it standalone, but isn't MCP-integrated. This would be the #1 reason to choose our MCP.

**Technical Approach**:

Use **[Harmony](https://github.com/pardeike/Harmony)** (MIT license, v2.3+, ~200KB DLL):
```csharp
var harmony = new Harmony("com.unixxtymcp.hotpatch");
var original = targetType.GetMethod("MethodName");
var replacement = BuildReplacementMethod(newSourceCode);
harmony.Patch(original, transpiler: new HarmonyMethod(replacement));
```

**Alternative Approach**: Direct Mono method body swap via `mono_method_set_header` (more powerful but Mono-specific, breaks on IL2CPP).

**Flow**:
```
Agent calls hot_patch with script path + new source code
    ↓
Is Unity in Play Mode?
├─ NO → Return error: "hot_patch only works in Play Mode. Use manage_script + recompile_scripts instead."
└─ YES →
    ↓
    Parse old script (from loaded assembly) and new script (from source)
    ↓
    Diff at method level using Roslyn SyntaxTree comparison
    ↓
    Classify changes:
    ├─ Body-only changes → CAN hot patch
    ├─ New methods → CAN hot patch (add to type)
    ├─ New fields/properties → CANNOT hot patch
    ├─ Signature changes → CANNOT hot patch
    └─ New types → CANNOT hot patch
    ↓
    For patchable methods:
    ├─ Compile new method body to IL
    ├─ Use Harmony to swap method implementation
    └─ Return: "Patched 2 methods in 15ms"
    ↓
    For non-patchable changes:
    └─ Return: "Cannot hot patch: new field 'health' added. Exit play mode to recompile."
```

**Dependencies**:
- `0Harmony.dll` (~200KB) bundled in `Package/Plugins/`
- Optional: `Microsoft.CodeAnalysis.CSharp` for Roslyn diffing (or use basic text diff)

**Limitations** (clearly reported to agent):
- Method bodies only (not new types, fields, or signatures)
- No static constructor changes
- No attribute changes
- Patches revert on domain reload (exiting play mode)
- Mono backend only (not IL2CPP)

**Testing**:
1. Enter play mode with a simple MonoBehaviour
2. Call `hot_patch` changing a `Debug.Log` message in `Update()`
3. Verify new message appears in console immediately
4. Exit play mode → verify original code is restored

---

### 4. `vision_capture` - Multimodal Scene Screenshots
**Priority**: High | **Effort**: 0.5 day | **File**: Extend `Package/Editor/Tools/SceneScreenshot.cs`

**What**: Capture Game View, Scene View, or Inspector as base64 PNG inline in the MCP response. Vision-capable models can "see" the game.

**Why**: Union MCP's #1 differentiator. No other free MCP has this. Critical for debugging visual issues.

**New Parameters**:
- `view`: `game` | `scene` | `both` (default: `game`)
- `format`: `file` (existing) | `base64` (new, default)
- `resolution`: `low` (320px) | `medium` (640px) | `high` (1280px)

**Implementation**:
```csharp
// Game View capture
var texture = ScreenCapture.CaptureScreenshotAsTexture();
byte[] png = texture.EncodeToPNG();
string base64 = Convert.ToBase64String(png);

// Scene View capture
var sceneView = SceneView.lastActiveSceneView;
sceneView.camera.Render();
var rt = sceneView.camera.targetTexture;
// ReadPixels from RenderTexture
```

**Return Format**:
```json
{
  "success": true,
  "images": [
    {
      "view": "game",
      "width": 640,
      "height": 480,
      "base64": "iVBORw0KGgo..."
    }
  ]
}
```

---

### 5. `debug_play` - Automated Play Mode Testing
**Priority**: High | **Effort**: 1-2 days | **File**: `Package/Editor/Tools/DebugPlay.cs`

**What**: Enter play mode, wait for a condition (time, console message, object exists), capture state, optionally exit. Returns structured snapshot.

**Why**: Closes the agent's feedback loop: edit → test → verify → iterate. CoplayDev's roadmap lists "LLMs to interact with user-created games" as long-term - we can do it now.

**Parameters**:
- `action`: `start` | `get_status` | `stop`
- `wait_seconds`: How long to run (default: 3)
- `wait_for_log`: Stop when this message appears in console
- `capture_screenshot`: Take screenshot on completion (default: true)
- `capture_console`: Return console output (default: true)
- `inspect_objects`: List of GameObject paths to capture component values from

**Return**:
```json
{
  "job_id": "debug_xyz",
  "status": "completed",
  "duration_ms": 3012,
  "screenshot_base64": "iVBORw0KGgo...",
  "console": [
    {"type": "Log", "message": "Player spawned at (0, 1, 0)"},
    {"type": "Warning", "message": "No audio listener found"}
  ],
  "inspected_objects": {
    "Player": {
      "Transform.position": "(0, 1, 0)",
      "PlayerController.health": 100
    }
  }
}
```

---

### 6. `type_inspector` - Deep Type Reflection
**Priority**: Medium (quick win) | **Effort**: 0.5 day | **File**: `Package/Editor/Tools/TypeInspector.cs`

**What**: Get full C# type information - fields, properties, methods, attributes, inheritance chain.

**Why**: Agents need to know what fields a component has before setting them. IvanMurzak and Union both have this.

**Parameters**:
- `type_name`: Fully qualified type name (e.g., `UnityEngine.Transform`)
- `include_methods`: Include method signatures (default: false)
- `include_inherited`: Include inherited members (default: true)
- `serialized_only`: Only show Unity-serializable fields (default: false)

**Return**:
```json
{
  "type": "PlayerController",
  "namespace": "Aethernals",
  "base_type": "MonoBehaviour",
  "interfaces": [],
  "fields": [
    {"name": "_moveSpeed", "type": "float", "access": "private", "serialized": true, "attributes": ["SerializeField"]},
    {"name": "Health", "type": "int", "access": "public", "serialized": true}
  ],
  "properties": [...],
  "methods": [...]
}
```

---

### 7. `console_clear` - Clear Console
**Priority**: Medium (quick win) | **Effort**: 30 min | **File**: Extend `Package/Editor/Tools/ConsoleWrite.cs`

**What**: Add `clear` action to `console_write` tool.

**How**: `LogEntries.Clear()` via reflection (same pattern as `ConsoleErrors.cs`).

---

## Phase 2: Complete Package

### 8. `manage_ugui` - Runtime UI Management
**Priority**: High | **Effort**: 2 days | **File**: `Package/Editor/Tools/ManageUGUI.cs`

**Actions**:
- `create_canvas` - Create Canvas with EventSystem
- `add_element` - Add Button, Text, Image, Panel, ScrollView, InputField, Slider, Toggle, Dropdown
- `modify_rect` - Set RectTransform (anchors, position, size, pivot)
- `add_layout` - Add VerticalLayoutGroup, HorizontalLayoutGroup, GridLayoutGroup
- `set_text` - Set TextMeshProUGUI text and styling
- `set_image` - Set Image sprite, color, type
- `get_hierarchy` - Get Canvas hierarchy with RectTransform data

---

### 9. `file_import` - External Asset Import
**Priority**: Medium | **Effort**: 0.5 day | **File**: `Package/Editor/Tools/FileImport.cs`

**What**: Copy files from anywhere on disk into Assets/ and configure import settings.

**Parameters**:
- `source_path`: Absolute path on disk
- `destination`: Relative path in Assets (e.g., `Assets/Textures/player.png`)
- `import_settings`: Optional overrides (texture type, compression, etc.)

---

### 10. `server_instructions` - Custom AI Instructions
**Priority**: Medium | **Effort**: 0.5 day

**What**: Per-project instructions sent to AI on MCP handshake.

**Implementation**: Read from `Assets/UnixxtyMCPInstructions.md` or EditorPrefs. Include in MCP `initialize` response per spec.

---

## Phase 3: Polish & Differentiation

### 11. `scene_diff` - Scene Change Tracking
Serialize hierarchy to JSON, diff against previous snapshot. Agents know what changed.

### 12. `undo_redo` - Undo System Control
Expose `Undo.PerformUndo()`, `Undo.PerformRedo()`, `Undo.GetCurrentGroupName()`.

### 13. `asset_preview` - Asset Thumbnails as Base64
`AssetPreview.GetAssetPreview()` → PNG → base64. Agents can "see" assets.

### 14. `physics_simulate` - Edit-Mode Physics
`Physics.Simulate(deltaTime)` in edit mode. Verify physics setups without play mode.

### 15. `lighting_bake` - Lightmap Control
`Lightmapping.BakeAsync()`, poll `Lightmapping.isRunning`, configure settings.

---

## Target Tool Count

| Phase | New Tools | Running Total |
|-------|-----------|---------------|
| Current | - | 52 |
| Phase 1 | 7 (smart_edit, compile_and_watch, hot_patch, vision_capture, debug_play, type_inspector, console_clear) | 59 |
| Phase 2 | 3 (manage_ugui, file_import, server_instructions) | 62 |
| Phase 3 | 5 (scene_diff, undo_redo, asset_preview, physics_simulate, lighting_bake) | 67 |

By CoplayDev's "capabilities" counting: **150+** capabilities.

---

## Success Criteria

- [ ] All Phase 1 tools compile and register in Unity 6
- [ ] `hot_patch` successfully patches a method during Play Mode
- [ ] `vision_capture` returns usable base64 screenshots
- [ ] `compile_and_watch` provides structured error feedback
- [ ] `smart_edit` catches syntax errors before saving
- [ ] `debug_play` can enter play mode, capture state, and exit
- [ ] Zero increase in external dependencies (except 0Harmony.dll for hot_patch)
- [ ] All tools survive domain reload
