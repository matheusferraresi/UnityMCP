# UnixxtyMCP Improvements Found During BattleYa Phase 1

Collected during Phase 1 multiplayer feature work (2026-02-28).
Address these after Phase 1 is complete.

---

## Issue 1: Domain Reload Resilience (HIGH PRIORITY)

**Problem**: When Claude Code writes multiple C# files, Unity auto-recompiles and enters domain reload. During this period (3-10 seconds), ALL MCP tool calls fail with:
- `"Request interrupted by Unity domain reload. Please retry."`
- `"Request processing timed out."`

This affects `compile_and_watch`, `console_read`, `execute_menu_item`, and all other tools.

**Impact**: AI assistants must blindly `sleep` and retry, which is unreliable and slow.

**Proposed Fix**: Add a `editor_state` or `is_ready` tool (or enhance existing `test_unity_info`) that:
1. Returns immediately even during domain reload (catch `AppDomainUnloadedException`)
2. Reports: `{ "ready": false, "reason": "domain_reload", "estimated_wait_ms": 3000 }`
3. Or better: make the MCP proxy queue requests and replay them after reload completes, returning the result once ready (with a longer timeout).

**Alternative**: Add auto-retry logic in MCPProxy.cs — if a request fails due to domain reload, hold the HTTP connection open and retry internally after reload completes (up to 15s timeout).

---

## Issue 2: scene_save Path Duplication (LOW PRIORITY)

**Problem**: `scene_save` returns a doubled path in the response:
```json
{
  "path": "Assets/_Project/Scenes/BattleScene.unity/BattleScene.unity"
}
```

The actual scene is at `Assets/_Project/Scenes/BattleScene.unity` (no duplication). The response path has `/BattleScene.unity` appended twice.

**Impact**: Cosmetic — the save works fine, but the returned path is misleading.

**Proposed Fix**: In `ManageScene.cs` save handler, check if the scene path already ends with `.unity` before appending the scene name.

---

## Issue 3: compile_and_watch Should Auto-Wait After External File Changes (MEDIUM)

