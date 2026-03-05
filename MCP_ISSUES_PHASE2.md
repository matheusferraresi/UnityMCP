# UnixxtyMCP Improvements Found During BattleYa Phase 0 (Mech Combat Pivot)

Collected during Phase 0 mech combat prototype testing (2026-03-01).
Tested: 4-phase skill-shot minigame, scene setup via MenuItem, play mode input simulation, scene cleanup.

---

## Issue 12: compile_and_watch Doesn't Register New MenuItems (HIGH)

**Problem**: After `compile_and_watch` completed successfully (0 errors, assembly_count: 1), `execute_menu_item("BattleYa/Setup Mech Combat Scene")` failed with "menu not found". The new `[MenuItem]` attribute was in a freshly compiled script, but Unity hadn't registered it yet. Had to call `unity_refresh` separately to trigger a full asset reimport before the menu item appeared.

**Impact**: HIGH — breaks the expected workflow of `write code → compile → use menu item`. AI assistants waste time debugging the menu path string when the real issue is stale registration.

**Proposed Fix**:
- Option A: `compile_and_watch` should call `AssetDatabase.Refresh()` after successful compilation to ensure all new attributes (MenuItem, InitializeOnLoad, etc.) are registered.
- Option B: Add a hint in the response when new scripts are detected: `"hint": "New scripts compiled. Call unity_refresh to register new MenuItem/InitializeOnLoad attributes."`
- Option C: `execute_menu_item` should check `EditorApplication.isCompiling` AND detect recently compiled scripts, suggesting a refresh if the menu item isn't found.

**Workaround**: Always call `unity_refresh` after `compile_and_watch` when new scripts contain `[MenuItem]` attributes.

---

## Issue 13: simulate_input Silently Fails Without Game View Focus (HIGH)

**Problem**: `simulate_input key_tap space` returned `{"success": true}` but had zero effect in-game. The AttackGaugeController stayed in CrosshairXPhase ("LOCK X!") and no input was registered. The fix was calling `manage_editor focus` first — after that, `key_tap space` worked perfectly.

No error, no warning, no indication that input was being sent to a void.

**Impact**: HIGH — silent success with no effect is the worst kind of failure. AI assistants waste significant debugging time (checking code logic, re-reading scripts, trying different key formats) when the actual problem is a missing prerequisite call.

**Proposed Fix** (pick one or combine):
- **Auto-focus**: When `simulate_input` is called during Play Mode, automatically focus the Game view window before injecting input. This is almost always what the user wants.
- **Pre-check + warning**: Check if Game view has focus. If not, return: `{"success": true, "warning": "Game view may not be focused. Input was injected but may not be received by game scripts. Call manage_editor focus first."}`
- **Documentation**: At minimum, add to the tool description: "Requires Game view to be focused during Play Mode. Use manage_editor focus first."

**Workaround**: Always call `manage_editor focus` before the first `simulate_input` in a play session.

---

## Issue 14: Tool Naming Inconsistency / Discovery Friction (MEDIUM)

**Problem**: Tried `scene_hierarchy`, `scene_info` before finding the correct `scene_get_hierarchy`. The naming convention is inconsistent across tools:

| Pattern | Examples |
|---------|----------|
| `noun_verb` | `scene_save`, `scene_load`, `scene_create` |
| `noun_get_noun` | `scene_get_hierarchy`, `scene_get_active` |
| `verb_noun` | `manage_editor`, `manage_script`, `manage_material` |
| `noun_manage` | `gameobject_manage`, `component_manage` |
| `verb_verb` | `compile_and_watch`, `wait_for_ready` |

**Impact**: MEDIUM — every new MCP user (human or AI) will guess wrong tool names repeatedly. The `search_tools` tool helps but adds an extra round-trip.

**Proposed Fix**:
- Add aliases for common patterns: `scene_hierarchy` → `scene_get_hierarchy`, `gameobject_delete` → `gameobject_manage(action:delete)`
- Or: standardize on one pattern (recommend `noun_verb`: `scene_hierarchy`, `scene_save`, `gameobject_find`, `gameobject_delete`)
- At minimum: make `search_tools` suggest "did you mean X?" for close matches

---

## Issue 15: scene_screenshot Is Async With No Completion Signal (MEDIUM)

**Problem**: `scene_screenshot` returns immediately with `"isAsync": true` but provides no way to know when the file is actually written to disk. Had to blindly `sleep 1` and then check if the file exists with `ls`.

**Impact**: MEDIUM — requires unreliable sleep-based waiting. Sometimes 0.5s is enough, sometimes not.

