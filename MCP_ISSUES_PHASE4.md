# UnixxtyMCP Improvements Found During ArcUI Default Font System Implementation

Collected during ArcUI default font system implementation and Aethernals UITK text visibility fix (2026-03-05).
Tested: `execute_menu_item`, `compile_and_watch`, `editor_eval`, `unity_refresh`, `manage_uitoolkit`, USS editing, FontAsset creation, PanelTextSettings wiring, package asset importing.

---

## Issue 36: `execute_menu_item` Returns No Diagnostics on Failure (HIGH)

**Problem**: When `execute_menu_item` fails, the error message is a generic catch-all with no actionable information:

```json
{
  "success": false,
  "error": "Failed to execute menu item: 'ArcUI/Internal/Regenerate Default Font'. The menu item may not exist, may be disabled, or may require specific conditions to be met.",
  "menu_path": "ArcUI/Internal/Regenerate Default Font"
}
```

The menu item existed, compiled successfully (`compile_and_watch` returned 0 errors), and the same code worked when invoked via `editor_eval`. The AI agent has zero diagnostic signal — was the path wrong? Was a validation function disabling it? Did the method throw? Did a modal dialog block execution?

**Context**: While implementing a default font system for the ArcUI package, we created `ArcFontGenerator.cs` with `[MenuItem("ArcUI/Internal/Regenerate Default Font")]`. After confirming it compiled, `execute_menu_item` failed silently. We had to fall back to `editor_eval` with the full C# code inlined:

```csharp
// This worked via editor_eval but not via execute_menu_item
var ttf = AssetDatabase.LoadAssetAtPath<Font>("Packages/com.pudexxt.arcui/Runtime/Resources/ArcUI/LiberationSans.ttf");
var fa = FontAsset.CreateFontAsset(ttf, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
AssetDatabase.CreateAsset(fa, outputPath);
```

**Impact**: HIGH — every failed menu item is a black box. This is one of the most-used tools and the most frustrating when it fails. Related to Issue 31 (Phase 3) and Issue 12 (Phase 2), but those were about timing after compilation. This one the menu was fully registered and still failed.

**Proposed Fix**: Return structured diagnostics:

```json
{
  "success": false,
  "error": "Menu item found but execution failed",
  "diagnostics": {
    "found": true,
    "enabled": true,
    "exception": "NullReferenceException: ...",
    "stack_trace": "at ArcUI.Editor.ArcFontGenerator.RegenerateFont() ..."
  }
}
```

Implementation:
1. `Menu.GetMenuItems(path)` or reflection to check existence → `found: bool`
2. `EditorApplication.ValidateMenuItem(path)` → `enabled: bool`
3. Wrap `EditorApplication.ExecuteMenuItem` in try/catch → capture exception + stack trace
4. Check if the method contains `EditorUtility.DisplayDialog` calls → warn that modal dialogs may block in MCP contexts

---

## Issue 37: `manage_uitoolkit` — Add Font and Text Settings Actions (HIGH)

**Problem**: Creating a `FontAsset` or `PanelTextSettings` required writing raw C# via `editor_eval`. These are essential UITK setup tasks — the #1 reason text is invisible in new UITK projects is missing font configuration.

**Context**: During the ArcUI default font implementation, we needed to:
1. Create a Dynamic SDF FontAsset from a .ttf file
2. Verify the FontAsset was loadable via `Resources.Load`
3. Create a `PanelTextSettings` asset
4. Wire `panelSettings.textSettings = textSettings`

All four operations required `editor_eval` with raw C# code. A customer setting up UITK for the first time would hit this wall immediately.

**Impact**: HIGH — invisible text is the most common UITK setup failure. Having dedicated actions would make the fix discoverable and prevent the issue entirely.

**Proposed Fix**: Add three new actions to `manage_uitoolkit`:

| Action | Parameters | What it does |
|--------|-----------|--------------|
| `create_font_asset` | `ttf_path`, `output_path`, `sampling_pt` (default 90), `atlas_size` (default 1024), `render_mode` (default SDFAA), `population` (default Dynamic) | Create a Dynamic SDF FontAsset from a .ttf/.otf file |
| `create_text_settings` | `output_path`, `default_font_path` | Create PanelTextSettings asset with default font reference |
| `set_panel_text_settings` | `panel_settings_path`, `text_settings_path` | Wire PanelTextSettings to PanelSettings |

**Example workflow:**
```
manage_uitoolkit(action: "create_font_asset", ttf_path: "Assets/Fonts/MyFont.ttf", output_path: "Assets/Fonts/MyFont SDF.asset")
manage_uitoolkit(action: "create_text_settings", output_path: "Assets/UI/TextSettings.asset", default_font_path: "Assets/Fonts/MyFont SDF.asset")
manage_uitoolkit(action: "set_panel_text_settings", panel_settings_path: "Assets/UI/Panel.asset", text_settings_path: "Assets/UI/TextSettings.asset")
```

**Implementation hint**: `FontAsset.CreateFontAsset(font, samplingPt, padding, renderMode, atlasW, atlasH, populationMode, enableMultiAtlas)` is in `UnityEngine.TextCore.Text`. PanelTextSettings has `m_DefaultFontAsset` accessible via `SerializedObject`.

