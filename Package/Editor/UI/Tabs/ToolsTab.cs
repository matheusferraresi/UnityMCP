using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.UI.Tabs
{
    /// <summary>
    /// Tools tab: searchable, categorized catalog of registered MCP tools.
    /// </summary>
    public class ToolsTab : ITab
    {
        public VisualElement Root { get; }

        private readonly Label _summaryLabel;
        private readonly TextField _searchField;
        private readonly ScrollView _toolListScrollView;
        private readonly VisualElement _toolListContainer;
        private readonly VisualElement _emptyState;

        private string _searchFilter = "";
        private List<IGrouping<string, ToolDefinition>> _cachedToolsByCategory;

        public ToolsTab()
        {
            Root = new VisualElement();
            Root.style.flexGrow = 1;

            // Summary bar
            VisualElement summaryBar = new VisualElement();
            summaryBar.AddToClassList("row--spaced");
            summaryBar.style.marginBottom = 6;

            _summaryLabel = new Label();
            _summaryLabel.AddToClassList("muted");
            summaryBar.Add(_summaryLabel);

            Button refreshButton = new Button(OnRefresh) { text = "Refresh" };
            refreshButton.AddToClassList("button--small");
            refreshButton.AddToClassList("button--accent");
            summaryBar.Add(refreshButton);

            Root.Add(summaryBar);

            // Search field
            _searchField = new TextField();
            _searchField.AddToClassList("search-field");
            // Set placeholder via value when empty
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue ?? "";
                RebuildToolList();
            });
            Root.Add(_searchField);

            // Set placeholder text
            _searchField.value = "";
            SetPlaceholder(_searchField, "Filter tools...");

            // Tool list scroll view
            _toolListScrollView = new ScrollView(ScrollViewMode.Vertical);
            _toolListScrollView.AddToClassList("scroll-view");
            Root.Add(_toolListScrollView);

            _toolListContainer = _toolListScrollView.contentContainer;

            // Empty state
            _emptyState = new VisualElement();
            _emptyState.AddToClassList("empty-state");
            Label emptyLabel = new Label("No tools registered.\nCreate tools by adding [MCPTool] attribute to static methods.");
            emptyLabel.AddToClassList("empty-state-text");
            _emptyState.Add(emptyLabel);
        }

        public void OnActivate()
        {
            RefreshCache();
            RebuildToolList();
        }

        public void OnDeactivate() { }

        public void Refresh() { }

        private void OnRefresh()
        {
            ToolRegistry.RefreshTools();
            RefreshCache();
            RebuildToolList();
        }

        private void RefreshCache()
        {
            _cachedToolsByCategory = ToolRegistry.GetDefinitionsByCategory().ToList();
            int categoryCount = _cachedToolsByCategory.Count;
            int toolCount = ToolRegistry.Count;
            _summaryLabel.text = $"{toolCount} tools across {categoryCount} categories";
        }

        #region Tool List Building

        private void RebuildToolList()
        {
            _toolListContainer.Clear();

            if (_cachedToolsByCategory == null || _cachedToolsByCategory.Count == 0)
            {
                _toolListScrollView.Add(_emptyState);
                return;
            }

            _emptyState.RemoveFromHierarchy();

            bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);
            string filterLower = hasFilter ? _searchFilter.ToLowerInvariant() : "";
            bool hasVisibleTools = false;

            foreach (IGrouping<string, ToolDefinition> group in _cachedToolsByCategory)
            {
                List<ToolDefinition> tools = group.ToList();

                // Filter tools if search is active
                if (hasFilter)
                {
                    tools = tools.Where(t =>
                        (t.name != null && t.name.ToLowerInvariant().Contains(filterLower)) ||
                        (t.description != null && t.description.ToLowerInvariant().Contains(filterLower))
                    ).ToList();

                    if (tools.Count == 0) continue;
                }

                hasVisibleTools = true;

                // Category foldout
                Foldout categoryFoldout = new Foldout { text = $"{group.Key} ({tools.Count})" };
                categoryFoldout.AddToClassList("category-foldout");
                categoryFoldout.value = hasFilter; // Expand when filtering, collapse otherwise

                foreach (ToolDefinition tool in tools)
                {
                    categoryFoldout.Add(BuildToolEntry(tool));
                }

                _toolListContainer.Add(categoryFoldout);
            }

            if (!hasVisibleTools)
            {
                VisualElement noResults = new VisualElement();
                noResults.AddToClassList("empty-state");
                Label noResultsLabel = new Label($"No tools matching \"{_searchFilter}\"");
                noResultsLabel.AddToClassList("empty-state-text");
                noResults.Add(noResultsLabel);
                _toolListContainer.Add(noResults);
            }
        }

        private VisualElement BuildToolEntry(ToolDefinition tool)
        {
            VisualElement entry = new VisualElement();
            entry.AddToClassList("tool-entry");

            // Tool name (monospace bold)
            Label nameLabel = new Label(tool.name);
            nameLabel.AddToClassList("tool-name");
            nameLabel.AddToClassList("mono");
            entry.Add(nameLabel);

            // Description
            if (!string.IsNullOrEmpty(tool.description))
            {
                Label descLabel = new Label(tool.description);
                descLabel.AddToClassList("tool-description");
                entry.Add(descLabel);
            }

            // Parameter summary
            if (tool.inputSchema?.properties != null && tool.inputSchema.properties.Count > 0)
            {
                int totalParams = tool.inputSchema.properties.Count;
                int requiredParams = tool.inputSchema.required?.Count ?? 0;
                string paramText = requiredParams > 0
                    ? $"{totalParams} params ({requiredParams} required)"
                    : $"{totalParams} params";

                Label paramLabel = new Label(paramText);
                paramLabel.AddToClassList("tool-params");
                entry.Add(paramLabel);
            }

            // Annotation pills
            if (tool.annotations != null)
            {
                VisualElement annotationRow = new VisualElement();
                annotationRow.AddToClassList("tool-annotations");

                if (tool.annotations.readOnlyHint == true)
                {
                    annotationRow.Add(CreateAnnotationPill("read-only", "pill--readonly"));
                }
                if (tool.annotations.destructiveHint == true)
                {
                    annotationRow.Add(CreateAnnotationPill("destructive", "pill--destructive"));
                }
                if (tool.annotations.idempotentHint == true)
                {
                    annotationRow.Add(CreateAnnotationPill("idempotent", "pill--idempotent"));
                }

                if (annotationRow.childCount > 0)
                    entry.Add(annotationRow);
            }

            return entry;
        }

        private static Label CreateAnnotationPill(string text, string styleClass)
        {
            Label pill = new Label(text);
            pill.AddToClassList("pill");
            pill.AddToClassList(styleClass);
            return pill;
        }

        #endregion

        #region Helpers

        private static void SetPlaceholder(TextField textField, string placeholder)
        {
            Label placeholderLabel = new Label(placeholder);
            placeholderLabel.AddToClassList("muted");
            placeholderLabel.style.position = Position.Absolute;
            placeholderLabel.style.left = 4;
            placeholderLabel.style.top = 2;
            placeholderLabel.pickingMode = PickingMode.Ignore;

            textField.Add(placeholderLabel);

            textField.RegisterCallback<FocusInEvent>(evt => placeholderLabel.style.display = DisplayStyle.None);
            textField.RegisterCallback<FocusOutEvent>(evt =>
            {
                if (string.IsNullOrEmpty(textField.value))
                    placeholderLabel.style.display = DisplayStyle.Flex;
            });

            // Initial state
            if (!string.IsNullOrEmpty(textField.value))
                placeholderLabel.style.display = DisplayStyle.None;
        }

        #endregion
    }
}