**Proposed Fix**:
- **Option A (preferred)**: Make it synchronous. Screenshot capture is fast (<100ms for a single frame). Return after the file is written: `{"success": true, "path": "...", "file_size_bytes": 181841}`
- **Option B**: Return a `job_id` for polling via a `screenshot_get_job` action, consistent with `compile_and_watch` and `profiler_start` patterns.
- **Option C**: Add an estimated wait: `{"isAsync": true, "estimated_ms": 200}` — at least gives a hint.

**Workaround**: `sleep 1` after `scene_screenshot` before reading the file.

---

## Issue 16: No Batch Delete for GameObjects (MEDIUM)

**Problem**: Cleaning up ~10 old artillery objects from the scene required 10 separate `gameobject_manage delete` calls. Each call is a full HTTP round-trip (~100-200ms), making bulk operations tediously slow.

**Impact**: MEDIUM — scene restructuring is common during development. This came up when pivoting from artillery to mech combat — every old system needed individual deletion.

**Proposed Fix**:
- Add array support to `gameobject_manage delete`: `{"action": "delete", "name": ["TerrainRoot", "Player2", "PowerGaugeCanvas", "PowerGaugeSystem", "NetworkGameSession", "NetworkTurnBridge"]}`
- Or: add a `scene_cleanup` tool: `{"keep": ["Main Camera", "Directional Light", "EventSystem"], "delete": "all_others"}`
- Or: document that `batch_execute` supports `gameobject_manage` calls (if it does — unclear from current docs)

**Workaround**: Call `gameobject_manage delete` in a loop, one object at a time.

---

## Issue 17: No "Delete All Except" Scene Operation (LOW)

**Problem**: When rebuilding a scene (e.g., after running a MenuItem scene setup), wanted to delete all old objects except infrastructure (Camera, Light, EventSystem). Had to manually identify each object by name and delete individually.

**Related to**: Issue 16. A higher-level scene operation would solve both.

**Proposed Fix**: Add a `scene_cleanup` or `gameobject_manage` filter mode:
```json
{"action": "delete_filtered", "keep_names": ["Main Camera", "Directional Light", "EventSystem"]}
```
Or by tag: `{"action": "delete_by_tag", "tag": "EditorOnly"}`

---

## Issue 18: console_read Log Persistence Across Domain Reload (LOW)

**Problem**: Unclear whether `console_read` captures Play Mode logs after exiting play mode (which triggers domain reload). When debugging why `simulate_input` wasn't working, couldn't verify if `AttackGaugeController` had logged any debug output during the play session.

**Impact**: LOW — can work around by reading console before exiting play mode.

**Proposed Fix**:
- Verify and document: "Console logs from Play Mode are preserved after playmode_exit and domain reload"
- If they aren't preserved: add a log buffer in the native proxy that survives domain reload

---

## Issue 19: scene_get_hierarchy Response Too Large for Deep Models (HIGH)

**Problem**: Mech models imported from Sketchfab have deep hierarchies (50+ nodes). `scene_get_hierarchy` with `max_depth=5` on a scene with two such models exceeds the 262KB MCP response limit (327KB), causing a hard error. There's no way to get deep hierarchies of complex models.

**Impact**: HIGH — blocks inspection of imported 3D model hierarchies, which is critical for scene setup and debugging.

**Proposed Fix**:
- Auto-truncate when approaching response size limit (e.g., skip `componentTypes` for leaf nodes, collapse empty intermediate nodes)
- Add a `compact` parameter that strips verbose fields (componentTypes, tag, layer, isStatic) to reduce payload
- Or: add response size awareness — if result > 200KB, auto-trim deepest levels and add `"truncated_at_depth": 3`

**Workaround**: Use `max_depth=1` or `max_depth=2` and manually drill into subtrees with `parent` parameter.

---

## Issue 19a: Large Responses — save_to_file Option for Any Tool (MEDIUM)

**Problem**: Even with `compact` mode, deeply nested hierarchies or large console dumps can exceed the 262KB MCP response limit. The agent has no way to receive oversized data.

**Proposed Fix**: Add a generic `save_to_file` parameter to tools that produce large output (`scene_get_hierarchy`, `console_read`, `search_tools`). When set, the tool writes the full JSON result to a `.txt` file in the project (e.g., `Assets/_MCP_Output/hierarchy.txt`) and returns only the file path + summary stats. The agent can then use the `Read` tool to read the file line by line, paginating through arbitrarily large results.

Example response when `save_to_file` is used:
```json
{
  "success": true,
  "saved_to": "Assets/_MCP_Output/hierarchy_2026-03-01_12-30-00.txt",
  "file_size_bytes": 327000,
  "total_items": 142,
  "hint": "Use Read tool to inspect the file. Too large for MCP response."
}
```

