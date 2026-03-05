# UnixxtyMCP Improvements Found During BattleYa UI Toolkit Adoption

Collected during UI Toolkit foundation setup and BossFightResult proof-of-concept migration (2026-03-05).
Tested: PanelSettings creation, ThemeStyleSheet assignment, UIDocument scene wiring, UXML/USS authoring, runtime panel verification, play mode validation.

---

## Issue 26: No `manage_uitoolkit` Tool — Major Gap for UI Toolkit Workflows (HIGH)

**Problem**: UI Toolkit is Unity's future UI system (uGUI is maintenance-mode), but UnixxtyMCP has no tool for it. The existing `manage_ugui` tool is irrelevant — UI Toolkit uses UIDocument + VisualElement trees, not Canvas + GameObjects.

During the session, every UI Toolkit operation required either `Write` (for UXML/USS text files) or `editor_eval` (for PanelSettings, UIDocument, SerializedObject wiring). This works for power users but is not discoverable or ergonomic.

**Impact**: HIGH — any customer building game UI in Unity 6+ will hit this wall. UI Toolkit adoption is accelerating (Football Manager 2025 shipped with it, Unity recommends it for new projects).

**Proposed Fix**: New `manage_uitoolkit` tool with these actions:

| Action | Parameters | What it does |
|--------|-----------|--------------|
| `create_panel_settings` | path, refResolution, scaleMode, match, themePath | Create PanelSettings ScriptableObject asset |
| `create_uidocument` | gameObject, panelSettingsPath, uxmlPath, sortOrder | Add UIDocument component to a GameObject |
| `list_uidocuments` | (none) | List all UIDocuments in scene with panel settings, UXML, sort order |
| `query_panel` | gameObject/name, selector, maxDepth | Query VisualElement tree of a runtime UIDocument (see Issue 27) |
| `set_element_style` | gameObject, elementName/selector, property, value | Set a style property on a VisualElement in a runtime panel |
| `preview_panel` | gameObject, show (bool) | Force-show/hide a UIDocument panel (set root display:flex/none) for screenshots |
| `assign_theme` | panelSettingsPath, themePath | Assign ThemeStyleSheet to PanelSettings |
| `inspect_uss` | ussPath | List custom properties defined, validate var() references against imported tokens |

**Example workflow with the new tool:**
```
manage_uitoolkit(action: "create_panel_settings", path: "Assets/UI/Panel.asset", refResolution: [1920,1080])
manage_uitoolkit(action: "create_uidocument", gameObject: "MyHUD", panelSettingsPath: "Assets/UI/Panel.asset", uxmlPath: "Assets/UI/HUD.uxml", sortOrder: 10)
manage_uitoolkit(action: "query_panel", name: "MyHUD", selector: ".health-bar")
```

---

## Issue 27: `uitoolkit_query` Cannot Inspect Runtime UIDocument Panels (HIGH)

**Problem**: The existing `uitoolkit_query` tool only works on EditorWindows (InspectorWindow, SceneView, etc.). It cannot inspect VisualElement trees inside runtime UIDocument panels — the exact panels that game developers create for HUDs, menus, and combat UI.

During the session, after adding BossFightResult_UITK to the scene, there was no way to verify the UXML loaded correctly, check if USS tokens resolved, or inspect computed styles on grade labels — without manually opening the UI Toolkit Debugger in Unity.

**Impact**: HIGH — runtime panel inspection is the #1 debugging workflow for UI Toolkit game development. Without it, AI assistants are blind to the UI state after entering play mode.

**Proposed Fix**: Extend `uitoolkit_query` (or add to `manage_uitoolkit`) to support runtime panels:

```
uitoolkit_query(panel: "BossFightResult_UITK", selector: ".grade-value", max_depth: 2)
→ Returns: [
    { name: "dom-grade", text: "S", classes: ["grade-value", "grade-s"], computedStyle: { color: "rgb(255,217,0)" } },
    { name: "prc-grade", text: "A", classes: ["grade-value", "grade-a"], computedStyle: { color: "rgb(102,230,102)" } },
    { name: "tec-grade", text: "B", classes: ["grade-value", "grade-b"], computedStyle: { color: "rgb(200,200,200)" } }
  ]
```

**Implementation hint**: In Unity, runtime panels are accessible via `UIDocument.rootVisualElement` on any enabled UIDocument component. Use `Object.FindObjectsByType<UIDocument>()` to enumerate all panels, then walk the VisualElement tree matching the selector.

---

## Issue 28: No Way to Preview Hidden UI Toolkit Panels (MEDIUM)

**Problem**: Game UI panels typically start hidden (`display: none`) and only show when triggered by game events (e.g., OnGameOver). `scene_screenshot` captures the Game view, but hidden panels are invisible — there's no way to preview the result screen's layout without actually completing a boss fight.

During the session, the BossFightResult panel was confirmed working only by: entering play mode → checking zero console errors → trusting the code. No visual verification was possible without triggering the game-over event.

