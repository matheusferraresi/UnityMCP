# UI Toolkit Editor Window Interaction Design

**Date:** 2026-01-27
**Status:** Approved
**Primary Use Case:** AI-assisted development (Claude/Cursor automating tasks in custom editor windows)

## Overview

Add support for interacting with UI Toolkit-based editor windows, enabling AI tools to click buttons, set field values, read current values, and navigate tabs/foldouts. This expands Unity MCP from read-only UI inspection to full UI automation.

## Scope

**In Scope:**
- UI Toolkit elements only (modern Unity 2021+ editor tools)
- Click, set value, get value, and navigation interactions
- Text content extraction from UI elements

**Out of Scope (for now):**
- IMGUI-based windows (potential future extension if demand exists)
- Async operation handling (dialogs triggered by button clicks)

## Tool Design

### Enhanced Existing Tool

#### `uitoolkit_query` (Enhanced)

Add `text` property to element data returned by queries, capturing displayed content.

**New field in element results:**

```json
{
  "name": "warning-label",
  "typeName": "Label",
  "text": "Character name cannot be empty",
  "ussClasses": ["warning", "validation-message"],
  "visible": true,
  "enabled": true
}
```

**Text extraction by element type:**

| Element Type | Text Source |
|--------------|-------------|
| `Label` | `.text` property |
| `Button` | `.text` property |
| `TextField` | `.value` property |
| `TextElement` (base) | `.text` property |
| `Foldout` | `.text` property (header text) |
| `Toggle` | `.label` property |
| `DropdownField` | `.value` property (selected option) |
| `EnumField` | `.value.ToString()` |
| Other | Attempt `.text` via reflection, null if unavailable |

**Truncation:** Text longer than 500 characters truncated with `...`.

---

### New Tools

#### `uitoolkit_click`

**Purpose:** Click buttons, toggles, or any clickable element in an editor window.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `window_type` | string | Yes | EditorWindow type name |
| `selector` | string | No | USS selector to find element |
| `name` | string | No | Element name to match |
| `class_name` | string | No | USS class to filter by |
| `button_text` | string | No | Find clickable by visible text |

At least one of `selector`, `name`, `class_name`, or `button_text` required.

**Click mechanism by element type:**

| Element Type | Action |
|--------------|--------|
| `Button` | Invoke via `clickable.SimulateClick()` |
| `Toggle` | Flip `.value` property |
| `Foldout` | Flip `.value` property |
| `ToolbarButton`, `ToolbarToggle` | Same as Button/Toggle |
| Generic clickable | Send `ClickEvent` via `element.SendEvent()` |

**Return value:**

```json
{
  "success": true,
  "clicked": {
    "name": "create-button",
    "typeName": "Button",
    "text": "Create Character"
  }
}
```

**Error cases:** Element not found, element not visible, element disabled, element not clickable.

---

#### `uitoolkit_set_value`

**Purpose:** Set values on input fields, dropdowns, sliders, and toggles.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `window_type` | string | Yes | EditorWindow type name |
| `selector` | string | No | USS selector to find element |
| `name` | string | No | Element name to match |
| `class_name` | string | No | USS class to filter by |
| `value` | any | Yes | Value to set (string, number, bool, asset path) |

**Value handling by element type:**

| Element Type | Value Type | Action |
|--------------|------------|--------|
| `TextField` | string | Set `.value` |
| `IntegerField` | int | Parse and set `.value` |
| `FloatField` | float | Parse and set `.value` |
| `Toggle` | bool | Set `.value` |
| `DropdownField` | string | Match against `.choices`, set `.value` |
| `EnumField` | string | Parse enum name, set `.value` |
| `Slider` | float | Clamp to range, set `.value` |
| `SliderInt` | int | Clamp to range, set `.value` |
| `MinMaxSlider` | object | Set `.minValue` and `.maxValue` |
| `ObjectField` | string (asset path) or null | Load via `AssetDatabase.LoadAssetAtPath`, validate type |

**Return value:**

```json
{
  "success": true,
  "element": { "name": "character-name", "typeName": "TextField" },
  "previousValue": "",
  "newValue": "Player"
}
```

**Error cases:** Element not found, element not editable, value type mismatch, value out of range, invalid enum/dropdown choice, asset not found, asset type mismatch.

---

#### `uitoolkit_get_value`

**Purpose:** Read current values from input fields and controls.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `window_type` | string | Yes | EditorWindow type name |
| `selector` | string | No | USS selector to find element |
| `name` | string | No | Element name to match |
| `class_name` | string | No | USS class to filter by |

