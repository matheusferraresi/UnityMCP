using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnixxtyMCP.Editor.Core;
using UnixxtyMCP.Editor.Utilities;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Manages Unity UI Toolkit assets and runtime panels: create PanelSettings,
    /// add UIDocuments, query runtime VisualElement trees, validate USS, and scaffold screens.
    /// </summary>
    public static class ManageUIToolkit
    {
        #region Element Reference Registry

        private static readonly Dictionary<string, VisualElement> s_refRegistry = new Dictionary<string, VisualElement>();
        private static int s_refCounter;
        private static string s_lastPanel;
        private const int MaxElements = 300;

        private static void ClearRefRegistry(string panelId)
        {
            if (s_lastPanel != panelId)
            {
                s_refRegistry.Clear();
                s_refCounter = 0;
                s_lastPanel = panelId;
            }
        }

        private static string RegisterElement(VisualElement element)
        {
            string refId = $"r{s_refCounter++}";
            s_refRegistry[refId] = element;
            return refId;
        }

        #endregion

        [MCPTool("manage_uitoolkit",
            "Manage Unity UI Toolkit: create PanelSettings/UIDocuments, query runtime panels, preview hidden UI, validate USS, scaffold screens, debug overlays",
            Category = "UI", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "create_panel_settings", "create_uidocument", "list_uidocuments", "query_panel",
                    "set_element_style", "preview_panel", "assign_theme", "inspect_uss", "scaffold_screen", "debug_overlay" })]
            string action,
            [MCPParam("path", "Asset path for create/inspect operations (e.g. 'Assets/UI/Panel.asset')")] string path = null,
            [MCPParam("target", "GameObject name/path/instanceId for UIDocument operations")] string target = null,
            [MCPParam("panel_settings_path", "Path to PanelSettings asset")] string panelSettingsPath = null,
            [MCPParam("uxml_path", "Path to UXML source asset")] string uxmlPath = null,
            [MCPParam("theme_path", "Path to ThemeStyleSheet (.tss) asset")] string themePath = null,
            [MCPParam("sort_order", "UIDocument sort order (higher renders on top)")] int? sortOrder = null,
            [MCPParam("selector", "USS selector to query elements (e.g. '#my-element', '.my-class', 'Label')")] string selector = null,
            [MCPParam("ref_id", "Element reference ID from a previous query_panel result")] string refId = null,
            [MCPParam("max_depth", "Maximum tree depth for query_panel (default 3)")] int? maxDepth = null,
            [MCPParam("property", "USS style property name (e.g. 'background-color', 'display', 'opacity')")] string property = null,
            [MCPParam("value", "Value to set for a style property")] string value = null,
            [MCPParam("show", "For preview_panel: true to force-show, false to restore hidden state")] bool? show = null,
            [MCPParam("reference_resolution", "Reference resolution as [width,height] (e.g. [1920,1080])")] object referenceResolution = null,
            [MCPParam("scale_mode", "PanelSettings scale mode: ConstantPixelSize, ConstantPhysicalSize, ScaleWithScreenSize",
                Enum = new[] { "ConstantPixelSize", "ConstantPhysicalSize", "ScaleWithScreenSize" })]
            string scaleMode = "ScaleWithScreenSize",
            [MCPParam("match", "Screen match mode value (0=width, 1=height, 0.5=both)")] float? match = null,
            [MCPParam("name", "Name for scaffold_screen")] string name = null,
            [MCPParam("namespace", "C# namespace for scaffold_screen")] string ns = null,
            [MCPParam("base_class", "Base class for scaffold_screen controller (default: MonoBehaviour)")] string baseClass = null,
            [MCPParam("elements", "JSON array of elements for scaffold_screen: [{name, type, text}]")] object elements = null,
            [MCPParam("filter", "Filter mode for query_panel: 'visible' (default) or 'all'")] string filter = "visible")
        {
            if (string.IsNullOrEmpty(action))
                throw MCPException.InvalidParams("Action parameter is required.");

            try
            {
                return action.ToLowerInvariant() switch
                {
                    "create_panel_settings" => CreatePanelSettings(path, referenceResolution, scaleMode, match, themePath),
                    "create_uidocument" => CreateUIDocument(target, panelSettingsPath, uxmlPath, sortOrder),
                    "list_uidocuments" => ListUIDocuments(),
                    "query_panel" => QueryPanel(target, selector, refId, maxDepth, filter),
                    "set_element_style" => SetElementStyle(target, selector, refId, property, value),
                    "preview_panel" => PreviewPanel(target, show),
                    "assign_theme" => AssignTheme(panelSettingsPath ?? path, themePath),
                    "inspect_uss" => InspectUSS(path, panelSettingsPath),
                    "scaffold_screen" => ScaffoldScreen(name, path, ns, baseClass, themePath, elements),
                    "debug_overlay" => DebugOverlay(target, show),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"UI Toolkit operation failed: {ex.Message}");
            }
        }

        #region create_panel_settings

        private static object CreatePanelSettings(string path, object refRes, string scaleMode, float? match, string themePath)
        {
            if (string.IsNullOrEmpty(path))
                throw MCPException.InvalidParams("'path' is required (e.g. 'Assets/UI/MyPanel.asset').");

            path = PathUtilities.NormalizePath(path);
            if (!path.EndsWith(".asset"))
                path += ".asset";

            string folder = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!PathUtilities.EnsureFolderExists(folder, out string folderErr))
                throw new MCPException($"Failed to create folder: {folderErr}");

            // Check for existing asset
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (existing != null)
                throw MCPException.InvalidParams($"PanelSettings already exists at '{path}'. Use assign_theme to modify it.");

            var panel = ScriptableObject.CreateInstance<PanelSettings>();

            // Scale mode
            if (!string.IsNullOrEmpty(scaleMode))
            {
                panel.scaleMode = scaleMode.ToLowerInvariant() switch
                {
                    "constantpixelsize" => PanelScaleMode.ConstantPixelSize,
                    "constantphysicalsize" => PanelScaleMode.ConstantPhysicalSize,
                    _ => PanelScaleMode.ScaleWithScreenSize
                };
            }

            // Reference resolution
            if (refRes != null)
            {
                var vec = ParseVector2Int(refRes);
                if (vec.HasValue)
                    panel.referenceResolution = new Vector2Int(vec.Value.x, vec.Value.y);
            }
            else
            {
                panel.referenceResolution = new Vector2Int(1920, 1080);
            }

            // Screen match mode
            if (match.HasValue)
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;

            // Theme
            if (!string.IsNullOrEmpty(themePath))
            {
                var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(PathUtilities.NormalizePath(themePath));
                if (theme != null)
                    panel.themeStyleSheet = theme;
            }

            AssetDatabase.CreateAsset(panel, path);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                path,
                scaleMode = panel.scaleMode.ToString(),
                referenceResolution = new { x = panel.referenceResolution.x, y = panel.referenceResolution.y },
                theme = panel.themeStyleSheet != null ? AssetDatabase.GetAssetPath(panel.themeStyleSheet) : null
            };
        }

        #endregion

        #region create_uidocument

        private static object CreateUIDocument(string target, string panelSettingsPath, string uxmlPath, int? sortOrder)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required (GameObject name, path, or instance ID).");

            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject not found: '{target}'");

            var existing = go.GetComponent<UIDocument>();
            if (existing != null)
                throw MCPException.InvalidParams($"'{go.name}' already has a UIDocument component.");

            Undo.RecordObject(go, $"Add UIDocument to '{go.name}'");
            var doc = Undo.AddComponent<UIDocument>(go);

            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PathUtilities.NormalizePath(panelSettingsPath));
                if (panel != null)
                    doc.panelSettings = panel;
                else
                    Debug.LogWarning($"[ManageUIToolkit] PanelSettings not found at '{panelSettingsPath}'");
            }

            if (!string.IsNullOrEmpty(uxmlPath))
            {
                var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PathUtilities.NormalizePath(uxmlPath));
                if (uxml != null)
                    doc.visualTreeAsset = uxml;
                else
                    Debug.LogWarning($"[ManageUIToolkit] UXML not found at '{uxmlPath}'");
            }

            if (sortOrder.HasValue)
                doc.sortingOrder = sortOrder.Value;

            EditorUtility.SetDirty(go);

            return new
            {
                success = true,
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                panelSettings = doc.panelSettings != null ? AssetDatabase.GetAssetPath(doc.panelSettings) : null,
                visualTreeAsset = doc.visualTreeAsset != null ? AssetDatabase.GetAssetPath(doc.visualTreeAsset) : null,
                sortingOrder = doc.sortingOrder
            };
        }

        #endregion

        #region list_uidocuments

        private static object ListUIDocuments()
        {
            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            var result = new List<object>();

            foreach (var doc in docs)
            {
                if (doc == null) continue;
                result.Add(new
                {
                    gameObject = doc.gameObject.name,
                    instanceId = doc.gameObject.GetInstanceID(),
                    active = doc.gameObject.activeInHierarchy,
                    enabled = doc.enabled,
                    panelSettings = doc.panelSettings != null ? AssetDatabase.GetAssetPath(doc.panelSettings) : null,
                    visualTreeAsset = doc.visualTreeAsset != null ? AssetDatabase.GetAssetPath(doc.visualTreeAsset) : null,
                    sortingOrder = doc.sortingOrder,
                    hasRootVisualElement = doc.rootVisualElement != null,
                    rootChildCount = doc.rootVisualElement?.childCount ?? 0
                });
            }

            return new { success = true, count = result.Count, documents = result };
        }

        #endregion

        #region query_panel (Issue #27 — Runtime Panel Inspection)

        private static object QueryPanel(string target, string selector, string refId, int? maxDepth, string filter)
        {
            // If no target specified, list all available UIDocument panels
            if (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(refId))
            {
                return ListUIDocuments();
            }

            // Resolve the UIDocument's root VisualElement
            VisualElement root;
            string panelId;

            if (!string.IsNullOrEmpty(refId))
            {
                // Drill-down mode: use previously registered ref
                if (!s_refRegistry.TryGetValue(refId, out var refElement))
                    throw MCPException.InvalidParams($"Invalid ref_id '{refId}'. Refs expire when querying a different panel.");

                root = refElement;
                panelId = s_lastPanel; // keep same panel context
            }
            else
            {
                var doc = FindUIDocument(target);
                root = doc.rootVisualElement;
                panelId = doc.gameObject.name;

                if (root == null)
                    throw new MCPException($"UIDocument '{doc.gameObject.name}' has no rootVisualElement. Is it enabled with a valid PanelSettings?");

                ClearRefRegistry(panelId);
            }

            int depth = maxDepth ?? (string.IsNullOrEmpty(refId) ? 3 : 5);
            bool filterVisible = filter != "all";

            // Selector search mode
            if (!string.IsNullOrEmpty(selector))
            {
                var matches = QueryBySelector(root, selector, filterVisible);
                return new
                {
                    success = true,
                    panel = panelId,
                    selector,
                    count = matches.Count,
                    matches = matches.Take(50).Select(e => BuildElementData(e, filterVisible)).ToList()
                };
            }

            // Tree mode
            int elementCount = 0;
            bool truncated = false;
            var tree = BuildTree(root, 0, depth, filterVisible, ref elementCount, ref truncated);

            return new
            {
                success = true,
                panel = panelId,
                depth,
                elementCount,
                truncated,
                tree
            };
        }

        private static UIDocument FindUIDocument(string target)
        {
            // Try by GameObject resolution first
            var go = GameObjectResolver.Resolve(target);
            if (go != null)
            {
                var doc = go.GetComponent<UIDocument>();
                if (doc != null) return doc;
                throw MCPException.InvalidParams($"GameObject '{go.name}' has no UIDocument component.");
            }

            // Try fuzzy match on UIDocument gameObject names
            var allDocs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            var match = allDocs.FirstOrDefault(d =>
                d != null && d.gameObject.name.Contains(target, StringComparison.OrdinalIgnoreCase));

            if (match != null) return match;

            var available = allDocs
                .Where(d => d != null)
                .Select(d => d.gameObject.name)
                .ToList();

            throw MCPException.InvalidParams($"UIDocument not found: '{target}'. Available: [{string.Join(", ", available)}]");
        }

        private static List<VisualElement> QueryBySelector(VisualElement root, string selector, bool filterVisible)
        {
            var results = new List<VisualElement>();

            if (selector.StartsWith("#"))
            {
                // Name selector
                string nameQuery = selector.Substring(1);
                root.Query(nameQuery).ForEach(e => results.Add(e));
            }
            else if (selector.StartsWith("."))
            {
                // Class selector
                string classQuery = selector.Substring(1);
                root.Query(null, classQuery).ForEach(e => results.Add(e));
            }
            else
            {
                // Type name search (recursive)
                CollectByType(root, selector, results, 0, 20);
            }

            if (filterVisible)
                results.RemoveAll(e => !IsVisible(e));

            return results;
        }

        private static void CollectByType(VisualElement element, string typeName, List<VisualElement> results, int depth, int maxDepth)
        {
            if (depth > maxDepth || results.Count >= MaxElements) return;

            if (element.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                results.Add(element);

            foreach (var child in element.Children())
                CollectByType(child, typeName, results, depth + 1, maxDepth);
        }

        private static object BuildTree(VisualElement element, int depth, int maxDepth, bool filterVisible,
            ref int count, ref bool truncated)
        {
            if (element == null) return null;
            if (count >= MaxElements) { truncated = true; return null; }

            if (filterVisible && !IsVisible(element) && !HasVisibleDescendant(element, maxDepth - depth))
                return null;

            count++;
            string rId = RegisterElement(element);

            var data = new Dictionary<string, object> { { "ref", rId }, { "t", element.GetType().Name } };

            if (!string.IsNullOrEmpty(element.name))
                data["n"] = element.name;

            string text = GetElementText(element);
            if (text != null)
                data["txt"] = text.Length > 40 ? text.Substring(0, 37) + "..." : text;

            var classes = element.GetClasses()
                .Where(c => !c.StartsWith("unity-"))
                .Take(3)
                .ToList();
            if (classes.Count > 0)
                data["cls"] = classes;

            // Show display:none explicitly (critical for hidden panels)
            if (element.resolvedStyle.display == DisplayStyle.None)
                data["hidden"] = true;

            // Include key resolved styles for layout debugging
            var rs = element.resolvedStyle;
            var style = new Dictionary<string, object>();
            if (rs.opacity < 1f) style["opacity"] = MathF.Round(rs.opacity, 2);
            if (rs.fontSize > 0) style["fontSize"] = MathF.Round(rs.fontSize, 1);
            if (rs.width > 0 && !float.IsNaN(rs.width)) style["w"] = MathF.Round(rs.width, 1);
            if (rs.height > 0 && !float.IsNaN(rs.height)) style["h"] = MathF.Round(rs.height, 1);
            if (rs.backgroundColor.a > 0.01f) style["bg"] = ColorToHex(rs.backgroundColor);
            if (rs.color != Color.clear) style["color"] = ColorToHex(rs.color);
            if (style.Count > 0)
                data["style"] = style;

            if (depth < maxDepth && element.childCount > 0)
            {
                var children = new List<object>();
                foreach (var child in element.Children())
                {
                    if (count >= MaxElements) { truncated = true; break; }
                    var childData = BuildTree(child, depth + 1, maxDepth, filterVisible, ref count, ref truncated);
                    if (childData != null)
                        children.Add(childData);
                }
                if (children.Count > 0)
                    data["c"] = children;
                else if (element.childCount > 0)
                    data["more"] = element.childCount;
            }
            else if (element.childCount > 0)
            {
                data["more"] = element.childCount;
            }

            return data;
        }

        private static object BuildElementData(VisualElement element, bool filterVisible)
        {
            string rId = RegisterElement(element);
            var data = new Dictionary<string, object>
            {
                { "ref", rId },
                { "type", element.GetType().Name }
            };

            if (!string.IsNullOrEmpty(element.name))
                data["name"] = element.name;

            string text = GetElementText(element);
            if (text != null)
                data["text"] = text.Length > 80 ? text.Substring(0, 77) + "..." : text;

            var classes = element.GetClasses().ToList();
            if (classes.Count > 0)
                data["classes"] = classes;

            if (element.childCount > 0)
                data["children"] = element.childCount;

            // Include key computed styles for search results
            var rs = element.resolvedStyle;
            data["computedStyle"] = new
            {
                display = rs.display.ToString(),
                visibility = rs.visibility.ToString(),
                opacity = rs.opacity,
                color = ColorToHex(rs.color),
                backgroundColor = ColorToHex(rs.backgroundColor),
                fontSize = rs.fontSize,
                width = rs.width,
                height = rs.height
            };

            return data;
        }

        #endregion

        #region set_element_style

        private static object SetElementStyle(string target, string selector, string refId, string property, string value)
        {
            if (string.IsNullOrEmpty(property))
                throw MCPException.InvalidParams("'property' is required.");
            if (string.IsNullOrEmpty(value))
                throw MCPException.InvalidParams("'value' is required.");

            VisualElement element = ResolveElement(target, selector, refId);

            ApplyStyleProperty(element, property, value);

            return new
            {
                success = true,
                element = element.name ?? element.GetType().Name,
                property,
                value
            };
        }

        private static void ApplyStyleProperty(VisualElement element, string property, string value)
        {
            var style = element.style;

            switch (property.ToLowerInvariant())
            {
                case "display":
                    style.display = value.ToLowerInvariant() == "none" ? DisplayStyle.None : DisplayStyle.Flex;
                    break;
                case "visibility":
                    style.visibility = value.ToLowerInvariant() == "hidden" ? Visibility.Hidden : Visibility.Visible;
                    break;
                case "opacity":
                    if (float.TryParse(value, out float opacity))
                        style.opacity = opacity;
                    break;
                case "background-color":
                case "backgroundcolor":
                    if (ColorUtility.TryParseHtmlString(value.StartsWith("#") ? value : "#" + value, out var bgColor))
                        style.backgroundColor = bgColor;
                    break;
                case "color":
                    if (ColorUtility.TryParseHtmlString(value.StartsWith("#") ? value : "#" + value, out var fgColor))
                        style.color = fgColor;
                    break;
                case "width":
                    style.width = ParseLength(value);
                    break;
                case "height":
                    style.height = ParseLength(value);
                    break;
                case "min-width":
                case "minwidth":
                    style.minWidth = ParseLength(value);
                    break;
                case "min-height":
                case "minheight":
                    style.minHeight = ParseLength(value);
                    break;
                case "max-width":
                case "maxwidth":
                    style.maxWidth = ParseLength(value);
                    break;
                case "max-height":
                case "maxheight":
                    style.maxHeight = ParseLength(value);
                    break;
                case "font-size":
                case "fontsize":
                    if (float.TryParse(value, out float fs))
                        style.fontSize = fs;
                    break;
                case "flex-grow":
                case "flexgrow":
                    if (float.TryParse(value, out float fg))
                        style.flexGrow = fg;
                    break;
                case "flex-shrink":
                case "flexshrink":
                    if (float.TryParse(value, out float fsh))
                        style.flexShrink = fsh;
                    break;
                case "flex-direction":
                case "flexdirection":
                    style.flexDirection = value.ToLowerInvariant() switch
                    {
                        "row" => FlexDirection.Row,
                        "row-reverse" => FlexDirection.RowReverse,
                        "column-reverse" => FlexDirection.ColumnReverse,
                        _ => FlexDirection.Column
                    };
                    break;
                case "justify-content":
                case "justifycontent":
                    style.justifyContent = value.ToLowerInvariant() switch
                    {
                        "center" => Justify.Center,
                        "flex-end" => Justify.FlexEnd,
                        "space-between" => Justify.SpaceBetween,
                        "space-around" => Justify.SpaceAround,
                        _ => Justify.FlexStart
                    };
                    break;
                case "align-items":
                case "alignitems":
                    style.alignItems = ParseAlign(value);
                    break;
                case "align-self":
                case "alignself":
                    style.alignSelf = ParseAlign(value);
                    break;
                case "margin-left":
                case "marginleft":
                    style.marginLeft = ParseLength(value);
                    break;
                case "margin-right":
                case "marginright":
                    style.marginRight = ParseLength(value);
                    break;
                case "margin-top":
                case "margintop":
                    style.marginTop = ParseLength(value);
                    break;
                case "margin-bottom":
                case "marginbottom":
                    style.marginBottom = ParseLength(value);
                    break;
                case "padding-left":
                case "paddingleft":
                    style.paddingLeft = ParseLength(value);
                    break;
                case "padding-right":
                case "paddingright":
                    style.paddingRight = ParseLength(value);
                    break;
                case "padding-top":
                case "paddingtop":
                    style.paddingTop = ParseLength(value);
                    break;
                case "padding-bottom":
                case "paddingbottom":
                    style.paddingBottom = ParseLength(value);
                    break;
                case "border-radius":
                case "borderradius":
                    var radius = ParseLength(value);
                    style.borderTopLeftRadius = radius;
                    style.borderTopRightRadius = radius;
                    style.borderBottomLeftRadius = radius;
                    style.borderBottomRightRadius = radius;
                    break;
                case "position":
                    style.position = value.ToLowerInvariant() == "absolute" ? Position.Absolute : Position.Relative;
                    break;
                case "-unity-text-align":
                case "unitytextalign":
                    if (Enum.TryParse<TextAnchor>(value, true, out var ta))
                        style.unityTextAlign = ta;
                    break;
                default:
                    throw MCPException.InvalidParams($"Unsupported style property: '{property}'. Supported: display, visibility, opacity, background-color, color, width, height, min/max-width/height, font-size, flex-grow/shrink/direction, justify-content, align-items/self, margin-*, padding-*, border-radius, position, -unity-text-align");
            }
        }

        #endregion

        #region preview_panel (Issue #28)

        private static readonly Dictionary<int, DisplayStyle> s_previewStates = new Dictionary<int, DisplayStyle>();

        private static object PreviewPanel(string target, bool? show)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required (UIDocument GameObject name).");

            var doc = FindUIDocument(target);
            var root = doc.rootVisualElement;
            if (root == null)
                throw new MCPException($"UIDocument '{doc.gameObject.name}' has no rootVisualElement.");

            int key = doc.GetInstanceID();

            if (show == true)
            {
                // Save current state and force-show
                s_previewStates[key] = root.resolvedStyle.display;
                root.style.display = DisplayStyle.Flex;

                // Also force-show children that might be the actual content container
                foreach (var child in root.Children())
                {
                    if (child.resolvedStyle.display == DisplayStyle.None)
                    {
                        int childKey = child.GetHashCode();
                        s_previewStates[childKey] = DisplayStyle.None;
                        child.style.display = DisplayStyle.Flex;
                    }
                }

                return new
                {
                    success = true,
                    action = "preview_show",
                    panel = doc.gameObject.name,
                    message = "Panel forced visible. Use preview_panel with show=false to restore, or take a screenshot now."
                };
            }
            else
            {
                // Restore saved states
                if (s_previewStates.TryGetValue(key, out var savedDisplay))
                {
                    root.style.display = savedDisplay;
                    s_previewStates.Remove(key);
                }

                // Restore child states
                foreach (var child in root.Children())
                {
                    int childKey = child.GetHashCode();
                    if (s_previewStates.TryGetValue(childKey, out var childDisplay))
                    {
                        child.style.display = childDisplay;
                        s_previewStates.Remove(childKey);
                    }
                }

                return new
                {
                    success = true,
                    action = "preview_restore",
                    panel = doc.gameObject.name,
                    message = "Panel display state restored."
                };
            }
        }

        #endregion

        #region assign_theme

        private static object AssignTheme(string panelPath, string themePath)
        {
            if (string.IsNullOrEmpty(panelPath))
                throw MCPException.InvalidParams("'panel_settings_path' (or 'path') is required.");
            if (string.IsNullOrEmpty(themePath))
                throw MCPException.InvalidParams("'theme_path' is required.");

            panelPath = PathUtilities.NormalizePath(panelPath);
            themePath = PathUtilities.NormalizePath(themePath);

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
            if (panel == null)
                throw MCPException.InvalidParams($"PanelSettings not found at '{panelPath}'.");

            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
            if (theme == null)
                throw MCPException.InvalidParams($"ThemeStyleSheet not found at '{themePath}'.");

            Undo.RecordObject(panel, "Assign ThemeStyleSheet");
            panel.themeStyleSheet = theme;
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                panelSettings = panelPath,
                theme = themePath
            };
        }

        #endregion

        #region inspect_uss (Issue #29 — USS Validation)

        private static object InspectUSS(string ussPath, string panelSettingsPath = null)
        {
            if (string.IsNullOrEmpty(ussPath))
                throw MCPException.InvalidParams("'path' is required (path to .uss file).");

            ussPath = PathUtilities.NormalizePath(ussPath);
            string fullPath = Path.Combine(Application.dataPath, "..", ussPath).Replace('\\', '/');

            if (!File.Exists(fullPath))
                throw MCPException.InvalidParams($"USS file not found: '{ussPath}'");

            string ussContent = File.ReadAllText(fullPath);

            // Find all var() references
            var varRefs = new HashSet<string>();
            foreach (Match m in Regex.Matches(ussContent, @"var\(\s*(--[\w-]+)\s*(?:,\s*[^)]+)?\)"))
                varRefs.Add(m.Groups[1].Value);

            // Find custom property definitions in this file
            var localDefs = new HashSet<string>();
            foreach (Match m in Regex.Matches(ussContent, @"(--[\w-]+)\s*:"))
                localDefs.Add(m.Groups[1].Value);

            // Find @import references and trace them
            var imports = new List<string>();
            var importedDefs = new HashSet<string>();
            CollectImportedDefinitions(fullPath, ussContent, imports, importedDefs);

            // Resolve theme chain if panel_settings_path is provided (Issue #32)
            var themeDefs = new HashSet<string>();
            string themeSource = null;
            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                panelSettingsPath = PathUtilities.NormalizePath(panelSettingsPath);
                var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                if (panel != null && panel.themeStyleSheet != null)
                {
                    themeSource = AssetDatabase.GetAssetPath(panel.themeStyleSheet);
                    if (!string.IsNullOrEmpty(themeSource))
                    {
                        // TSS files are USS-compatible — parse their @import chain
                        string tssFullPath = Path.Combine(Application.dataPath, "..", themeSource).Replace('\\', '/');
                        if (File.Exists(tssFullPath))
                        {
                            string tssContent = File.ReadAllText(tssFullPath);
                            // Collect definitions from TSS file itself
                            foreach (Match def in Regex.Matches(tssContent, @"(--[\w-]+)\s*:"))
                                themeDefs.Add(def.Groups[1].Value);
                            // Collect definitions from TSS imports
                            var tssImports = new List<string>();
                            CollectImportedDefinitions(tssFullPath, tssContent, tssImports, themeDefs);
                        }
                    }
                }
            }

            var allDefs = new HashSet<string>(localDefs);
            allDefs.UnionWith(importedDefs);
            allDefs.UnionWith(themeDefs);

            var resolved = varRefs.Where(v => allDefs.Contains(v)).OrderBy(v => v).ToList();
            var unresolved = varRefs.Where(v => !allDefs.Contains(v)).OrderBy(v => v).ToList();

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "path", ussPath },
                { "varReferences", varRefs.Count },
                { "localDefinitions", localDefs.OrderBy(d => d).ToList() },
                { "imports", imports },
                { "importedDefinitions", importedDefs.OrderBy(d => d).ToList() },
                { "resolved", resolved },
                { "unresolved", unresolved },
                { "hasErrors", unresolved.Count > 0 }
            };

            if (themeDefs.Count > 0)
            {
                result["themeSource"] = themeSource;
                result["themeDefinitions"] = themeDefs.OrderBy(d => d).ToList();
            }

            result["message"] = unresolved.Count > 0
                ? $"{unresolved.Count} unresolved var() reference(s). Check spelling or import chain: {string.Join(", ", unresolved)}"
                    + (string.IsNullOrEmpty(panelSettingsPath) ? " Hint: pass panel_settings_path to resolve variables from the theme chain." : "")
                : $"All {resolved.Count} var() references resolved successfully."
                    + (themeDefs.Count > 0 ? $" ({themeDefs.Count} from theme chain)" : "");

            return result;
        }

        private static void CollectImportedDefinitions(string parentFullPath, string content, List<string> imports, HashSet<string> defs)
        {
            foreach (Match m in Regex.Matches(content, @"@import\s+url\([""']([^""']+)[""']\)"))
            {
                string importRef = m.Groups[1].Value;
                imports.Add(importRef);

                string parentDir = Path.GetDirectoryName(parentFullPath);
                string importFullPath = Path.Combine(parentDir, importRef).Replace('\\', '/');

                if (File.Exists(importFullPath))
                {
                    string importContent = File.ReadAllText(importFullPath);
                    foreach (Match def in Regex.Matches(importContent, @"(--[\w-]+)\s*:"))
                        defs.Add(def.Groups[1].Value);
                }
            }
        }

        #endregion

        #region scaffold_screen (Issue #30)

        private static object ScaffoldScreen(string screenName, string basePath, string ns, string baseClass, string themePath, object elements)
        {
            if (string.IsNullOrEmpty(screenName))
                throw MCPException.InvalidParams("'name' is required (e.g. 'MainMenu').");

            basePath = PathUtilities.NormalizePath(basePath ?? "Assets/_Project/UI");
            ns = ns ?? "Game.UI";
            baseClass = baseClass ?? "MonoBehaviour";

            // Parse elements array
            var elementList = ParseElements(elements);

            // Generate UXML
            string uxmlPath = $"{basePath}/UXML/{screenName}.uxml";
            string uxmlContent = GenerateUXML(screenName, elementList, themePath);

            // Generate USS
            string ussPath = $"{basePath}/USS/{screenName}.uss";
            string ussContent = GenerateUSS(screenName, elementList, themePath);

            // Generate C# Controller
            string csPath = $"{basePath}/Controllers/{screenName}Controller.cs";
            string csContent = GenerateController(screenName, ns, baseClass, elementList);

            // Create folders and write files
            var created = new List<string>();

            foreach (var (filePath, content) in new[] { (uxmlPath, uxmlContent), (ussPath, ussContent), (csPath, csContent) })
            {
                string fullPath = Path.Combine(Application.dataPath, "..", filePath).Replace('\\', '/');
                string dir = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(fullPath, content);
                created.Add(filePath);
            }

            AssetDatabase.Refresh();

            return new
            {
                success = true,
                screen = screenName,
                files = created,
                message = $"Scaffolded 3 files for '{screenName}'. Import the UXML in a UIDocument and attach {screenName}Controller."
            };
        }

        private static List<(string name, string type, string text)> ParseElements(object elements)
        {
            var result = new List<(string, string, string)>();
            if (elements == null) return result;

            if (elements is Newtonsoft.Json.Linq.JArray arr)
            {
                foreach (var item in arr)
                {
                    string n = item["name"]?.ToString() ?? "element";
                    string t = item["type"]?.ToString() ?? "VisualElement";
                    string txt = item["text"]?.ToString();
                    result.Add((n, t, txt));
                }
            }

            return result;
        }

        private static string GenerateUXML(string name, List<(string name, string type, string text)> elements, string themePath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\"");
            sb.AppendLine($"    editor-extension-mode=\"False\">");
            sb.AppendLine($"    <Style src=\"project://database/{PathUtilities.NormalizePath($"Assets/_Project/UI/USS/{name}.uss")}\" />");
            sb.AppendLine($"    <ui:VisualElement name=\"{ToKebab(name)}-root\" class=\"screen-root\">");

            foreach (var (eName, eType, eText) in elements)
            {
                string tag = eType switch
                {
                    "Button" => "ui:Button",
                    "Label" => "ui:Label",
                    "TextField" => "ui:TextField",
                    "Toggle" => "ui:Toggle",
                    "Slider" => "ui:Slider",
                    "DropdownField" => "ui:DropdownField",
                    "ScrollView" => "ui:ScrollView",
                    "Foldout" => "ui:Foldout",
                    _ => "ui:VisualElement"
                };

                string textAttr = !string.IsNullOrEmpty(eText) ? $" text=\"{EscapeXml(eText)}\"" : "";
                sb.AppendLine($"        <{tag} name=\"{eName}\"{textAttr} />");
            }

            sb.AppendLine("    </ui:VisualElement>");
            sb.AppendLine("</ui:UXML>");
            return sb.ToString();
        }

        private static string GenerateUSS(string name, List<(string name, string type, string text)> elements, string themePath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"/* {name} Screen Styles */");
            sb.AppendLine();
            sb.AppendLine($".screen-root {{");
            sb.AppendLine("    flex-grow: 1;");
            sb.AppendLine("    align-items: center;");
            sb.AppendLine("    justify-content: center;");
            sb.AppendLine("}");

            foreach (var (eName, eType, _) in elements)
            {
                sb.AppendLine();
                sb.AppendLine($"#{eName} {{");
                sb.AppendLine(eType switch
                {
                    "Button" => "    padding: 8px 16px;\n    font-size: 18px;",
                    "Label" => "    font-size: 24px;\n    -unity-text-align: middle-center;",
                    _ => "    /* Add styles */"
                });
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private static string GenerateController(string name, string ns, string baseClass, List<(string name, string type, string text)> elements)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEngine.UIElements;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {name}Controller : {baseClass}");
            sb.AppendLine("    {");

            // Fields
            sb.AppendLine("        private UIDocument _document;");
            sb.AppendLine("        private VisualElement _root;");
            foreach (var (eName, eType, _) in elements)
            {
                string csType = eType switch
                {
                    "Button" => "Button",
                    "Label" => "Label",
                    "TextField" => "TextField",
                    "Toggle" => "Toggle",
                    "Slider" => "Slider",
                    "DropdownField" => "DropdownField",
                    "ScrollView" => "ScrollView",
                    "Foldout" => "Foldout",
                    _ => "VisualElement"
                };
                string fieldName = "_" + ToCamelCase(eName);
                sb.AppendLine($"        private {csType} {fieldName};");
            }

            sb.AppendLine();

            // Awake
            sb.AppendLine("        private void Awake()");
            sb.AppendLine("        {");
            sb.AppendLine("            _document = GetComponent<UIDocument>();");
            sb.AppendLine("        }");
            sb.AppendLine();

            // OnEnable — query elements
            sb.AppendLine("        private void OnEnable()");
            sb.AppendLine("        {");
            sb.AppendLine("            _root = _document.rootVisualElement;");
            foreach (var (eName, eType, _) in elements)
            {
                string csType = eType switch
                {
                    "Button" => "Button",
                    "Label" => "Label",
                    "TextField" => "TextField",
                    "Toggle" => "Toggle",
                    "Slider" => "Slider",
                    "DropdownField" => "DropdownField",
                    "ScrollView" => "ScrollView",
                    "Foldout" => "Foldout",
                    _ => "VisualElement"
                };
                string fieldName = "_" + ToCamelCase(eName);
                sb.AppendLine($"            {fieldName} = _root.Q<{csType}>(\"{eName}\");");
            }

            // Register button callbacks
            var buttons = elements.Where(e => e.type == "Button").ToList();
            if (buttons.Count > 0)
            {
                sb.AppendLine();
                foreach (var (eName, _, _) in buttons)
                {
                    string fieldName = "_" + ToCamelCase(eName);
                    string methodName = "On" + ToPascalCase(eName) + "Clicked";
                    sb.AppendLine($"            {fieldName}?.RegisterCallback<ClickEvent>(evt => {methodName}());");
                }
            }

            sb.AppendLine("        }");

            // Button handler stubs
            foreach (var (eName, _, _) in buttons)
            {
                string methodName = "On" + ToPascalCase(eName) + "Clicked";
                sb.AppendLine();
                sb.AppendLine($"        private void {methodName}()");
                sb.AppendLine("        {");
                sb.AppendLine($"            // TODO: Handle {eName} click");
                sb.AppendLine("        }");
            }

            // Show/Hide
            sb.AppendLine();
            sb.AppendLine("        public void Show() => _root.style.display = DisplayStyle.Flex;");
            sb.AppendLine("        public void Hide() => _root.style.display = DisplayStyle.None;");

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        #endregion

        #region debug_overlay (Issue #35 — Visual Debug Overlay)

        private static readonly HashSet<int> s_overlayPanels = new HashSet<int>();

        private static object DebugOverlay(string target, bool? show)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required (UIDocument GameObject name).");

            if (show == null)
                show = true;

            var doc = FindUIDocument(target);
            var root = doc.rootVisualElement;
            if (root == null)
                throw new MCPException($"UIDocument '{doc.gameObject.name}' has no rootVisualElement.");

            int key = doc.GetInstanceID();

            if (show == true)
            {
                if (s_overlayPanels.Contains(key))
                    return new { success = true, action = "debug_overlay", panel = doc.gameObject.name, message = "Debug overlay already active. Set show=false to remove." };

                ApplyDebugStyles(root, 0);
                s_overlayPanels.Add(key);

                int count = CountElements(root);
                return new
                {
                    success = true,
                    action = "debug_overlay_on",
                    panel = doc.gameObject.name,
                    elements = count,
                    message = $"Debug overlay applied to {count} elements. Color-coded borders show depth (red→cyan→green→yellow→purple→pink). Set show=false to remove."
                };
            }
            else
            {
                if (!s_overlayPanels.Contains(key))
                    return new { success = true, action = "debug_overlay", panel = doc.gameObject.name, message = "No debug overlay active on this panel." };

                RemoveDebugStyles(root);
                s_overlayPanels.Remove(key);

                return new
                {
                    success = true,
                    action = "debug_overlay_off",
                    panel = doc.gameObject.name,
                    message = "Debug overlay removed."
                };
            }
        }

        private static readonly Color[] s_debugColors = new[]
        {
            new Color(1f, 0f, 0f, 0.7f),       // red
            new Color(0f, 0.7f, 1f, 0.7f),      // cyan
            new Color(0f, 1f, 0.4f, 0.7f),      // green
            new Color(1f, 0.78f, 0f, 0.7f),     // yellow
            new Color(0.78f, 0f, 1f, 0.7f),     // purple
            new Color(1f, 0.4f, 0.78f, 0.7f),   // pink
        };

        private static void ApplyDebugStyles(VisualElement element, int depth)
        {
            Color borderColor = s_debugColors[depth % s_debugColors.Length];
            Color bgColor = new Color(borderColor.r, borderColor.g, borderColor.b, 0.04f);

            element.AddToClassList("mcp-dbg");

            element.style.borderTopWidth = 1;
            element.style.borderBottomWidth = 1;
            element.style.borderLeftWidth = 1;
            element.style.borderRightWidth = 1;
            element.style.borderTopColor = borderColor;
            element.style.borderBottomColor = borderColor;
            element.style.borderLeftColor = borderColor;
            element.style.borderRightColor = borderColor;

            if (element.resolvedStyle.backgroundColor.a < 0.01f)
                element.style.backgroundColor = bgColor;

            foreach (var child in element.Children())
                ApplyDebugStyles(child, depth + 1);
        }

        private static void RemoveDebugStyles(VisualElement element)
        {
            element.RemoveFromClassList("mcp-dbg");

            element.style.borderTopWidth = StyleKeyword.Null;
            element.style.borderBottomWidth = StyleKeyword.Null;
            element.style.borderLeftWidth = StyleKeyword.Null;
            element.style.borderRightWidth = StyleKeyword.Null;
            element.style.borderTopColor = StyleKeyword.Null;
            element.style.borderBottomColor = StyleKeyword.Null;
            element.style.borderLeftColor = StyleKeyword.Null;
            element.style.borderRightColor = StyleKeyword.Null;
            element.style.backgroundColor = StyleKeyword.Null;

            foreach (var child in element.Children())
                RemoveDebugStyles(child);
        }

        private static int CountElements(VisualElement root)
        {
            int count = 1;
            foreach (var child in root.Children())
                count += CountElements(child);
            return count;
        }

        #endregion

        #region Helpers

        private static VisualElement ResolveElement(string target, string selector, string refId)
        {
            // By ref_id (fastest)
            if (!string.IsNullOrEmpty(refId))
            {
                if (s_refRegistry.TryGetValue(refId, out var refElement))
                    return refElement;
                throw MCPException.InvalidParams($"Invalid ref_id '{refId}'.");
            }

            // By target + selector
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' or 'ref_id' is required to identify an element.");

            var doc = FindUIDocument(target);
            var root = doc.rootVisualElement;
            if (root == null)
                throw new MCPException($"UIDocument '{doc.gameObject.name}' has no rootVisualElement.");

            if (string.IsNullOrEmpty(selector))
                return root;

            var matches = QueryBySelector(root, selector, false);
            if (matches.Count == 0)
                throw MCPException.InvalidParams($"No element matches selector '{selector}' in panel '{doc.gameObject.name}'.");

            return matches[0];
        }

        private static bool IsVisible(VisualElement element)
        {
            if (!element.visible) return false;
            if (element.resolvedStyle.display == DisplayStyle.None) return false;
            if (element.resolvedStyle.opacity <= 0) return false;
            return true;
        }

        private static bool HasVisibleDescendant(VisualElement element, int remainingDepth)
        {
            if (remainingDepth <= 0) return false;
            foreach (var child in element.Children())
            {
                if (IsVisible(child)) return true;
                if (HasVisibleDescendant(child, remainingDepth - 1)) return true;
            }
            return false;
        }

        private static string GetElementText(VisualElement element)
        {
            return element switch
            {
                Label label => label.text,
                Button button => button.text,
                TextField tf => tf.value,
                Toggle toggle => toggle.label,
                Foldout foldout => foldout.text,
                DropdownField dd => dd.value,
                _ => null
            };
        }

        private static string ColorToHex(Color color)
        {
            return $"#{ColorUtility.ToHtmlStringRGBA(color)}";
        }

        private static StyleLength ParseLength(string value)
        {
            if (value.EndsWith("%"))
            {
                if (float.TryParse(value.TrimEnd('%'), out float pct))
                    return new StyleLength(new Length(pct, LengthUnit.Percent));
            }
            else if (value.EndsWith("px"))
            {
                if (float.TryParse(value.Replace("px", ""), out float px))
                    return new StyleLength(new Length(px, LengthUnit.Pixel));
            }
            else if (value.ToLowerInvariant() == "auto")
            {
                return new StyleLength(StyleKeyword.Auto);
            }
            else if (float.TryParse(value, out float raw))
            {
                return new StyleLength(new Length(raw, LengthUnit.Pixel));
            }

            throw MCPException.InvalidParams($"Cannot parse length value: '{value}'. Use numbers, 'Npx', 'N%', or 'auto'.");
        }

        private static Align ParseAlign(string value)
        {
            return value.ToLowerInvariant() switch
            {
                "center" => Align.Center,
                "flex-start" => Align.FlexStart,
                "flex-end" => Align.FlexEnd,
                "stretch" => Align.Stretch,
                "auto" => Align.Auto,
                _ => Align.Auto
            };
        }

        private static Vector2Int? ParseVector2Int(object data)
        {
            if (data is Newtonsoft.Json.Linq.JArray arr && arr.Count >= 2)
                return new Vector2Int((int)arr[0], (int)arr[1]);
            if (data is Newtonsoft.Json.Linq.JObject obj)
                return new Vector2Int(obj.Value<int>("x"), obj.Value<int>("y"));
            return null;
        }

        private static string ToKebab(string name)
        {
            return Regex.Replace(name, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
        }

        private static string ToCamelCase(string kebab)
        {
            var parts = kebab.Split('-', '_');
            if (parts.Length <= 1) return kebab.Length > 0 ? char.ToLower(kebab[0]) + kebab.Substring(1) : kebab;
            return parts[0].ToLowerInvariant() + string.Concat(parts.Skip(1).Select(p =>
                p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1).ToLowerInvariant() : ""));
        }

        private static string ToPascalCase(string kebab)
        {
            var parts = kebab.Split('-', '_');
            return string.Concat(parts.Select(p =>
                p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1).ToLowerInvariant() : ""));
        }

        private static string EscapeXml(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        #endregion
    }
}
