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

## Summary

| # | Issue | Priority | Effort | Category |
|---|-------|----------|--------|----------|
| 12 | compile_and_watch misses MenuItem registration | HIGH | Small | Compilation |
| 13 | simulate_input silent fail without focus | HIGH | Small | Input |
| 14 | Tool naming inconsistency | MEDIUM | Medium | DX/Discovery |
| 15 | scene_screenshot async with no completion | MEDIUM | Small | Screenshot |
| 16 | No batch delete for GameObjects | MEDIUM | Small | Scene Mgmt |
| 17 | No "delete all except" operation | LOW | Small | Scene Mgmt |
| 18 | console_read persistence across reload | LOW | Trivial (doc) | Logging |

### Priority Clusters

**Immediate wins (Issues 12, 13)**: Both are HIGH priority, small effort, and directly block common AI workflows. Issue 13 (simulate_input focus) is especially impactful — silent success with no effect is the most confusing failure mode possible.

**Scene management (Issues 16, 17)**: Batch operations would significantly speed up scene restructuring workflows. These come up whenever a project pivots or refactors scene layout.

**DX polish (Issues 14, 15, 18)**: Lower urgency but improve the overall experience for both AI and human users.

### Cross-Reference with Phase 1 Issues

Issues 4 (`wait_for_ready`) and 7 (`execute_menu_item` during compile) from Phase 1 are related to Issue 12 here. The root cause is the same: compilation completes but Unity's attribute/menu registration lags behind. A combined fix (auto-refresh after compile + better error messages) would address all three.
