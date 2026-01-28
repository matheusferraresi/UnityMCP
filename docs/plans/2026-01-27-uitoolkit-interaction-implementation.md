# UI Toolkit Interaction Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Add four new MCP tools (`uitoolkit_click`, `uitoolkit_set_value`, `uitoolkit_get_value`, `uitoolkit_navigate`) and enhance `uitoolkit_query` with text extraction, enabling AI-assisted automation of Unity Editor windows.

**Architecture:** All tools live in `UIToolkitTools.cs`, sharing a new `FindElement()` helper and validation utilities. Each tool follows the existing pattern: `[MCPTool]` attribute, parameter validation, try/catch with consistent error responses.

**Tech Stack:** C#, Unity 6+, UI Toolkit (UnityEngine.UIElements), UnityEditor APIs

**Testing:** Manual verification in Unity Editor - this is an Editor-only package without automated test infrastructure. Each task includes verification steps.

---

## Task 1: Add GetElementText Helper

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs:362-397` (after BuildElementData)

**Step 1: Add the text extraction helper method**

Add after the `BuildElementData` method (around line 397):

```csharp
/// <summary>
/// Extracts displayable text content from a VisualElement.
/// </summary>
/// <param name="element">The element to extract text from.</param>
/// <param name="maxLength">Maximum text length before truncation (default 500).</param>
/// <returns>The text content, or null if no text is available.</returns>
private static string GetElementText(VisualElement element, int maxLength = 500)
{
    if (element == null)
    {
        return null;
    }

    string text = null;

    // Try specific element types first
    switch (element)
    {
        case Label label:
            text = label.text;
            break;
        case Button button:
            text = button.text;
            break;
        case TextField textField:
            text = textField.value;
            break;
        case Foldout foldout:
            text = foldout.text;
            break;
        case Toggle toggle:
            text = toggle.label;
            break;
        case DropdownField dropdownField:
            text = dropdownField.value;
            break;
        case EnumField enumField:
            text = enumField.value?.ToString();
            break;
        case TextElement textElement:
            text = textElement.text;
            break;
        default:
            // Try reflection for unknown types with a 'text' property
            try
            {
                var textProperty = element.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (textProperty != null && textProperty.PropertyType == typeof(string))
                {
                    text = textProperty.GetValue(element) as string;
                }
            }
            catch
            {
                // Ignore reflection errors
            }
            break;
    }

    // Truncate if needed
    if (!string.IsNullOrEmpty(text) && text.Length > maxLength)
    {
        text = text.Substring(0, maxLength) + "...";
    }

    return string.IsNullOrEmpty(text) ? null : text;
}
```

**Step 2: Verify compilation**

Open Unity Editor with the project, check Console for compilation errors.
Expected: No errors

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add GetElementText helper for text extraction"
```

---

## Task 2: Enhance BuildElementData with Text Property

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs:365-397` (BuildElementData method)

**Step 1: Update BuildElementData to include text**

Replace the `BuildElementData` method:

```csharp
/// <summary>
/// Builds element data for a single VisualElement.
/// </summary>
private static object BuildElementData(VisualElement element)
{
    if (element == null)
    {
        return null;
    }

    var ussClasses = new List<string>();
    foreach (string cssClass in element.GetClasses())
    {
        ussClasses.Add(cssClass);
    }

    var boundingBox = element.worldBound;
    string elementText = GetElementText(element);

    var result = new Dictionary<string, object>
    {
        { "name", element.name },
        { "typeName", element.GetType().Name },
        { "ussClasses", ussClasses },
        { "visible", element.visible },
        { "enabled", element.enabledSelf },
        { "pickable", element.pickingMode == PickingMode.Position },
        { "bounds", new { x = boundingBox.x, y = boundingBox.y, width = boundingBox.width, height = boundingBox.height } },
        { "childCount", element.childCount }
    };

    // Only include text if present
    if (elementText != null)
    {
        result["text"] = elementText;
    }

    return result;
}
```

**Step 2: Verify via MCP call**

Use MCP client to call `uitoolkit_query` on a window with labels/buttons.
Expected: Elements now include `text` property where applicable.

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add text property to query results"
```

---

## Task 3: Add BuildElementTree Text Support

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs:402-439` (BuildElementTree method)

**Step 1: Update BuildElementTree to include text**

Replace the `BuildElementTree` method:

```csharp
/// <summary>
/// Builds a tree structure representing the visual element hierarchy.
/// </summary>
private static object BuildElementTree(VisualElement element, int currentDepth, int maxDepth)
{
    if (element == null)
    {
        return null;
    }

    var ussClasses = new List<string>();
    foreach (string cssClass in element.GetClasses())
    {
        ussClasses.Add(cssClass);
    }

    string elementText = GetElementText(element);

    var elementData = new Dictionary<string, object>
    {
        { "name", element.name },
        { "typeName", element.GetType().Name },
        { "ussClasses", ussClasses },
        { "childCount", element.childCount }
    };

    // Only include text if present
    if (elementText != null)
    {
        elementData["text"] = elementText;
    }