**Problem**: When files are changed outside Unity (by Claude Code's Write/Edit tools), Unity detects them on focus and triggers recompilation automatically. Calling `compile_and_watch` at this point often hits domain reload.

**Impact**: The recommended workflow (`write files → compile_and_watch → check errors`) is fragile because Unity is already compiling before we call the tool.

**Proposed Fix**:
- Option A: `compile_and_watch start` should detect "compilation already in progress" and attach to that compilation job instead of triggering a new one.
- Option B: Add a `wait_for_ready` parameter that blocks until Unity finishes any in-progress compilation before starting a new one.

---

## Issue 4: No Tool for "Wait Until Unity Is Ready" (MEDIUM)

**Problem**: After `playmode_exit`, file changes, or asset imports, Unity enters domain reload. There's no reliable way to know when it's safe to call tools again.

**Current workaround**: `playmode_exit` now returns `domain_reload_pending: true, recommended_delay_ms: 2000` (from our earlier MCP improvement), but this is just an estimate. The actual reload time varies (2-10s depending on project size).

**Proposed Fix**: Add a `wait_for_ready` tool or parameter:
```
mcp__unixxty__wait_for_ready { timeout_ms: 15000 }
→ { ready: true, waited_ms: 3200 }
```

This would internally poll `EditorApplication.isCompiling` and `EditorApplication.isUpdating` until both are false, then return.

---

## Issue 5: console_read Should Filter by Timestamp (LOW PRIORITY)

**Problem**: After entering play mode, `console_read` returns errors from before play mode (e.g., old package resolution errors). There's no way to say "show me only errors since I entered play mode."

**Current workaround**: Use `console_write action:clear` before play mode to reset, but this loses useful context.

**Proposed Fix**: Add `since_timestamp` or `since_entry_id` parameter to `console_read`:
```
console_read { types: "error", since_entry_id: 42 }
```

---

## Summary

| # | Issue | Priority | Effort |
|---|-------|----------|--------|
| 1 | Domain reload resilience | HIGH | Medium (proxy-level retry) |
| 2 | scene_save path duplication | LOW | Trivial |
| 3 | compile_and_watch auto-attach | MEDIUM | Medium |
| 4 | wait_for_ready tool | MEDIUM | Small |
| 5 | console_read timestamp filter | LOW | Small |

Issues 1 and 4 are related — solving Issue 4 (`wait_for_ready`) would be the simplest fix, and Issue 1 (auto-retry) would be the most robust long-term solution.

---

## Issue 6: scene_screenshot Doesn't Capture Canvas UI Outside Play Mode (LOW)

**Problem**: `scene_screenshot` of LobbyScene in edit mode shows only the camera clear color (dark blue) — the Canvas UI elements are not rendered. Only visible after entering play mode.

**Impact**: Can't visually verify UI layouts without entering play mode first.

**Context**: This may be a Unity limitation (ScreenSpaceOverlay Canvas only renders at runtime), not an MCP bug. Worth investigating if Game View rendering can be forced.

**Proposed Fix**: Document this limitation in the tool description, or add a note in the response: `"note": "Canvas UI may not be visible outside Play Mode"`.

---

## Issue 7: execute_menu_item Fails Silently During Compilation (MEDIUM)

**Problem**: When calling `execute_menu_item` while Unity is still compiling (domain reload in progress), it returns:
```json
{
  "success": false,
  "error": "Failed to execute menu item: '...'. The menu item may not exist, may be disabled, or may require specific conditions to be met."
}
```

The error message is misleading — the menu item exists but Unity hasn't registered it yet because compilation isn't finished.

**Impact**: AI assistants can't distinguish "menu item doesn't exist" from "Unity is still compiling". This leads to unnecessary debugging of the menu path string.

**Proposed Fix**: Check `EditorApplication.isCompiling` before attempting menu item execution. If compiling, return:
```json
{
  "success": false,
  "error": "Unity is still compiling scripts. Retry after compilation completes.",
  "is_compiling": true
}
```

---

## Issue 8: compile_and_watch Returns Empty on Domain Reload Interrupt (LOW)

**Problem**: `compile_and_watch start` sometimes returns empty/no output when it hits a domain reload mid-request. The `job_id` is not returned, so you can't poll for status.

**Observed**: First call returned `"Request interrupted by Unity domain reload. Please retry."`, second call timed out entirely with no job_id.

**Proposed Fix**: The proxy should catch domain reload exceptions during `compile_and_watch start` and return a special response:
```json
{
  "success": true,
  "message": "Compilation already triggered by domain reload. Attaching to in-progress compilation.",
  "job_id": "auto_attached_xxx",
  "status": "compiling"
}
```

---

## Updated Summary

| # | Issue | Priority | Effort |
|---|-------|----------|--------|
| 1 | Domain reload resilience | HIGH | Medium (proxy-level retry) |
| 2 | scene_save path duplication | LOW | Trivial |
| 3 | compile_and_watch auto-attach | MEDIUM | Medium |
| 4 | wait_for_ready tool | MEDIUM | Small |
| 5 | console_read timestamp filter | LOW | Small |
| 6 | scene_screenshot no Canvas in edit mode | LOW | Trivial (doc) |
| 7 | execute_menu_item misleading error during compile | MEDIUM | Small |
| 8 | compile_and_watch empty on domain reload | LOW | Small |

---

## Issue 9: compile_and_watch Hangs Indefinitely (HIGH)

**Problem**: `compile_and_watch start` sometimes hangs and never returns, blocking the AI assistant indefinitely. The user had to manually cancel the tool call. This happened after writing a new .cs file — Unity auto-compiled, but the tool never completed.

**Impact**: HIGH — blocks the entire workflow. The assistant can't proceed until the user manually intervenes.

**Proposed Fix**: Add a hard timeout (e.g., 30s) to the compile_and_watch tool. If compilation hasn't completed within the timeout, return:
```json
{
  "success": false,
  "status": "timeout",
  "message": "Compilation did not complete within 30s. Check console_read for errors."
}
```

**Workaround**: Skip compile_and_watch entirely. Use `console_read { types: "error" }` after a brief sleep to check for compile errors instead.

---

## Updated Summary

| # | Issue | Priority | Effort |
|---|-------|----------|--------|
| 1 | Domain reload resilience | HIGH | Medium (proxy-level retry) |
| 2 | scene_save path duplication | LOW | Trivial |
| 3 | compile_and_watch auto-attach | MEDIUM | Medium |
| 4 | wait_for_ready tool | MEDIUM | Small |
| 5 | console_read timestamp filter | LOW | Small |
| 6 | scene_screenshot no Canvas in edit mode | LOW | Trivial (doc) |
| 7 | execute_menu_item misleading error during compile | MEDIUM | Small |
| 8 | compile_and_watch empty on domain reload | LOW | Small |
| 9 | compile_and_watch hangs indefinitely | HIGH | Small (add timeout) |

**Top priority cluster**: Issues 1, 3, 4, 7, 8, 9 are all domain-reload-related. A single proxy-level solution (Issue 1) would fix most of them.

---

## Issue 10: hot_patch Cannot Access Instance Members (MEDIUM)

**Problem**: `hot_patch` compiles the patch body as a static method via Roslyn, so instance fields/properties on the target MonoBehaviour (like `enabled`, `_activePlayer`, private fields) are not accessible. Attempting to reference them causes a Roslyn compilation error.

**Context**: During debugging of a MovementController input issue, tried to hot-patch `Update()` to add logging before the guard check. The patch body referenced `enabled` (a MonoBehaviour property) and `_activePlayer` (a private field), both of which failed compilation.

**Impact**: Significantly limits hot_patch usefulness for debugging MonoBehaviours — the most common use case. Had to fall back to file-based code edits + recompilation instead.

**Proposed Fix**:
- Inject the target instance as a parameter (e.g., `__instance`) that the patch body can reference, similar to Harmony's `__instance` convention
- Or: document the limitation clearly and suggest using `__instance` if Harmony prefix/postfix patches already support it
- Or: provide a `debug_log` tool that injects a simple `Debug.Log()` call at the start of a method without requiring a full Roslyn compilation

**Workaround**: Edit the source file directly with debug logging, let Unity recompile.

---

## Issue 11: hot_patch Timeout on Retry (LOW)

**Problem**: After a `hot_patch` call fails with a Roslyn compilation error, retrying with a corrected patch sometimes times out entirely with no response.

**Impact**: Low — workaround is to edit files directly.

**Proposed Fix**: Ensure hot_patch has a hard timeout (10-15s) and returns a clear error rather than hanging.

---

## Updated Summary

| # | Issue | Priority | Effort |
|---|-------|----------|--------|
| 1 | Domain reload resilience | HIGH | Medium (proxy-level retry) |
| 2 | scene_save path duplication | LOW | Trivial |
| 3 | compile_and_watch auto-attach | MEDIUM | Medium |
| 4 | wait_for_ready tool | MEDIUM | Small |
| 5 | console_read timestamp filter | LOW | Small |
| 6 | scene_screenshot no Canvas in edit mode | LOW | Trivial (doc) |
| 7 | execute_menu_item misleading error during compile | MEDIUM | Small |
| 8 | compile_and_watch empty on domain reload | LOW | Small |
| 9 | compile_and_watch hangs indefinitely | HIGH | Small (add timeout) |
| 10 | hot_patch can't access instance members | MEDIUM | Medium |
| 11 | hot_patch timeout on retry | LOW | Small |

**Top priority cluster**: Issues 1, 3, 4, 7, 8, 9 are all domain-reload-related. A single proxy-level solution (Issue 1) would fix most of them.                                                                        