**Value extraction by element type:**

| Element Type | Returns |
|--------------|---------|
| `TextField` | String value |
| `IntegerField` | Integer value |
| `FloatField` | Float value |
| `Toggle` | Boolean value |
| `DropdownField` | Selected choice + available choices array |
| `EnumField` | Enum value string + available options array |
| `Slider` | Float value + min/max range |
| `SliderInt` | Int value + min/max range |
| `MinMaxSlider` | Object with minValue, maxValue, and limits |
| `RadioButtonGroup` | Selected index + choices array |
| `Label` | Text content (read-only indicator) |
| `ObjectField` | Object name, type, asset path, instance ID + objectType, allowSceneObjects |

**Return value examples:**

```json
{
  "success": true,
  "element": { "name": "difficulty", "typeName": "DropdownField" },
  "value": "Hard",
  "choices": ["Easy", "Medium", "Hard", "Expert"],
  "editable": true
}
```

```json
{
  "success": true,
  "element": { "name": "target-prefab", "typeName": "ObjectField" },
  "value": {
    "name": "PlayerCharacter",
    "type": "GameObject",
    "assetPath": "Assets/Prefabs/PlayerCharacter.prefab",
    "instanceId": 12340
  },
  "objectType": "UnityEngine.GameObject",
  "allowSceneObjects": true
}
```

---

#### `uitoolkit_navigate`

**Purpose:** Expand/collapse foldouts and select tabs in tab views.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `window_type` | string | Yes | EditorWindow type name |
| `selector` | string | No | USS selector to find element |
| `name` | string | No | Element name to match |
| `class_name` | string | No | USS class to filter by |
| `foldout_text` | string | No | Find foldout by header text |
| `tab_text` | string | No | Find tab by label text |
| `expand` | bool | No | For foldouts: true=expand, false=collapse (toggles if omitted) |

**Navigation by element type:**

| Element Type | Action |
|--------------|--------|
| `Foldout` | Set `.value` to expand/collapse |
| `Tab` (TabView) | Call tab selection API |
| `TreeView` item | Expand/collapse node |
| `ListView` item | Scroll into view, select |

**Return value:**

```json
{
  "success": true,
  "element": { "name": "advanced-settings", "typeName": "Foldout", "text": "Advanced Settings" },
  "action": "expanded",
  "previousState": false,
  "newState": true
}
```

**Error cases:** Element not found, element is not navigable.

---

## Shared Infrastructure

### `FindElement()` Helper

Unified element lookup used by all interaction tools:

```csharp
private static (VisualElement element, object error) FindElement(
    VisualElement root,
    string selector = null,
    string name = null,
    string className = null,
    string text = null,
    Type requiredType = null)
```

**Search priority:**
1. If `selector` provided → USS query
2. If `name` provided → Match element name
3. If `className` provided → Match USS class
4. If `text` provided → Match element text content
5. Multiple params combine as AND filters

### Error Response Pattern

Consistent across all tools:

```json
{
  "success": false,
  "error": "Could not find element matching selector '.submit-btn' in window 'CharacterManager'",
  "suggestion": "Use uitoolkit_query to explore available elements"
}
```

### Validation Helpers

- `IsClickable(element)` - Check if element can receive clicks
- `IsEditable(element)` - Check if element accepts value changes
- `IsVisible(element)` - Check visibility in hierarchy
- `IsEnabled(element)` - Check enabled state in hierarchy

---

## Supported Element Types Summary

| Category | Element Types |
|----------|---------------|
| **Clickable** | Button, Toggle, Foldout, ToolbarButton, ToolbarToggle |
| **Editable** | TextField, IntegerField, FloatField, Toggle, DropdownField, EnumField, Slider, SliderInt, MinMaxSlider, ObjectField |
| **Readable** | All editable types + Label, RadioButtonGroup |
| **Navigable** | Foldout, Tab, TreeView, ListView |

---

## Implementation Location

All tools implemented in `Package/Editor/Tools/UIToolkitTools.cs`, sharing existing helper methods and adding new ones as needed.

---

## Future Considerations

- **IMGUI Support:** Could be added later if specific legacy tools require it, but would need reflection-based approach
- **Async Operations:** Buttons that trigger dialogs or coroutines may need special handling in future iterations
- **Batch Operations:** Setting multiple values in one call could improve efficiency for complex forms