    // Add children if within depth limit
    if (currentDepth < maxDepth && element.childCount > 0)
    {
        var childrenList = new List<object>();
        foreach (var child in element.Children())
        {
            childrenList.Add(BuildElementTree(child, currentDepth + 1, maxDepth));
        }
        elementData["children"] = childrenList;
    }
    else if (element.childCount > 0)
    {
        elementData["childrenTruncated"] = true;
    }

    return elementData;
}
```

**Step 2: Verify via MCP call**

Call `uitoolkit_query` with just `window_type` (no selector) to get tree view.
Expected: Tree nodes include `text` property.

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add text property to element tree results"
```

---

## Task 4: Add FindElement Helper

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs` (add after FindEditorWindow, around line 323)

**Step 1: Add the unified element finder**

```csharp
/// <summary>
/// Finds a single VisualElement using flexible search criteria.
/// </summary>
/// <param name="root">The root element to search from.</param>
/// <param name="selector">USS selector to query.</param>
/// <param name="name">Element name to match.</param>
/// <param name="className">USS class to filter by.</param>
/// <param name="text">Text content to match.</param>
/// <param name="maxDepth">Maximum search depth (default 20).</param>
/// <returns>Tuple of found element and error response (element is null if not found).</returns>
private static (VisualElement element, object error) FindElement(
    VisualElement root,
    string selector = null,
    string name = null,
    string className = null,
    string text = null,
    int maxDepth = 20)
{
    if (root == null)
    {
        return (null, new
        {
            success = false,
            error = "Root element is null."
        });
    }

    bool hasSearchCriteria = !string.IsNullOrEmpty(selector) ||
                             !string.IsNullOrEmpty(name) ||
                             !string.IsNullOrEmpty(className) ||
                             !string.IsNullOrEmpty(text);

    if (!hasSearchCriteria)
    {
        return (null, new
        {
            success = false,
            error = "At least one search parameter (selector, name, class_name, or text) is required.",
            suggestion = "Use uitoolkit_query to explore available elements."
        });
    }

    VisualElement foundElement = null;

    // Strategy 1: USS selector query
    if (!string.IsNullOrEmpty(selector))
    {
        var candidates = root.Query(selector).ToList();

        // Apply additional filters if specified
        foreach (var candidate in candidates)
        {
            if (MatchesFilters(candidate, name, className, text))
            {
                foundElement = candidate;
                break;
            }
        }
    }
    else
    {
        // Strategy 2: Recursive search
        foundElement = FindElementRecursive(root, name, className, text, 0, maxDepth);
    }

    if (foundElement == null)
    {
        string searchDesc = BuildSearchDescription(selector, name, className, text);
        return (null, new
        {
            success = false,
            error = $"Could not find element matching {searchDesc}.",
            suggestion = "Use uitoolkit_query to explore available elements."
        });
    }

    return (foundElement, null);
}

/// <summary>
/// Recursively searches for an element matching the given criteria.
/// </summary>
private static VisualElement FindElementRecursive(
    VisualElement element,
    string targetName,
    string targetClassName,
    string targetText,
    int currentDepth,
    int maxDepth)
{
    if (element == null || currentDepth > maxDepth)
    {
        return null;
    }

    if (MatchesFilters(element, targetName, targetClassName, targetText))
    {
        return element;
    }

    foreach (var child in element.Children())
    {
        var found = FindElementRecursive(child, targetName, targetClassName, targetText, currentDepth + 1, maxDepth);
        if (found != null)
        {
            return found;
        }
    }

    return null;
}

