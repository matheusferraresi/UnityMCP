using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnixxtyMCP.Editor;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Manages Unity UI (UGUI/Canvas) elements: create canvases, add UI elements,
    /// modify RectTransforms, configure layout groups, and inspect UI hierarchy.
    /// </summary>
    public static class ManageUGUI
    {
        [MCPTool("manage_ugui", "Manage Unity UI (Canvas/UGUI): create canvases, add buttons/text/images/panels, modify RectTransforms, add layout groups, inspect UI hierarchy", Category = "UI", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action: create_canvas, add_element, modify_rect, add_layout, set_text, set_image, get_hierarchy, remove_element", required: true,
                Enum = new[] { "create_canvas", "add_element", "modify_rect", "add_layout", "set_text", "set_image", "get_hierarchy", "remove_element" })] string action,
            [MCPParam("target", "Instance ID (int) or name/path of target UI element")] string target = null,
            [MCPParam("name", "Name for the new element")] string name = null,
            [MCPParam("element_type", "UI element type: Button, Text, Image, Panel, ScrollView, InputField, Slider, Toggle, Dropdown, RawImage",
                Enum = new[] { "Button", "Text", "Image", "Panel", "ScrollView", "InputField", "Slider", "Toggle", "Dropdown", "RawImage" })] string elementType = null,
            [MCPParam("parent", "Instance ID or name/path of parent (for add_element)")] string parent = null,
            [MCPParam("canvas_render_mode", "Canvas render mode: ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace",
                Enum = new[] { "ScreenSpaceOverlay", "ScreenSpaceCamera", "WorldSpace" })] string canvasRenderMode = "ScreenSpaceOverlay",
            [MCPParam("anchors", "Anchor preset or custom anchors: 'stretch', 'center', 'top-left', 'bottom-right', or {minX,minY,maxX,maxY}")] object anchors = null,
            [MCPParam("position", "Anchored position as [x,y] array")] object position = null,
            [MCPParam("size", "Size delta as [width,height] array")] object size = null,
            [MCPParam("pivot", "Pivot as [x,y] array (0-1 range)")] object pivot = null,
            [MCPParam("text", "Text content for Text/Button/InputField elements")] string text = null,
            [MCPParam("font_size", "Font size for text elements")] int? fontSize = null,
            [MCPParam("color", "Color as hex string (#RRGGBB or #RRGGBBAA) or name (red, blue, etc)")] string color = null,
            [MCPParam("sprite_path", "Asset path to sprite for Image elements")] string spritePath = null,
            [MCPParam("layout_type", "Layout group type: Vertical, Horizontal, Grid",
                Enum = new[] { "Vertical", "Horizontal", "Grid" })] string layoutType = null,
            [MCPParam("spacing", "Spacing for layout groups")] float? spacing = null,
            [MCPParam("padding", "Padding as [left,right,top,bottom] array")] object padding = null,
            [MCPParam("child_alignment", "Child alignment: UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight")] string childAlignment = null,
            [MCPParam("cell_size", "Cell size for Grid layout as [width,height]")] object cellSize = null,
            [MCPParam("fit_mode", "Content size fitter mode for width/height: Unconstrained, MinSize, PreferredSize",
                Enum = new[] { "Unconstrained", "MinSize", "PreferredSize" })] string fitMode = null,
            [MCPParam("raycast_target", "Whether this element blocks raycasts")] bool? raycastTarget = null,
            [MCPParam("interactable", "Whether interactive elements are interactable")] bool? interactable = null)
        {
            if (string.IsNullOrEmpty(action))
                throw MCPException.InvalidParams("Action parameter is required.");

            try
            {
                return action.ToLowerInvariant() switch
                {
                    "create_canvas" => CreateCanvas(name, canvasRenderMode),
                    "add_element" => AddElement(parent, elementType, name, text, fontSize, color, spritePath, anchors, position, size, pivot, raycastTarget),
                    "modify_rect" => ModifyRect(target, anchors, position, size, pivot),
                    "add_layout" => AddLayout(target, layoutType, spacing, padding, childAlignment, cellSize, fitMode),
                    "set_text" => SetText(target, text, fontSize, color),
                    "set_image" => SetImage(target, spritePath, color, raycastTarget),
                    "get_hierarchy" => GetHierarchy(target),
                    "remove_element" => RemoveElement(target),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"UGUI operation failed: {ex.Message}");
            }
        }

        #region Actions

        private static object CreateCanvas(string name, string renderMode)
        {
            name = name ?? "Canvas";

            var canvasGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(canvasGo, $"Create Canvas '{name}'");

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = renderMode?.ToLowerInvariant() switch
            {
                "screenspacecamera" => RenderMode.ScreenSpaceCamera,
                "worldspace" => RenderMode.WorldSpace,
                _ => RenderMode.ScreenSpaceOverlay
            };

            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            // Add EventSystem if none exists
            if (UnityEngine.Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length == 0)
            {
                var eventSystemGo = new GameObject("EventSystem");
                Undo.RegisterCreatedObjectUndo(eventSystemGo, "Create EventSystem");
                eventSystemGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                eventSystemGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            }

            return new
            {
                success = true,
                instanceId = canvasGo.GetInstanceID(),
                name = canvasGo.name,
                renderMode = canvas.renderMode.ToString()
            };
        }

        private static object AddElement(string parent, string elementType, string name, string text, int? fontSize, string color, string spritePath, object anchors, object position, object size, object pivot, bool? raycastTarget)
        {
            if (string.IsNullOrEmpty(elementType))
                throw MCPException.InvalidParams("element_type is required for add_element action.");

            var parentTransform = FindUIElement(parent);
            if (parentTransform == null)
            {
                // Find first canvas as default parent
                var canvas = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault();
                if (canvas == null)
                    throw MCPException.InvalidParams("No Canvas found. Create a canvas first with action 'create_canvas'.");
                parentTransform = canvas.GetComponent<RectTransform>();
            }

            GameObject element = elementType switch
            {
                "Button" => CreateButton(parentTransform, name ?? "Button", text ?? "Button", fontSize, color),
                "Text" => CreateText(parentTransform, name ?? "Text", text ?? "New Text", fontSize, color),
                "Image" => CreateImage(parentTransform, name ?? "Image", spritePath, color),
                "Panel" => CreatePanel(parentTransform, name ?? "Panel", color),
                "ScrollView" => CreateScrollView(parentTransform, name ?? "ScrollView"),
                "InputField" => CreateInputField(parentTransform, name ?? "InputField", text),
                "Slider" => CreateSlider(parentTransform, name ?? "Slider"),
                "Toggle" => CreateToggle(parentTransform, name ?? "Toggle", text ?? "Toggle"),
                "Dropdown" => CreateDropdown(parentTransform, name ?? "Dropdown"),
                "RawImage" => CreateRawImage(parentTransform, name ?? "RawImage"),
                _ => throw MCPException.InvalidParams($"Unknown element_type: '{elementType}'. Valid: Button, Text, Image, Panel, ScrollView, InputField, Slider, Toggle, Dropdown, RawImage")
            };

            Undo.RegisterCreatedObjectUndo(element, $"Create UI {elementType}");

            var rect = element.GetComponent<RectTransform>();
            if (rect != null)
            {
                ApplyAnchors(rect, anchors);
                ApplyPosition(rect, position);
                ApplySize(rect, size);
                ApplyPivot(rect, pivot);
            }

            if (raycastTarget.HasValue)
            {
                var graphic = element.GetComponent<Graphic>();
                if (graphic != null) graphic.raycastTarget = raycastTarget.Value;
            }

            return new
            {
                success = true,
                instanceId = element.GetInstanceID(),
                name = element.name,
                elementType,
                parentName = parentTransform.name
            };
        }

        private static object ModifyRect(string target, object anchors, object position, object size, object pivot)
        {
            var rect = FindUIElement(target);
            if (rect == null)
                throw MCPException.InvalidParams($"UI element not found: '{target}'");

            Undo.RecordObject(rect, $"Modify RectTransform '{rect.name}'");

            ApplyAnchors(rect, anchors);
            ApplyPosition(rect, position);
            ApplySize(rect, size);
            ApplyPivot(rect, pivot);

            return new
            {
                success = true,
                name = rect.name,
                anchoredPosition = new { x = rect.anchoredPosition.x, y = rect.anchoredPosition.y },
                sizeDelta = new { width = rect.sizeDelta.x, height = rect.sizeDelta.y },
                anchorMin = new { x = rect.anchorMin.x, y = rect.anchorMin.y },
                anchorMax = new { x = rect.anchorMax.x, y = rect.anchorMax.y },
                pivot = new { x = rect.pivot.x, y = rect.pivot.y }
            };
        }

        private static object AddLayout(string target, string layoutType, float? spacing, object padding, string childAlignment, object cellSize, string fitMode)
        {
            if (string.IsNullOrEmpty(layoutType))
                throw MCPException.InvalidParams("layout_type is required for add_layout action.");

            var rect = FindUIElement(target);
            if (rect == null)
                throw MCPException.InvalidParams($"UI element not found: '{target}'");

            var go = rect.gameObject;
            Undo.RecordObject(go, $"Add {layoutType} Layout to '{go.name}'");

            HorizontalOrVerticalLayoutGroup layout = null;
            GridLayoutGroup gridLayout = null;

            switch (layoutType)
            {
                case "Vertical":
                    layout = Undo.AddComponent<VerticalLayoutGroup>(go);
                    break;
                case "Horizontal":
                    layout = Undo.AddComponent<HorizontalLayoutGroup>(go);
                    break;
                case "Grid":
                    gridLayout = Undo.AddComponent<GridLayoutGroup>(go);
                    break;
                default:
                    throw MCPException.InvalidParams($"Unknown layout_type: '{layoutType}'. Valid: Vertical, Horizontal, Grid");
            }

            if (layout != null)
            {
                if (spacing.HasValue) layout.spacing = spacing.Value;
                ApplyPadding(layout.padding, padding);
                if (!string.IsNullOrEmpty(childAlignment) && Enum.TryParse<TextAnchor>(childAlignment, true, out var align))
                    layout.childAlignment = align;
            }

            if (gridLayout != null)
            {
                if (spacing.HasValue) gridLayout.spacing = new Vector2(spacing.Value, spacing.Value);
                ApplyPadding(gridLayout.padding, padding);
                if (!string.IsNullOrEmpty(childAlignment) && Enum.TryParse<TextAnchor>(childAlignment, true, out var align))
                    gridLayout.childAlignment = align;
                ApplyCellSize(gridLayout, cellSize);
            }

            if (!string.IsNullOrEmpty(fitMode))
            {
                var fitter = go.GetComponent<ContentSizeFitter>() ?? Undo.AddComponent<ContentSizeFitter>(go);
                if (Enum.TryParse<ContentSizeFitter.FitMode>(fitMode, true, out var mode))
                {
                    fitter.horizontalFit = mode;
                    fitter.verticalFit = mode;
                }
            }

            return new { success = true, name = go.name, layoutType };
        }

        private static object SetText(string target, string text, int? fontSize, string color)
        {
            var rect = FindUIElement(target);
            if (rect == null)
                throw MCPException.InvalidParams($"UI element not found: '{target}'");

            // Try TMPro first, then legacy Text
            var tmpText = rect.GetComponentInChildren<TMP_Text>();
            if (tmpText != null)
            {
                Undo.RecordObject(tmpText, $"Set Text on '{rect.name}'");
                if (text != null) tmpText.text = text;
                if (fontSize.HasValue) tmpText.fontSize = fontSize.Value;
                if (!string.IsNullOrEmpty(color)) tmpText.color = ParseColor(color);
                return new { success = true, name = rect.name, text = tmpText.text, fontSize = tmpText.fontSize, usingTMP = true };
            }

            var legacyText = rect.GetComponentInChildren<Text>();
            if (legacyText != null)
            {
                Undo.RecordObject(legacyText, $"Set Text on '{rect.name}'");
                if (text != null) legacyText.text = text;
                if (fontSize.HasValue) legacyText.fontSize = fontSize.Value;
                if (!string.IsNullOrEmpty(color)) legacyText.color = ParseColor(color);
                return new { success = true, name = rect.name, text = legacyText.text, fontSize = legacyText.fontSize, usingTMP = false };
            }

            throw MCPException.InvalidParams($"No Text or TMP_Text component found on '{rect.name}' or its children.");
        }

        private static object SetImage(string target, string spritePath, string color, bool? raycastTarget)
        {
            var rect = FindUIElement(target);
            if (rect == null)
                throw MCPException.InvalidParams($"UI element not found: '{target}'");

            var image = rect.GetComponent<Image>();
            if (image == null)
                throw MCPException.InvalidParams($"No Image component found on '{rect.name}'.");

            Undo.RecordObject(image, $"Set Image on '{rect.name}'");

            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite == null)
                    throw MCPException.InvalidParams($"Sprite not found at: '{spritePath}'");
                image.sprite = sprite;
            }

            if (!string.IsNullOrEmpty(color))
                image.color = ParseColor(color);

            if (raycastTarget.HasValue)
                image.raycastTarget = raycastTarget.Value;

            return new { success = true, name = rect.name, sprite = image.sprite?.name, color = ColorUtility.ToHtmlStringRGBA(image.color) };
        }

        private static object GetHierarchy(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                // Return all canvases
                var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
                var result = new List<object>();
                foreach (var canvas in canvases)
                {
                    result.Add(BuildHierarchyNode(canvas.GetComponent<RectTransform>(), 0, 3));
                }
                return new { success = true, canvasCount = canvases.Length, hierarchy = result };
            }

            var rect = FindUIElement(target);
            if (rect == null)
                throw MCPException.InvalidParams($"UI element not found: '{target}'");

            return new { success = true, hierarchy = BuildHierarchyNode(rect, 0, 5) };
        }

        private static object RemoveElement(string target)
        {
            var rect = FindUIElement(target);
            if (rect == null)
                throw MCPException.InvalidParams($"UI element not found: '{target}'");

            string elementName = rect.name;
            Undo.DestroyObjectImmediate(rect.gameObject);

            return new { success = true, removed = elementName };
        }

        #endregion

        #region Element Creation Helpers

        private static GameObject CreateButton(RectTransform parent, string name, string text, int? fontSize, string color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 30);

            var image = go.GetComponent<Image>();
            image.color = !string.IsNullOrEmpty(color) ? ParseColor(color) : new Color(1f, 1f, 1f, 1f);

            // Add text child using TMPro
            var textGo = new GameObject("Text (TMP)", typeof(RectTransform), typeof(CanvasRenderer));
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize ?? 24;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.black;

            return go;
        }

        private static GameObject CreateText(RectTransform parent, string name, string text, int? fontSize, string color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize ?? 24;
            tmp.color = !string.IsNullOrEmpty(color) ? ParseColor(color) : Color.white;
            tmp.raycastTarget = false;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 50);

            return go;
        }

        private static GameObject CreateImage(RectTransform parent, string name, string spritePath, string color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);

            var image = go.GetComponent<Image>();
            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite != null) image.sprite = sprite;
            }
            image.color = !string.IsNullOrEmpty(color) ? ParseColor(color) : Color.white;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(100, 100);

            return go;
        }

        private static GameObject CreatePanel(RectTransform parent, string name, string color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);

            var image = go.GetComponent<Image>();
            image.color = !string.IsNullOrEmpty(color) ? ParseColor(color) : new Color(1f, 1f, 1f, 0.392f);

            // Stretch to fill parent
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            return go;
        }

        private static GameObject CreateScrollView(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 200);

            var scrollRect = go.GetComponent<ScrollRect>();
            var image = go.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.392f);

            // Viewport
            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(go.transform, false);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.sizeDelta = Vector2.zero;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.GetComponent<Image>().color = Color.white;
            viewport.GetComponent<Mask>().showMaskGraphic = false;

            // Content
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = new Vector2(0, 300);

            scrollRect.viewport = vpRect;
            scrollRect.content = contentRect;

            return go;
        }

        private static GameObject CreateInputField(RectTransform parent, string name, string placeholder)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 30);

            var image = go.GetComponent<Image>();
            image.color = Color.white;

            // Text area
            var textArea = new GameObject("Text Area", typeof(RectTransform));
            textArea.transform.SetParent(go.transform, false);
            var textAreaRect = textArea.GetComponent<RectTransform>();
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            textAreaRect.offsetMin = new Vector2(10, 6);
            textAreaRect.offsetMax = new Vector2(-10, -7);

            // Placeholder
            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer));
            placeholderGo.transform.SetParent(textArea.transform, false);
            var phRect = placeholderGo.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.sizeDelta = Vector2.zero;
            phRect.offsetMin = Vector2.zero;
            phRect.offsetMax = Vector2.zero;
            var phText = placeholderGo.AddComponent<TextMeshProUGUI>();
            phText.text = placeholder ?? "Enter text...";
            phText.fontStyle = FontStyles.Italic;
            phText.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);
            phText.fontSize = 14;

            // Text
            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            textGo.transform.SetParent(textArea.transform, false);
            var tRect = textGo.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.sizeDelta = Vector2.zero;
            tRect.offsetMin = Vector2.zero;
            tRect.offsetMax = Vector2.zero;
            var tText = textGo.AddComponent<TextMeshProUGUI>();
            tText.fontSize = 14;
            tText.color = Color.black;

            var inputField = go.AddComponent<TMP_InputField>();
            inputField.textViewport = textAreaRect;
            inputField.textComponent = tText;
            inputField.placeholder = phText;

            return go;
        }

        private static GameObject CreateSlider(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 20);

            // Background
            var bg = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.25f);
            bgRect.anchorMax = new Vector2(1, 0.75f);
            bgRect.sizeDelta = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.78f, 0.78f, 0.78f);

            // Fill Area
            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var faRect = fillArea.GetComponent<RectTransform>();
            faRect.anchorMin = new Vector2(0, 0.25f);
            faRect.anchorMax = new Vector2(1, 0.75f);
            faRect.offsetMin = new Vector2(5, 0);
            faRect.offsetMax = new Vector2(-15, 0);

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.sizeDelta = new Vector2(10, 0);
            fill.GetComponent<Image>().color = new Color(0.26f, 0.52f, 0.96f);

            // Handle
            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(go.transform, false);
            var haRect = handleArea.GetComponent<RectTransform>();
            haRect.anchorMin = Vector2.zero;
            haRect.anchorMax = Vector2.one;
            haRect.offsetMin = new Vector2(10, 0);
            haRect.offsetMax = new Vector2(-10, 0);

            var handle = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            handle.transform.SetParent(handleArea.transform, false);
            var hRect = handle.GetComponent<RectTransform>();
            hRect.sizeDelta = new Vector2(20, 0);
            handle.GetComponent<Image>().color = Color.white;

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = hRect;
            slider.targetGraphic = handle.GetComponent<Image>();

            return go;
        }

        private static GameObject CreateToggle(RectTransform parent, string name, string label)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Toggle));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 20);

            // Background
            var bg = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 1);
            bgRect.anchorMax = new Vector2(0, 1);
            bgRect.pivot = new Vector2(0, 1);
            bgRect.sizeDelta = new Vector2(20, 20);
            bgRect.anchoredPosition = Vector2.zero;
            bg.GetComponent<Image>().color = Color.white;

            // Checkmark
            var checkmark = new GameObject("Checkmark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            checkmark.transform.SetParent(bg.transform, false);
            var cmRect = checkmark.GetComponent<RectTransform>();
            cmRect.anchorMin = Vector2.zero;
            cmRect.anchorMax = Vector2.one;
            cmRect.sizeDelta = Vector2.zero;
            checkmark.GetComponent<Image>().color = new Color(0.26f, 0.52f, 0.96f);

            // Label
            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            labelGo.transform.SetParent(go.transform, false);
            var lRect = labelGo.GetComponent<RectTransform>();
            lRect.anchorMin = Vector2.zero;
            lRect.anchorMax = Vector2.one;
            lRect.offsetMin = new Vector2(23, 1);
            lRect.offsetMax = new Vector2(-5, -2);
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.color = Color.white;

            var toggle = go.GetComponent<Toggle>();
            toggle.targetGraphic = bg.GetComponent<Image>();
            toggle.graphic = checkmark.GetComponent<Image>();
            toggle.isOn = true;

            return go;
        }

        private static GameObject CreateDropdown(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 30);

            var image = go.GetComponent<Image>();
            image.color = Color.white;

            // Label
            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            labelGo.transform.SetParent(go.transform, false);
            var lRect = labelGo.GetComponent<RectTransform>();
            lRect.anchorMin = Vector2.zero;
            lRect.anchorMax = Vector2.one;
            lRect.offsetMin = new Vector2(10, 6);
            lRect.offsetMax = new Vector2(-25, -7);
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "Option A";
            tmp.fontSize = 14;
            tmp.color = Color.black;

            var dropdown = go.AddComponent<TMP_Dropdown>();
            dropdown.captionText = tmp;
            dropdown.options.Add(new TMP_Dropdown.OptionData("Option A"));
            dropdown.options.Add(new TMP_Dropdown.OptionData("Option B"));
            dropdown.options.Add(new TMP_Dropdown.OptionData("Option C"));

            return go;
        }

        private static GameObject CreateRawImage(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(100, 100);

            return go;
        }

        #endregion

        #region Utility Methods

        private static RectTransform FindUIElement(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return null;

            // Try instance ID
            if (int.TryParse(identifier, out int instanceId))
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                return obj?.GetComponent<RectTransform>();
            }

            // Try path/name search
            var go = GameObject.Find(identifier);
            return go?.GetComponent<RectTransform>();
        }

        private static void ApplyAnchors(RectTransform rect, object anchors)
        {
            if (anchors == null) return;

            if (anchors is string preset)
            {
                switch (preset.ToLowerInvariant())
                {
                    case "stretch":
                        rect.anchorMin = Vector2.zero;
                        rect.anchorMax = Vector2.one;
                        rect.sizeDelta = Vector2.zero;
                        rect.offsetMin = Vector2.zero;
                        rect.offsetMax = Vector2.zero;
                        break;
                    case "center":
                        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                        break;
                    case "top-left":
                        rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
                        break;
                    case "top-right":
                        rect.anchorMin = rect.anchorMax = new Vector2(1, 1);
                        break;
                    case "bottom-left":
                        rect.anchorMin = rect.anchorMax = new Vector2(0, 0);
                        break;
                    case "bottom-right":
                        rect.anchorMin = rect.anchorMax = new Vector2(1, 0);
                        break;
                    case "top-center":
                        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
                        break;
                    case "bottom-center":
                        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0);
                        break;
                    case "left":
                        rect.anchorMin = rect.anchorMax = new Vector2(0, 0.5f);
                        break;
                    case "right":
                        rect.anchorMin = rect.anchorMax = new Vector2(1, 0.5f);
                        break;
                    case "stretch-horizontal":
                        rect.anchorMin = new Vector2(0, 0.5f);
                        rect.anchorMax = new Vector2(1, 0.5f);
                        break;
                    case "stretch-vertical":
                        rect.anchorMin = new Vector2(0.5f, 0);
                        rect.anchorMax = new Vector2(0.5f, 1);
                        break;
                }
            }
            else if (anchors is Newtonsoft.Json.Linq.JObject jObj)
            {
                float minX = jObj.Value<float>("minX");
                float minY = jObj.Value<float>("minY");
                float maxX = jObj.Value<float>("maxX");
                float maxY = jObj.Value<float>("maxY");
                rect.anchorMin = new Vector2(minX, minY);
                rect.anchorMax = new Vector2(maxX, maxY);
            }
        }

        private static void ApplyPosition(RectTransform rect, object position)
        {
            if (position == null) return;
            var vec = ParseVector2(position);
            if (vec.HasValue) rect.anchoredPosition = vec.Value;
        }

        private static void ApplySize(RectTransform rect, object size)
        {
            if (size == null) return;
            var vec = ParseVector2(size);
            if (vec.HasValue) rect.sizeDelta = vec.Value;
        }

        private static void ApplyPivot(RectTransform rect, object pivot)
        {
            if (pivot == null) return;
            var vec = ParseVector2(pivot);
            if (vec.HasValue) rect.pivot = vec.Value;
        }

        private static void ApplyPadding(RectOffset padding, object paddingData)
        {
            if (paddingData == null) return;
            if (paddingData is Newtonsoft.Json.Linq.JArray arr && arr.Count == 4)
            {
                padding.left = (int)arr[0];
                padding.right = (int)arr[1];
                padding.top = (int)arr[2];
                padding.bottom = (int)arr[3];
            }
        }

        private static void ApplyCellSize(GridLayoutGroup grid, object cellSize)
        {
            if (cellSize == null) return;
            var vec = ParseVector2(cellSize);
            if (vec.HasValue) grid.cellSize = vec.Value;
        }

        private static Vector2? ParseVector2(object data)
        {
            if (data is Newtonsoft.Json.Linq.JArray arr && arr.Count >= 2)
                return new Vector2((float)arr[0], (float)arr[1]);
            if (data is Newtonsoft.Json.Linq.JObject obj)
                return new Vector2(obj.Value<float>("x"), obj.Value<float>("y"));
            return null;
        }

        private static Color ParseColor(string color)
        {
            if (string.IsNullOrEmpty(color)) return Color.white;

            // Named colors
            switch (color.ToLowerInvariant())
            {
                case "red": return Color.red;
                case "green": return Color.green;
                case "blue": return Color.blue;
                case "white": return Color.white;
                case "black": return Color.black;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                case "gray": case "grey": return Color.gray;
                case "clear": return Color.clear;
            }

            // Hex color
            if (!color.StartsWith("#")) color = "#" + color;
            if (ColorUtility.TryParseHtmlString(color, out var parsed))
                return parsed;

            return Color.white;
        }

        private static object BuildHierarchyNode(RectTransform rect, int depth, int maxDepth)
        {
            if (rect == null) return null;

            var components = new List<string>();
            foreach (var comp in rect.GetComponents<Component>())
            {
                if (comp == null) continue;
                var type = comp.GetType();
                if (type != typeof(RectTransform) && type != typeof(CanvasRenderer))
                    components.Add(type.Name);
            }

            var node = new Dictionary<string, object>
            {
                ["name"] = rect.name,
                ["instanceId"] = rect.gameObject.GetInstanceID(),
                ["active"] = rect.gameObject.activeSelf,
                ["components"] = components,
                ["rect"] = new
                {
                    anchoredPosition = new { x = rect.anchoredPosition.x, y = rect.anchoredPosition.y },
                    sizeDelta = new { w = rect.sizeDelta.x, h = rect.sizeDelta.y },
                    anchorMin = new { x = rect.anchorMin.x, y = rect.anchorMin.y },
                    anchorMax = new { x = rect.anchorMax.x, y = rect.anchorMax.y }
                }
            };

            if (depth < maxDepth && rect.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < rect.childCount; i++)
                {
                    var childRect = rect.GetChild(i) as RectTransform;
                    if (childRect != null)
                        children.Add(BuildHierarchyNode(childRect, depth + 1, maxDepth));
                }
                node["children"] = children;
            }
            else if (rect.childCount > 0)
            {
                node["childCount"] = rect.childCount;
            }

            return node;
        }

        #endregion
    }
}