**Benefits**:
- Works for any tool, not just hierarchy
- Agent can read specific line ranges with offset/limit
- No MCP protocol changes needed
- File persists for re-reading across tool calls

---

## Issue 20: manage_material get_info Cannot Inspect Scene Object Renderers (MEDIUM)

**Problem**: `manage_material get_info` requires `material_path` (an asset path), but there's no way to inspect what shader/properties a scene object's renderer is using. Tried `target: "Mech1/MechModel/.../Object_7"` but got error about missing `material_path`.

**Impact**: MEDIUM — debugging material issues on scene objects requires guessing the material asset path or looking in the Inspector manually.

**Proposed Fix**:
- Add `get_renderer_info` action that accepts a scene object target and returns its renderer's material names, shader, and key properties
- Or: allow `get_info` to accept `target` (scene object) as alternative to `material_path` (asset path)

**Workaround**: Find the material asset path manually and use that with `get_info`.

---

## Issue 21: console_read Truncates Multi-line Log Messages (MEDIUM)

**Problem**: `Debug.Log()` messages containing `\n` newlines are truncated to the first line in `console_read` output. Only the first line appears; the rest is silently dropped.

**Impact**: MEDIUM — developers often log multi-line diagnostics (stack traces, formatted data). Losing all but the first line makes debugging harder.

**Proposed Fix**:
- Preserve full message content including newlines in `console_read` output
- If truncation is needed for size, add `"truncated": true` flag and a `full_length` field

**Workaround**: Use individual `Debug.Log()` calls per line instead of multi-line messages.

---

## Issue 12a: compile_and_watch AssetDatabase.Refresh Infinite Loop (FIXED)

**Problem**: The Issue 12 fix (`AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport)` in `OnCompilationFinished`) triggered another compile cycle, creating an infinite loop. The other Claude agent was stuck polling `get_job` forever.

**Fix Applied**: Changed to `AssetDatabase.Refresh()` (no force flag) with `!EditorApplication.isCompiling` guard. Committed as `8237bb9`.

---

## Issue 22: compile_and_watch Auto-Refresh on CS2001 Errors (HIGH)

**Problem**: When script files are deleted externally, `compile_and_watch` reports CS2001 errors ("source file could not be found") because Unity's csproj still references the deleted files. The agent has to manually call `unity_refresh(force)` first, then re-trigger compilation — a clunky 3-step workflow.

**Impact**: HIGH — the ideal flow `delete files → compile_and_watch(start) → poll get_job → done` is broken. Agents waste time debugging stale file references.

**Fix Applied**: In `OnCompilationFinished`, if ALL errors are CS2001 and the job hasn't retried yet:
1. Set `hasRetried = true` on the job (prevents infinite loops)
2. Reset job to `Compiling` status, clear errors
3. Call `AssetDatabase.Refresh()` via `delayCall` to update csproj
4. Re-request compilation via `CompilationPipeline.RequestScriptCompilation()`
5. If retry also fails, report errors normally (real errors, not stale refs)

Added `hasRetried` field to `CompileJob` and contextual hints in `get_job` responses.

---

## Issue 23: unity_refresh wait_for_ready Hint on Unity 6+ (MEDIUM)

**Problem**: `unity_refresh(compile=request, wait_for_ready=true)` silently skips waiting on Unity 6+ due to domain reload issues, returning a vague hint that doesn't tell the agent what to do instead.

**Impact**: MEDIUM — agents call `unity_refresh` expecting it to block until compilation finishes, then proceed with stale state.

**Fix Applied**: Improved the Unity 6+ hint to explicitly recommend `compile_and_watch` as the alternative:
> "Use compile_and_watch to track compilation: call compile_and_watch(action='start'), then poll compile_and_watch(action='get_job', job_id='...') every 2-3 seconds."

---

## Issue 24: compile_and_watch get_job Domain Reload Hint (LOW)

**Problem**: When `get_job` is polled during a domain reload, MCPProxy returns a domain-reload error. The existing error message says "wait 2-3 seconds and retry" which is correct, but the `get_job` response itself has no hint about this possibility when the job is still compiling.

**Impact**: LOW — agents already handle the error, but a proactive hint makes the polling loop more predictable.

**Fix Applied**: Added `hint` field to `get_job` response:
- When `compiling`: "Still compiling. If you get a domain reload error, wait 2-3 seconds and retry."
- When `succeeded` after retry: "Succeeded after auto-retry (stale csproj references were refreshed)."
- When `failed` after retry: "Failed after auto-retry. These are real compilation errors (not stale file references)."

---

## Issue 25: No Way to Run Arbitrary Editor C# Outside Play Mode (HIGH)