/// <summary>
/// Checks if an element matches the given filter criteria.
/// </summary>
private static bool MatchesFilters(VisualElement element, string name, string className, string text)
{
    if (element == null)
    {
        return false;
    }

    // Name filter
    if (!string.IsNullOrEmpty(name))
    {
        if (string.IsNullOrEmpty(element.name) ||
            !element.name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    // Class filter
    if (!string.IsNullOrEmpty(className))
    {
        if (!element.ClassListContains(className))
        {
            return false;
        }
    }

    // Text filter
    if (!string.IsNullOrEmpty(text))
    {
        string elementText = GetElementText(element);
        if (string.IsNullOrEmpty(elementText) ||
            !elementText.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    return true;
}

/// <summary>
/// Builds a human-readable description of search criteria.
/// </summary>
private static string BuildSearchDescription(string selector, string name, string className, string text)
{
    var parts = new List<string>();

    if (!string.IsNullOrEmpty(selector))
        parts.Add($"selector '{selector}'");
    if (!string.IsNullOrEmpty(name))
        parts.Add($"name '{name}'");
    if (!string.IsNullOrEmpty(className))
        parts.Add($"class '{className}'");
    if (!string.IsNullOrEmpty(text))
        parts.Add($"text containing '{text}'");

    return string.Join(" and ", parts);
}
```

**Step 2: Verify compilation**

Open Unity Editor, check Console for errors.
Expected: No compilation errors

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add FindElement helper with flexible search"
```

---

## Task 5: Add Validation Helpers

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs` (add after FindElement helpers)

**Step 1: Add validation helper methods**

```csharp
/// <summary>
/// Checks if an element is visible in the hierarchy.
/// </summary>
private static bool IsElementVisible(VisualElement element)
{
    if (element == null)
    {
        return false;
    }

    var current = element;
    while (current != null)
    {
        if (!current.visible || current.resolvedStyle.display == DisplayStyle.None)
        {
            return false;
        }
        current = current.parent;
    }

    return true;
}

/// <summary>
/// Checks if an element is enabled in the hierarchy.
/// </summary>
private static bool IsElementEnabled(VisualElement element)
{
    if (element == null)
    {
        return false;
    }

    var current = element;
    while (current != null)
    {
        if (!current.enabledSelf)
        {
            return false;
        }
        current = current.parent;
    }

    return true;
}

/// <summary>
/// Checks if an element can be clicked.
/// </summary>
private static bool IsClickable(VisualElement element)
{
    if (element == null)
    {
        return false;
    }

    // Check for known clickable types
    if (element is Button || element is Toggle || element is Foldout)
    {
        return true;
    }

    // Check if it has a clickable manipulator
    if (element.clickable != null)
    {
        return true;
    }

    // Check picking mode
    return element.pickingMode == PickingMode.Position;
}

/// <summary>
/// Checks if an element can have its value set.
/// </summary>
private static bool IsEditable(VisualElement element)
{
    if (element == null)
    {
        return false;
    }

    return element is TextField ||
           element is IntegerField ||
           element is FloatField ||
           element is Toggle ||
           element is DropdownField ||
           element is EnumField ||
           element is Slider ||
           element is SliderInt ||
           element is MinMaxSlider ||
           element is ObjectField;
}

/// <summary>
/// Builds basic element info for response objects.
/// </summary>
private static object BuildBasicElementInfo(VisualElement element)
{
    if (element == null)
    {
        return null;
    }

    string elementText = GetElementText(element);
    var info = new Dictionary<string, object>
    {
        { "name", element.name },
        { "typeName", element.GetType().Name }
    };

    if (elementText != null)
    {
        info["text"] = elementText;
    }

    return info;
}
```

**Step 2: Verify compilation**

Open Unity Editor, check Console for errors.
Expected: No compilation errors

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add validation helpers for element state"
```

---

## Task 6: Implement uitoolkit_click Tool

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs` (add new region after Get Styles Tool region)

**Step 1: Add the click tool**

Add after the `#endregion` of Get Styles Tool (around line 232):

```csharp
#region Click Tool

/// <summary>
/// Clicks a button, toggle, or clickable element in an EditorWindow.
/// </summary>
[MCPTool("uitoolkit_click", "Click a button, toggle, or clickable element in an EditorWindow", Category = "UIToolkit")]
public static object Click(
    [MCPParam("window_type", "EditorWindow type name", required: true)] string windowType,
    [MCPParam("selector", "USS selector to find element")] string selector = null,
    [MCPParam("name", "Element name to match")] string name = null,
    [MCPParam("class_name", "USS class to filter by")] string className = null,
    [MCPParam("button_text", "Find clickable by visible text")] string buttonText = null)
{
    try
    {
        // Find the EditorWindow
        var (window, windowError) = FindEditorWindow(windowType);
        if (window == null)
        {
            return windowError;
        }

        VisualElement rootElement = window.rootVisualElement;
        if (rootElement == null)
        {
            return new
            {
                success = false,
                error = $"EditorWindow '{windowType}' has no rootVisualElement."
            };
        }

        // Find the target element
        var (element, findError) = FindElement(rootElement, selector, name, className, buttonText);
        if (element == null)
        {
            return findError;
        }

        // Validate element state
        if (!IsElementVisible(element))
        {
            return new
            {
                success = false,
                error = "Element is not visible.",
                element = BuildBasicElementInfo(element)
            };
        }

        if (!IsElementEnabled(element))
        {
            return new
            {
                success = false,
                error = "Element is disabled.",
                element = BuildBasicElementInfo(element)
            };
        }

        // Perform the click based on element type
        bool clicked = PerformClick(element);

        if (!clicked)
        {
            return new
            {
                success = false,
                error = "Element is not clickable.",
                element = BuildBasicElementInfo(element),
                suggestion = "This element type may not support click interactions."
            };
        }

        return new
        {
            success = true,
            clicked = BuildBasicElementInfo(element)
        };
    }
    catch (Exception exception)
    {
        Debug.LogWarning($"[UIToolkitTools] Error clicking element: {exception.Message}");
        return new
        {
            success = false,
            error = $"Error clicking element: {exception.Message}"
        };
    }
}

/// <summary>
/// Performs a click action on the element based on its type.
/// </summary>
private static bool PerformClick(VisualElement element)
{
    if (element == null)
    {
        return false;
    }

    switch (element)
    {
        case Button button:
            // Use the clickable to simulate click
            using (var clickEvent = ClickEvent.GetPooled())
            {
                clickEvent.target = button;
                button.SendEvent(clickEvent);
            }
            return true;

        case Toggle toggle:
            toggle.value = !toggle.value;
            return true;

        case Foldout foldout:
            foldout.value = !foldout.value;
            return true;

        default:
            // Try generic clickable
            if (element.clickable != null)
            {
                using (var clickEvent = ClickEvent.GetPooled())
                {
                    clickEvent.target = element;
                    element.SendEvent(clickEvent);
                }
                return true;
            }

            // Last resort: send click event anyway
            if (element.pickingMode == PickingMode.Position)
            {
                using (var clickEvent = ClickEvent.GetPooled())
                {
                    clickEvent.target = element;
                    element.SendEvent(clickEvent);
                }
                return true;
            }

            return false;
    }
}

#endregion
```

**Step 2: Verify via MCP call**

Call `uitoolkit_click` on a known button in an editor window.
Expected: Button click is triggered, success response returned.

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add uitoolkit_click tool"
```

---

## Task 7: Implement uitoolkit_get_value Tool

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs` (add after Click Tool region)

**Step 1: Add the get value tool**

```csharp
#region Get Value Tool

/// <summary>
/// Gets the current value from an input field or control in an EditorWindow.
/// </summary>
[MCPTool("uitoolkit_get_value", "Get the current value from an input field or control", Category = "UIToolkit")]
public static object GetValue(
    [MCPParam("window_type", "EditorWindow type name", required: true)] string windowType,
    [MCPParam("selector", "USS selector to find element")] string selector = null,
    [MCPParam("name", "Element name to match")] string name = null,
    [MCPParam("class_name", "USS class to filter by")] string className = null)
{
    try
    {
        // Find the EditorWindow
        var (window, windowError) = FindEditorWindow(windowType);
        if (window == null)
        {
            return windowError;
        }

        VisualElement rootElement = window.rootVisualElement;
        if (rootElement == null)
        {
            return new
            {
                success = false,
                error = $"EditorWindow '{windowType}' has no rootVisualElement."
            };
        }

        // Find the target element
        var (element, findError) = FindElement(rootElement, selector, name, className);
        if (element == null)
        {
            return findError;
        }

        // Extract value based on element type
        return ExtractElementValue(element);
    }
    catch (Exception exception)
    {
        Debug.LogWarning($"[UIToolkitTools] Error getting value: {exception.Message}");
        return new
        {
            success = false,
            error = $"Error getting value: {exception.Message}"
        };
    }
}

/// <summary>
/// Extracts the value from an element based on its type.
/// </summary>
private static object ExtractElementValue(VisualElement element)
{
    var elementInfo = BuildBasicElementInfo(element);
    bool isEditable = IsEditable(element);

    switch (element)
    {
        case TextField textField:
            return new
            {
                success = true,
                element = elementInfo,
                value = textField.value,
                editable = isEditable
            };

        case IntegerField intField:
            return new
            {
                success = true,
                element = elementInfo,
                value = intField.value,
                editable = isEditable
            };

        case FloatField floatField:
            return new
            {
                success = true,
                element = elementInfo,
                value = floatField.value,
                editable = isEditable
            };

        case Toggle toggle:
            return new
            {
                success = true,
                element = elementInfo,
                value = toggle.value,
                label = toggle.label,
                editable = isEditable
            };

        case DropdownField dropdownField:
            return new
            {
                success = true,
                element = elementInfo,
                value = dropdownField.value,
                index = dropdownField.index,
                choices = dropdownField.choices?.ToList(),
                editable = isEditable
            };

        case EnumField enumField:
            var enumType = enumField.value?.GetType();
            string[] enumOptions = null;
            if (enumType != null && enumType.IsEnum)
            {
                enumOptions = Enum.GetNames(enumType);
            }
            return new
            {
                success = true,
                element = elementInfo,
                value = enumField.value?.ToString(),
                enumType = enumType?.Name,
                options = enumOptions,
                editable = isEditable
            };

        case Slider slider:
            return new
            {
                success = true,
                element = elementInfo,
                value = slider.value,
                min = slider.lowValue,
                max = slider.highValue,
                editable = isEditable
            };

        case SliderInt sliderInt:
            return new
            {
                success = true,
                element = elementInfo,
                value = sliderInt.value,
                min = sliderInt.lowValue,
                max = sliderInt.highValue,
                editable = isEditable
            };

        case MinMaxSlider minMaxSlider:
            return new
            {
                success = true,
                element = elementInfo,
                minValue = minMaxSlider.minValue,
                maxValue = minMaxSlider.maxValue,
                limitMin = minMaxSlider.lowLimit,
                limitMax = minMaxSlider.highLimit,
                editable = isEditable
            };

        case ObjectField objectField:
            var obj = objectField.value;
            object valueInfo = null;
            if (obj != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                valueInfo = new
                {
                    name = obj.name,
                    type = obj.GetType().Name,
                    assetPath = string.IsNullOrEmpty(assetPath) ? null : assetPath,
                    instanceId = obj.GetInstanceID()
                };
            }
            return new
            {
                success = true,
                element = elementInfo,
                value = valueInfo,
                objectType = objectField.objectType?.FullName,
                allowSceneObjects = objectField.allowSceneObjects,
                editable = isEditable
            };

        case RadioButtonGroup radioGroup:
            return new
            {
                success = true,
                element = elementInfo,
                value = radioGroup.value,
                choices = radioGroup.choices?.ToList(),
                editable = isEditable
            };

        case Label label:
            return new
            {
                success = true,
                element = elementInfo,
                value = label.text,
                editable = false
            };

        case Foldout foldout:
            return new
            {
                success = true,
                element = elementInfo,
                value = foldout.value,
                text = foldout.text,
                editable = false
            };

        default:
            // Try to get a generic value property
            string textValue = GetElementText(element);
            if (textValue != null)
            {
                return new
                {
                    success = true,
                    element = elementInfo,
                    value = textValue,
                    editable = false,
                    note = "Value extracted from text property; element type may not be fully supported."
                };
            }

            return new
            {
                success = false,
                element = elementInfo,
                error = $"Element type '{element.GetType().Name}' does not have a readable value.",
                suggestion = "This element type may not support value extraction."
            };
    }
}

#endregion
```

**Step 2: Verify via MCP call**

Call `uitoolkit_get_value` on various field types.
Expected: Returns appropriate value and metadata for each field type.

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add uitoolkit_get_value tool"
```

---

## Task 8: Implement uitoolkit_set_value Tool

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs` (add after Get Value Tool region)

**Step 1: Add the set value tool**

```csharp
#region Set Value Tool

/// <summary>
/// Sets the value of an input field or control in an EditorWindow.
/// </summary>
[MCPTool("uitoolkit_set_value", "Set the value of an input field or control", Category = "UIToolkit")]
public static object SetValue(
    [MCPParam("window_type", "EditorWindow type name", required: true)] string windowType,
    [MCPParam("value", "Value to set (string, number, bool, or asset path)", required: true)] object value,
    [MCPParam("selector", "USS selector to find element")] string selector = null,
    [MCPParam("name", "Element name to match")] string name = null,
    [MCPParam("class_name", "USS class to filter by")] string className = null)
{
    try
    {
        // Find the EditorWindow
        var (window, windowError) = FindEditorWindow(windowType);
        if (window == null)
        {
            return windowError;
        }

        VisualElement rootElement = window.rootVisualElement;
        if (rootElement == null)
        {
            return new
            {
                success = false,
                error = $"EditorWindow '{windowType}' has no rootVisualElement."
            };
        }

        // Find the target element
        var (element, findError) = FindElement(rootElement, selector, name, className);
        if (element == null)
        {
            return findError;
        }

        // Validate element state
        if (!IsElementVisible(element))
        {
            return new
            {
                success = false,
                error = "Element is not visible.",
                element = BuildBasicElementInfo(element)
            };
        }

        if (!IsElementEnabled(element))
        {
            return new
            {
                success = false,
                error = "Element is disabled.",
                element = BuildBasicElementInfo(element)
            };
        }

        if (!IsEditable(element))
        {
            return new
            {
                success = false,
                error = $"Element type '{element.GetType().Name}' is not editable.",
                element = BuildBasicElementInfo(element)
            };
        }

        // Apply the value
        return ApplyElementValue(element, value);
    }
    catch (Exception exception)
    {
        Debug.LogWarning($"[UIToolkitTools] Error setting value: {exception.Message}");
        return new
        {
            success = false,
            error = $"Error setting value: {exception.Message}"
        };
    }
}

/// <summary>
/// Applies a value to an element based on its type.
/// </summary>
private static object ApplyElementValue(VisualElement element, object value)
{
    var elementInfo = BuildBasicElementInfo(element);

    switch (element)
    {
        case TextField textField:
        {
            string previousValue = textField.value;
            string newValue = value?.ToString() ?? "";
            textField.value = newValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue
            };
        }

        case IntegerField intField:
        {
            int previousValue = intField.value;
            if (!TryConvertToInt(value, out int newValue))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"Cannot convert '{value}' to integer."
                };
            }
            intField.value = newValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue
            };
        }

        case FloatField floatField:
        {
            float previousValue = floatField.value;
            if (!TryConvertToFloat(value, out float newValue))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"Cannot convert '{value}' to float."
                };
            }
            floatField.value = newValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue
            };
        }

        case Toggle toggle:
        {
            bool previousValue = toggle.value;
            if (!TryConvertToBool(value, out bool newValue))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"Cannot convert '{value}' to boolean."
                };
            }
            toggle.value = newValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue
            };
        }

        case DropdownField dropdownField:
        {
            string previousValue = dropdownField.value;
            string newValue = value?.ToString() ?? "";

            // Check if the value exists in choices
            if (dropdownField.choices != null && !dropdownField.choices.Contains(newValue))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"Value '{newValue}' is not in the available choices.",
                    choices = dropdownField.choices.ToList()
                };
            }

            dropdownField.value = newValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue
            };
        }

        case EnumField enumField:
        {
            var previousValue = enumField.value;
            string valueStr = value?.ToString() ?? "";

            var enumType = enumField.value?.GetType();
            if (enumType == null || !enumType.IsEnum)
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = "EnumField does not have a valid enum type set."
                };
            }

            if (!Enum.TryParse(enumType, valueStr, true, out object newEnumValue))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"'{valueStr}' is not a valid value for enum type '{enumType.Name}'.",
                    options = Enum.GetNames(enumType)
                };
            }

            enumField.value = (Enum)newEnumValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue = previousValue?.ToString(),
                newValue = newEnumValue.ToString()
            };
        }

        case Slider slider:
        {
            float previousValue = slider.value;
            if (!TryConvertToFloat(value, out float newValue))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"Cannot convert '{value}' to float."
                };
            }

            // Clamp to slider range
            newValue = Mathf.Clamp(newValue, slider.lowValue, slider.highValue);
            slider.value = newValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue,
                range = new { min = slider.lowValue, max = slider.highValue }
            };
        }

        case SliderInt sliderInt:
        {
            int previousValue = sliderInt.value;
            if (!TryConvertToInt(value, out int newValue))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"Cannot convert '{value}' to integer."
                };
            }

            // Clamp to slider range
            newValue = Mathf.Clamp(newValue, sliderInt.lowValue, sliderInt.highValue);
            sliderInt.value = newValue;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue,
                range = new { min = sliderInt.lowValue, max = sliderInt.highValue }
            };
        }

        case MinMaxSlider minMaxSlider:
        {
            // Expect value to be an object with minValue and maxValue
            float previousMin = minMaxSlider.minValue;
            float previousMax = minMaxSlider.maxValue;

            if (!TryParseMinMax(value, out float newMin, out float newMax))
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = "Value must be an object with 'minValue' and 'maxValue' properties, or a string like '0.5,0.8'."
                };
            }

            // Clamp to limits
            newMin = Mathf.Clamp(newMin, minMaxSlider.lowLimit, minMaxSlider.highLimit);
            newMax = Mathf.Clamp(newMax, minMaxSlider.lowLimit, minMaxSlider.highLimit);

            minMaxSlider.minValue = newMin;
            minMaxSlider.maxValue = newMax;

            return new
            {
                success = true,
                element = elementInfo,
                previousMinValue = previousMin,
                previousMaxValue = previousMax,
                newMinValue = newMin,
                newMaxValue = newMax,
                limits = new { min = minMaxSlider.lowLimit, max = minMaxSlider.highLimit }
            };
        }

        case ObjectField objectField:
        {
            var previousObj = objectField.value;
            object previousValue = null;
            if (previousObj != null)
            {
                previousValue = new
                {
                    name = previousObj.name,
                    type = previousObj.GetType().Name,
                    assetPath = AssetDatabase.GetAssetPath(previousObj)
                };
            }

            // Handle null/clear
            if (value == null || (value is string strVal && string.IsNullOrEmpty(strVal)))
            {
                objectField.value = null;
                return new
                {
                    success = true,
                    element = elementInfo,
                    previousValue,
                    newValue = (object)null
                };
            }

            // Load asset by path
            string assetPath = value.ToString();
            var newObj = AssetDatabase.LoadAssetAtPath(assetPath, objectField.objectType ?? typeof(UnityEngine.Object));

            if (newObj == null)
            {
                return new
                {
                    success = false,
                    element = elementInfo,
                    error = $"Could not load asset at path '{assetPath}'.",
                    expectedType = objectField.objectType?.Name
                };
            }

            objectField.value = newObj;
            return new
            {
                success = true,
                element = elementInfo,
                previousValue,
                newValue = new
                {
                    name = newObj.name,
                    type = newObj.GetType().Name,
                    assetPath
                }
            };
        }

        default:
            return new
            {
                success = false,
                element = elementInfo,
                error = $"Setting values for element type '{element.GetType().Name}' is not supported."
            };
    }
}

/// <summary>
/// Tries to convert a value to an integer.
/// </summary>
private static bool TryConvertToInt(object value, out int result)
{
    result = 0;

    if (value is int intVal)
    {
        result = intVal;
        return true;
    }

    if (value is long longVal)
    {
        result = (int)longVal;
        return true;
    }

    if (value is float floatVal)
    {
        result = (int)floatVal;
        return true;
    }

    if (value is double doubleVal)
    {
        result = (int)doubleVal;
        return true;
    }

    if (value is string strVal && int.TryParse(strVal, out result))
    {
        return true;
    }

    return false;
}

/// <summary>
/// Tries to convert a value to a float.
/// </summary>
private static bool TryConvertToFloat(object value, out float result)
{
    result = 0f;

    if (value is float floatVal)
    {
        result = floatVal;
        return true;
    }

    if (value is double doubleVal)
    {
        result = (float)doubleVal;
        return true;
    }

    if (value is int intVal)
    {
        result = intVal;
        return true;
    }

    if (value is long longVal)
    {
        result = longVal;
        return true;
    }

    if (value is string strVal && float.TryParse(strVal, out result))
    {
        return true;
    }

    return false;
}

/// <summary>
/// Tries to convert a value to a boolean.
/// </summary>
private static bool TryConvertToBool(object value, out bool result)
{
    result = false;

    if (value is bool boolVal)
    {
        result = boolVal;
        return true;
    }

    if (value is string strVal)
    {
        if (bool.TryParse(strVal, out result))
        {
            return true;
        }

        // Handle common string representations
        strVal = strVal.Trim().ToLowerInvariant();
        if (strVal == "1" || strVal == "yes" || strVal == "on")
        {
            result = true;
            return true;
        }
        if (strVal == "0" || strVal == "no" || strVal == "off")
        {
            result = false;
            return true;
        }
    }

    if (value is int intVal)
    {
        result = intVal != 0;
        return true;
    }

    return false;
}

/// <summary>
/// Tries to parse min/max values from various formats.
/// </summary>
private static bool TryParseMinMax(object value, out float minValue, out float maxValue)
{
    minValue = 0;
    maxValue = 0;

    if (value is string strVal)
    {
        // Try "min,max" format
        var parts = strVal.Split(',');
        if (parts.Length == 2 &&
            float.TryParse(parts[0].Trim(), out minValue) &&
            float.TryParse(parts[1].Trim(), out maxValue))
        {
            return true;
        }
    }

    // Try dictionary/JSON object format
    if (value is IDictionary<string, object> dict)
    {
        if (dict.TryGetValue("minValue", out object minObj) &&
            dict.TryGetValue("maxValue", out object maxObj) &&
            TryConvertToFloat(minObj, out minValue) &&
            TryConvertToFloat(maxObj, out maxValue))
        {
            return true;
        }
    }

    // Try Newtonsoft JObject
    try
    {
        var valueType = value.GetType();
        var minProp = valueType.GetProperty("minValue") ?? valueType.GetProperty("MinValue");
        var maxProp = valueType.GetProperty("maxValue") ?? valueType.GetProperty("MaxValue");

        if (minProp != null && maxProp != null)
        {
            var minObj = minProp.GetValue(value);
            var maxObj = maxProp.GetValue(value);

            if (TryConvertToFloat(minObj, out minValue) && TryConvertToFloat(maxObj, out maxValue))
            {
                return true;
            }
        }
    }
    catch
    {
        // Ignore reflection errors
    }

    return false;
}

#endregion
```

**Step 2: Verify via MCP call**

Call `uitoolkit_set_value` on various field types with appropriate values.
Expected: Values are set correctly, previous and new values returned.

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add uitoolkit_set_value tool"
```

---

## Task 9: Implement uitoolkit_navigate Tool

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs` (add after Set Value Tool region)

**Step 1: Add the navigate tool**

```csharp
#region Navigate Tool

/// <summary>
/// Navigates UI elements like foldouts and tabs in an EditorWindow.
/// </summary>
[MCPTool("uitoolkit_navigate", "Expand/collapse foldouts or select tabs in an EditorWindow", Category = "UIToolkit")]
public static object Navigate(
    [MCPParam("window_type", "EditorWindow type name", required: true)] string windowType,
    [MCPParam("selector", "USS selector to find element")] string selector = null,
    [MCPParam("name", "Element name to match")] string name = null,
    [MCPParam("class_name", "USS class to filter by")] string className = null,
    [MCPParam("foldout_text", "Find foldout by header text")] string foldoutText = null,
    [MCPParam("tab_text", "Find tab by label text")] string tabText = null,
    [MCPParam("expand", "For foldouts: true=expand, false=collapse (toggles if omitted)")] bool? expand = null)
{
    try
    {
        // Find the EditorWindow
        var (window, windowError) = FindEditorWindow(windowType);
        if (window == null)
        {
            return windowError;
        }

        VisualElement rootElement = window.rootVisualElement;
        if (rootElement == null)
        {
            return new
            {
                success = false,
                error = $"EditorWindow '{windowType}' has no rootVisualElement."
            };
        }

        // Determine search text (foldout_text or tab_text takes precedence)
        string searchText = foldoutText ?? tabText;

        // Find the target element
        var (element, findError) = FindElement(rootElement, selector, name, className, searchText);
        if (element == null)
        {
            return findError;
        }

        // Validate element state
        if (!IsElementVisible(element))
        {
            return new
            {
                success = false,
                error = "Element is not visible.",
                element = BuildBasicElementInfo(element)
            };
        }

        if (!IsElementEnabled(element))
        {
            return new
            {
                success = false,
                error = "Element is disabled.",
                element = BuildBasicElementInfo(element)
            };
        }

        // Perform navigation based on element type
        return PerformNavigation(element, expand);
    }
    catch (Exception exception)
    {
        Debug.LogWarning($"[UIToolkitTools] Error navigating: {exception.Message}");
        return new
        {
            success = false,
            error = $"Error navigating: {exception.Message}"
        };
    }
}

/// <summary>
/// Performs navigation action on the element based on its type.
/// </summary>
private static object PerformNavigation(VisualElement element, bool? expand)
{
    var elementInfo = BuildBasicElementInfo(element);

    switch (element)
    {
        case Foldout foldout:
        {
            bool previousState = foldout.value;
            bool newState = expand ?? !previousState; // Toggle if expand not specified
            foldout.value = newState;

            return new
            {
                success = true,
                element = elementInfo,
                action = newState ? "expanded" : "collapsed",
                previousState,
                newState
            };
        }

        case Toggle toggle:
        {
            // Some UI patterns use toggles as collapsible headers
            bool previousState = toggle.value;
            bool newState = expand ?? !previousState;
            toggle.value = newState;

            return new
            {
                success = true,
                element = elementInfo,
                action = newState ? "enabled" : "disabled",
                previousState,
                newState
            };
        }

        case TreeView treeView:
        {
            // TreeView navigation - expand/collapse all or specific items
            // Note: Full TreeView support would need item IDs
            return new
            {
                success = false,
                element = elementInfo,
                error = "TreeView navigation requires item IDs. Use uitoolkit_query to discover items first.",
                suggestion = "TreeView item navigation will be supported in a future update."
            };
        }

        case ListView listView:
        {
            // ListView - scroll to item, select
            return new
            {
                success = false,
                element = elementInfo,
                error = "ListView navigation requires item indices. Use uitoolkit_query to discover items first.",
                suggestion = "ListView item navigation will be supported in a future update."
            };
        }

        default:
        {
            // Check if element looks like a tab (Button inside tab bar, or has tab-like classes)
            if (element is Button button)
            {
                // Try clicking it as a tab
                using (var clickEvent = ClickEvent.GetPooled())
                {
                    clickEvent.target = button;
                    button.SendEvent(clickEvent);
                }

                return new
                {
                    success = true,
                    element = elementInfo,
                    action = "clicked",
                    note = "Element clicked as potential tab button."
                };
            }

            return new
            {
                success = false,
                element = elementInfo,
                error = $"Element type '{element.GetType().Name}' is not a navigable element.",
                suggestion = "Navigable elements include: Foldout, Toggle (as header), or Button (as tab)."
            };
        }
    }
}

#endregion
```

**Step 2: Verify via MCP call**

Call `uitoolkit_navigate` on foldouts in an editor window.
Expected: Foldouts expand/collapse correctly.

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "feat(uitoolkit): add uitoolkit_navigate tool"
```

---

## Task 10: Add Required Using Statement

**Files:**
- Modify: `Package/Editor/Tools/UIToolkitTools.cs` (top of file)

**Step 1: Add IDictionary using**

Check if `System.Collections.Generic` is already imported (it is, at line 3). The `IDictionary<string, object>` should work. But verify `ObjectField` import exists.

Add after existing using statements if needed:

```csharp
using UnityEditor.UIElements;
```

This is needed for `ObjectField`, `IntegerField`, `FloatField`, `EnumField`, `ToolbarButton`, `ToolbarToggle`.

**Step 2: Verify compilation**

Open Unity Editor, check Console for errors.
Expected: No compilation errors

**Step 3: Commit**

```bash
git add Package/Editor/Tools/UIToolkitTools.cs
git commit -m "fix(uitoolkit): add missing UnityEditor.UIElements using"
```

---

## Task 11: Final Integration Testing

**Files:**
- No code changes - testing only

**Step 1: Test uitoolkit_query enhancement**

Call `uitoolkit_query` with `window_type: "InspectorWindow"`.
Expected: Elements include `text` property where applicable.

**Step 2: Test uitoolkit_click**

1. Open a Unity window with buttons (e.g., Build Settings)
2. Call `uitoolkit_click` with `window_type: "BuildPlayerWindow"` and `button_text: "Build"`
Expected: Error about element state or success if enabled.

**Step 3: Test uitoolkit_get_value**

1. Open Inspector with a GameObject selected
2. Call `uitoolkit_get_value` looking for name field
Expected: Returns the current value.

**Step 4: Test uitoolkit_set_value**

1. Open a window with a text field
2. Call `uitoolkit_set_value` with a new value
Expected: Value changes, response shows previous and new values.

**Step 5: Test uitoolkit_navigate**

1. Find a window with foldouts
2. Call `uitoolkit_navigate` to expand/collapse
Expected: Foldout state changes.

**Step 6: Final commit**

```bash
git add -A
git commit -m "test: verify all uitoolkit interaction tools work correctly"
```

---

## Task 12: Update Package Version (Optional)

**Files:**
- Modify: `Package/package.json`

**Step 1: Bump version**

If releasing, update version from current to next minor:

```json
{
  "version": "0.2.0"
}
```

**Step 2: Commit**

```bash
git add Package/package.json
git commit -m "chore: bump version to 0.2.0 for uitoolkit interaction features"
```

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Add GetElementText helper | UIToolkitTools.cs |
| 2 | Enhance BuildElementData with text | UIToolkitTools.cs |
| 3 | Enhance BuildElementTree with text | UIToolkitTools.cs |
| 4 | Add FindElement helper | UIToolkitTools.cs |
| 5 | Add validation helpers | UIToolkitTools.cs |
| 6 | Implement uitoolkit_click | UIToolkitTools.cs |
| 7 | Implement uitoolkit_get_value | UIToolkitTools.cs |
| 8 | Implement uitoolkit_set_value | UIToolkitTools.cs |
| 9 | Implement uitoolkit_navigate | UIToolkitTools.cs |
| 10 | Add required using statement | UIToolkitTools.cs |
| 11 | Integration testing | - |
| 12 | Update package version (optional) | package.json |