**Impact**: MEDIUM — slows iteration significantly for any UI that isn't always visible (popups, results, modals, notifications, tooltips).

**Proposed Fix**:
- Option A: `manage_uitoolkit(action: "preview_panel", gameObject: "BossFightResult_UITK", show: true)` — temporarily force `display: Flex` on the root, take screenshot, restore original state.
- Option B: Integrate with UI Builder's preview mode — render the UXML in isolation.
- Option C: Add a `--force-show-panels` flag to `scene_screenshot` that temporarily un-hides all UIDocument roots before capture.

**Workaround**: Use `editor_eval` to temporarily set `display: Flex` on the panel, screenshot, then restore. Cumbersome but functional.

---

## Issue 29: USS Validation — Silent Failures on Undefined Custom Properties (MEDIUM)

**Problem**: USS custom properties (`var(--color-pangya)`) fail silently if the property name is misspelled or the importing chain is broken. There's no compile error, no runtime warning, and no MCP tool to validate USS references.

During the session, after creating `colors.uss` with 30+ custom properties and referencing them from `BossFightResult.uss`, there was no way to verify the references resolved correctly without visually inspecting the running UI.

**Impact**: MEDIUM — a single typo in a token name (e.g., `--color-pangyaa`) causes the style to silently fall back to the default, which could go unnoticed until the screen is actually shown in-game.

**Proposed Fix**:
- `manage_uitoolkit(action: "inspect_uss", path: "Assets/UI/USS/BossFightResult.uss")` → parses the USS file, extracts all `var()` references, cross-references against imported sheets, reports any unresolved properties.
- Output: `{ "resolved": ["--color-victory", "--color-defeat", ...], "unresolved": ["--color-pangyaa"], "imports": ["AethernalsTheme.tss"] }`

---

## Issue 30: Scaffold / Template Generation for UI Toolkit Screens (LOW)

**Problem**: Every UI Toolkit screen follows a predictable 3-file pattern:
1. `UXML/ScreenName.uxml` — layout with named elements
2. `USS/ScreenName.uss` — styling importing theme tokens
3. `Controllers/ScreenNameController.cs` — MonoBehaviour extending UIScreenBase with `QueryElements()`

During the session, all three files were created manually with `Write`. The UXML always starts with the same boilerplate (namespace, style import), the USS always imports the theme, and the controller always follows the same `QueryElements()` + event subscription pattern.

**Impact**: LOW (it works, just tedious) — but for an MCP plugin selling to customers, reducing boilerplate is a selling point.

**Proposed Fix**: A scaffold action that generates all 3 files from a description:

```
manage_uitoolkit(action: "scaffold_screen",
    name: "MainMenu",
    template: "menu",
    base_class: "UIScreenBase",
    namespace: "BattleYa.UI",
    elements: [
        { name: "start-btn", type: "Button", text: "Start Game" },
        { name: "title", type: "Label", text: "Aethernals" },
        { name: "options-panel", type: "VisualElement", class: "panel hidden" }
    ],
    theme_path: "Assets/_Project/UI/Themes/AethernalsTheme.tss",
    uss_tokens: true
)
```

Generates:
- `MainMenu.uxml` with correct element hierarchy
- `MainMenu.uss` with theme import and element stubs
- `MainMenuController.cs` with `QueryElements()` pre-populated for all named elements

---

## Issue 31: `execute_menu_item` Fails for Newly Compiled MenuItems (Known — See Issue 12)

**Problem**: Same as Issue 12 from Phase 2, but confirmed still present. After `compile_and_watch` reports success for `UIToolkitSetup.cs` containing `[MenuItem("BattleYa/UI Toolkit/Create PanelSettings Asset")]`, `execute_menu_item` fails because the menu hasn't registered yet.

**Impact**: Same as Issue 12. Documented here as a re-confirmation during Phase 3 testing.

**Workaround**: Used `editor_eval` to run the same code directly, bypassing the menu item entirely.

---

## Summary

| Issue | Severity | Category | Status |
|-------|----------|----------|--------|
| #26 | HIGH | New tool: `manage_uitoolkit` | **Done** |
| #27 | HIGH | Runtime panel inspection | **Done** |
| #28 | MEDIUM | Hidden panel preview | **Done** |
| #29 | MEDIUM | USS validation | **Done** |
| #30 | LOW | Scaffold templates | **Done** |
| #31 | LOW | MenuItem registration (re-confirm #12) | Known |

### What Worked Well
- `editor_eval` handled all PanelSettings/UIDocument operations flexibly
- UXML and USS are plain text — `Write`/`Edit`/`Read` tools work perfectly
- `compile_and_watch` reliably confirmed zero errors after creating 5 new C# files
- `console_read` confirmed zero runtime errors during play mode
- `scene_save` persisted UIDocument additions to the scene
- `wait_for_ready` correctly waited for domain reload after play mode exit