---

## Issue 38: `unity_refresh` — Timeout Too Aggressive for Force Refresh (MEDIUM)

**Problem**: Force-refreshing the asset database with `wait_for_ready: true` timed out, even though the refresh completed successfully (Unity regenerated all meta files for the new package assets).

```json
// This timed out:
unity_refresh(mode: "force", scope: "all", wait_for_ready: true)
// But the refresh DID complete — meta files were regenerated by Unity
```

**Context**: After copying a 350KB .ttf font and OFL license into the ArcUI package's `Runtime/Resources/ArcUI/` directory, we needed Unity to import them before `AssetDatabase.LoadAssetAtPath` would find them. The force refresh worked but the `wait_for_ready` timed out because importing binary assets (fonts, textures) takes longer than the default timeout.

**Impact**: MEDIUM — the refresh still works, but the timeout error is misleading. The agent can't distinguish "timed out but succeeded" from "actually failed."

**Proposed Fix**: Either:
- Add a `timeout_ms` parameter (default 30000, max 120000) so agents can specify longer waits for heavy imports
- Return a different status for timeout vs failure: `{ status: "timeout", note: "Refresh started but wait timed out. Assets may still be importing." }`
- Increase the default timeout for `mode: "force"` (force imports are inherently slower)

---

## Issue 39: `manage_uitoolkit` — Add `patch_uss` Action for USS Editing (MEDIUM)

**Problem**: USS files were edited with the generic `Edit` tool. This works but has no awareness of USS syntax — it can't match selectors, validate property names, or handle duplicate selectors.

**Context**: We needed to add `-unity-font-definition: resource("ArcUI/DefaultFont SDF")` to the `:root` block in both `arc-tokens-dark.uss` and `arc-tokens-light.uss`. With the generic `Edit` tool, we had to manually match the exact string context to insert into. A USS-aware tool would handle this semantically.

**Impact**: MEDIUM — the generic Edit tool works, but USS-aware editing prevents syntax errors and handles edge cases (adding to existing selectors, creating new ones, removing properties).

**Proposed Fix**: Add `patch_uss` action to `manage_uitoolkit`:

```
manage_uitoolkit(action: "patch_uss",
    file: "Assets/UI/Tokens/arc-tokens-dark.uss",
    selector: ":root",
    property: "-unity-font-definition",
    value: "resource(\"ArcUI/DefaultFont SDF\")")
```

Behavior:
- If selector exists: add/update the property within that selector block
- If selector doesn't exist: create the block with the property
- If property already exists in the selector: update the value
- Returns the modified USS content or a diff

---

## Issue 40: Asset Path Returns Null When File Exists But Not Imported (LOW)

**Problem**: After copying a .ttf into a package's `Resources/` directory via filesystem tools (`cp`/`Write`), `AssetDatabase.LoadAssetAtPath` returns null because Unity hasn't imported the file yet. The agent gets a null result with no hint about why.

**Context**: First attempt to create the FontAsset:
```csharp
// Returned: "ERROR: Could not load LiberationSans.ttf"
var ttf = AssetDatabase.LoadAssetAtPath<Font>("Packages/com.pudexxt.arcui/Runtime/Resources/ArcUI/LiberationSans.ttf");
```
The file existed on disk — we had just copied it. But Unity's AssetDatabase didn't know about it yet. We had to run `unity_refresh(mode: "force")` first, then retry.

**Impact**: LOW — workaround is simple (`unity_refresh` first), but the error is confusing. An agent unfamiliar with Unity's import pipeline might loop on the null result.

**Proposed Fix**: In `editor_eval` or any tool that loads assets, when an asset path returns null:
1. Check `System.IO.File.Exists(path)` on the resolved filesystem path
2. If file exists but AssetDatabase doesn't know it: return a hint: `"Asset file exists on disk but is not imported. Run unity_refresh first."`
3. If file doesn't exist either: return the current null/error

---

## Summary

| Issue | Severity | Category | Status |
|-------|----------|----------|--------|
| #36 | HIGH | `execute_menu_item` diagnostics | **Done** |
| #37 | HIGH | `manage_uitoolkit` font/text settings | **Done** |
| #38 | MEDIUM | `unity_refresh` timeout | **Done** |
| #39 | MEDIUM | `manage_uitoolkit` USS editing | **Done** |
| #40 | LOW | Asset import hint | **Done** |

### What Worked Well
- `compile_and_watch` — Reliable, structured errors, never failed across multiple compilations
- `editor_eval` — The MVP workhorse. Created FontAsset, verified Resources.Load, searched asset paths. Extremely flexible
- `unity_refresh` — Successfully force-imported new package assets (despite the timeout)
- Asset path resolution — `FindAssets("LiberationSans t:Font")` correctly found fonts in both project and package paths
- `console_read` — Confirmed no runtime errors after font system changes

### What Didn't Work
- `execute_menu_item` — Failed on a valid, compiled menu item with no diagnostic info
- `unity_refresh` with `wait_for_ready` — Timed out on a successful refresh
- First `editor_eval` attempt to load a font — null result because asset wasn't imported yet (needed `unity_refresh` first)