**Problem**: There's no MCP tool to execute arbitrary C# code in the Unity Editor outside of Play Mode. `hot_patch` requires Play Mode (Harmony patching). `console_write` only logs. This means common editor operations like modifying Build Settings, Project Settings, or Editor Preferences require workarounds.

**Discovered when**: An agent needed to add a scene to `EditorBuildSettings.scenes` at a specific index. The ideal call would be something like:
```json
{"tool": "editor_eval", "code": "var scenes = EditorBuildSettings.scenes.ToList(); scenes.Insert(0, new EditorBuildSettingsScene(\"Assets/_Project/Scenes/MainMenuScene.unity\", true)); EditorBuildSettings.scenes = scenes.ToArray();"}
```

**Current workarounds** (all suboptimal):
1. **Edit serialized YAML directly** — fragmatic but fragile, requires knowing Unity's YAML format, no validation
2. **Create temp `[MenuItem]` script → compile → execute → delete** — works but 4+ tool calls and a full recompile for a one-liner
3. **`manage_scriptable_object`** — only works for ScriptableObjects, not ProjectSettings

**Impact**: HIGH — ProjectSettings and EditorBuildSettings modifications come up regularly (adding scenes to build, changing player settings, toggling compilation defines). Without this, agents either edit raw YAML (risky) or create throwaway scripts (slow).

**Proposed Fix**: Add an `editor_eval` (or `run_editor_code`) tool that:
- Accepts a C# code string
- Compiles it at runtime via Roslyn (already available in the plugin for `validate_script_advanced`)
- Executes it on the main thread via `EditorApplication.delayCall`
- Returns the result (or any exceptions)
- Restricted to Editor-only APIs (not available in Play Mode builds)

**Safety considerations**:
- Mark as `DestructiveHint = true`
- Log all executed code to Console for auditability
- Consider a `dry_run` mode that compiles but doesn't execute

---

## Summary

| # | Issue | Priority | Effort | Status |
|---|-------|----------|--------|--------|
| 12 | compile_and_watch misses MenuItem registration | HIGH | Small | FIXED (652b4b4) |
| 12a | compile_and_watch Refresh infinite loop | HIGH | Small | FIXED (8237bb9) |
| 13 | simulate_input silent fail without focus | HIGH | Small | FIXED (652b4b4) |
| 14 | Tool naming inconsistency / did_you_mean | MEDIUM | Medium | FIXED (652b4b4) |
| 15 | scene_screenshot async with no completion | MEDIUM | Small | FIXED (652b4b4) |
| 16 | No batch delete for GameObjects | MEDIUM | Small | FIXED (652b4b4) |
| 17 | No "delete all except" operation | LOW | Small | FIXED (652b4b4) |
| 18 | console_read persistence across reload | LOW | Trivial | VERIFIED — Play mode logs survive exit reload. Pre-play logs lost if "Clear on Play" is enabled (Unity default). |
| 19 | scene_get_hierarchy too large for deep models | HIGH | Medium | **FIXED** (compact mode + pagination + auto-save + improved docs) |
| 19a | Large responses: auto-save to file | MEDIUM | Medium | FIXED — centralized in MCPProxy.cs |
| 20 | manage_material can't inspect scene renderers | MEDIUM | Small | FIXED — added get_renderer_info action |
| 21 | console_read truncates multi-line messages | MEDIUM | Small | FIXED (e5622a1) |
| 22 | compile_and_watch CS2001 auto-refresh+retry | HIGH | Small | FIXED |
| 23 | unity_refresh wait_for_ready hint improvement | MEDIUM | Trivial | FIXED |
| 24 | compile_and_watch get_job domain reload hint | LOW | Trivial | FIXED |
| 25 | No editor_eval tool for arbitrary editor C# | HIGH | Medium | FIXED |

### Priority Clusters

**Immediate wins (Issues 12, 13)**: Both are HIGH priority, small effort, and directly block common AI workflows. Issue 13 (simulate_input focus) is especially impactful — silent success with no effect is the most confusing failure mode possible.

**Scene management (Issues 16, 17)**: Batch operations would significantly speed up scene restructuring workflows. These come up whenever a project pivots or refactors scene layout.

**DX polish (Issues 14, 15, 18)**: Lower urgency but improve the overall experience for both AI and human users.

### Cross-Reference with Phase 1 Issues

Issues 4 (`wait_for_ready`) and 7 (`execute_menu_item` during compile) from Phase 1 are related to Issue 12 here. The root cause is the same: compilation completes but Unity's attribute/menu registration lags behind. A combined fix (auto-refresh after compile + better error messages) would address all three.
