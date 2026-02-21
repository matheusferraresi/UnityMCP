using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.UI.Tabs
{
    /// <summary>
    /// Registry tab: searchable catalog of registered MCP tools, resources, and prompts.
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
            SetPlaceholder(_searchField, "Filter registry...");

            // Tool list scroll view
            _toolListScrollView = new ScrollView(ScrollViewMode.Vertical);
            _toolListScrollView.AddToClassList("scroll-view");
            Root.Add(_toolListScrollView);

            _toolListContainer = _toolListScrollView.contentContainer;

            // Empty state
            _emptyState = new VisualElement();
            _emptyState.AddToClassList("empty-state");
            Label emptyLabel = new Label("No items registered.\nCreate tools with [MCPTool], resources with [MCPResource], or prompts with [MCPPrompt].");
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
            int toolCount = ToolRegistry.Count;
            int resourceCount = ResourceRegistry.Count;
            int promptCount = PromptRegistry.Count;
            _summaryLabel.text = $"{toolCount} tools, {resourceCount} resources, {promptCount} prompts";
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

            if (!hasVisibleTools && !hasFilter)
            {
                _toolListScrollView.Add(_emptyState);
            }
            else if (!hasVisibleTools)
            {
                VisualElement noResults = new VisualElement();
                noResults.AddToClassList("empty-state");
                Label noResultsLabel = new Label($"No tools matching \"{_searchFilter}\"");
                noResultsLabel.AddToClassList("empty-state-text");
                noResults.Add(noResultsLabel);
                _toolListContainer.Add(noResults);
            }

            // --- Resources Section ---
            BuildResourcesSection(filterLower);

            // --- Prompts Section ---
            BuildPromptsSection(filterLower);
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

        #region Resources Section

        private void BuildResourcesSection(string filterLower)
        {
            List<ResourceDefinition> resources = ResourceRegistry.GetDefinitions().ToList();
            List<ResourceTemplate> templates = ResourceRegistry.GetTemplateDefinitions().ToList();

            bool hasFilter = !string.IsNullOrEmpty(filterLower);

            // Filter resources
            if (hasFilter)
            {
                resources = resources.Where(r =>
                    (r.name != null && r.name.ToLowerInvariant().Contains(filterLower)) ||
                    (r.uri != null && r.uri.ToLowerInvariant().Contains(filterLower)) ||
                    (r.description != null && r.description.ToLowerInvariant().Contains(filterLower))
                ).ToList();

                templates = templates.Where(t =>
                    (t.name != null && t.name.ToLowerInvariant().Contains(filterLower)) ||
                    (t.uriTemplate != null && t.uriTemplate.ToLowerInvariant().Contains(filterLower)) ||
                    (t.description != null && t.description.ToLowerInvariant().Contains(filterLower))
                ).ToList();
            }

            int totalCount = resources.Count + templates.Count;
            if (totalCount == 0 && hasFilter) return;
            if (resources.Count == 0 && templates.Count == 0) return;

            Foldout resourcesFoldout = new Foldout { text = $"Resources ({totalCount})" };
            resourcesFoldout.AddToClassList("category-foldout");
            resourcesFoldout.value = hasFilter;

            foreach (ResourceDefinition resource in resources)
            {
                resourcesFoldout.Add(BuildResourceEntry(resource));
            }

            foreach (ResourceTemplate template in templates)
            {
                resourcesFoldout.Add(BuildResourceTemplateEntry(template));
            }

            _toolListContainer.Add(resourcesFoldout);
        }

        private static VisualElement BuildResourceEntry(ResourceDefinition resource)
        {
            VisualElement entry = new VisualElement();
            entry.AddToClassList("tool-entry");

            Label nameLabel = new Label(resource.name ?? resource.uri);
            nameLabel.AddToClassList("tool-name");
            nameLabel.AddToClassList("mono");
            entry.Add(nameLabel);

            if (!string.IsNullOrEmpty(resource.uri))
            {
                Label uriLabel = new Label(resource.uri);
                uriLabel.AddToClassList("tool-params");
                entry.Add(uriLabel);
            }

            if (!string.IsNullOrEmpty(resource.description))
            {
                Label descLabel = new Label(resource.description);
                descLabel.AddToClassList("tool-description");
                entry.Add(descLabel);
            }

            if (!string.IsNullOrEmpty(resource.mimeType))
            {
                VisualElement pillRow = new VisualElement();
                pillRow.AddToClassList("tool-annotations");
                pillRow.Add(CreateAnnotationPill(resource.mimeType, "pill--readonly"));
                entry.Add(pillRow);
            }

            return entry;
        }

        private static VisualElement BuildResourceTemplateEntry(ResourceTemplate template)
        {
            VisualElement entry = new VisualElement();
            entry.AddToClassList("tool-entry");

            Label nameLabel = new Label(template.name ?? template.uriTemplate);
            nameLabel.AddToClassList("tool-name");
            nameLabel.AddToClassList("mono");
            entry.Add(nameLabel);

            if (!string.IsNullOrEmpty(template.uriTemplate))
            {
                Label uriLabel = new Label(template.uriTemplate);
                uriLabel.AddToClassList("tool-params");
                entry.Add(uriLabel);
            }

            if (!string.IsNullOrEmpty(template.description))
            {
                Label descLabel = new Label(template.description);
                descLabel.AddToClassList("tool-description");
                entry.Add(descLabel);
            }

            VisualElement pillRow = new VisualElement();
            pillRow.AddToClassList("tool-annotations");
            pillRow.Add(CreateAnnotationPill("template", "pill--idempotent"));
            if (!string.IsNullOrEmpty(template.mimeType))
            {
                pillRow.Add(CreateAnnotationPill(template.mimeType, "pill--readonly"));
            }
            entry.Add(pillRow);

            return entry;
        }

        #endregion

        #region Prompts Section

        private void BuildPromptsSection(string filterLower)
        {
            List<PromptDefinition> prompts = PromptRegistry.GetDefinitions().ToList();

            bool hasFilter = !string.IsNullOrEmpty(filterLower);

            if (hasFilter)
            {
                prompts = prompts.Where(p =>
                    (p.name != null && p.name.ToLowerInvariant().Contains(filterLower)) ||
                    (p.description != null && p.description.ToLowerInvariant().Contains(filterLower))
                ).ToList();
            }

            if (prompts.Count == 0) return;

            Foldout promptsFoldout = new Foldout { text = $"Prompts ({prompts.Count})" };
            promptsFoldout.AddToClassList("category-foldout");
            promptsFoldout.value = hasFilter;

            foreach (PromptDefinition prompt in prompts)
            {
                promptsFoldout.Add(BuildPromptEntry(prompt));
            }

            _toolListContainer.Add(promptsFoldout);
        }

        private static VisualElement BuildPromptEntry(PromptDefinition prompt)
        {
            VisualElement entry = new VisualElement();
            entry.AddToClassList("tool-entry");

            Label nameLabel = new Label(prompt.name);
            nameLabel.AddToClassList("tool-name");
            nameLabel.AddToClassList("mono");
            entry.Add(nameLabel);

            if (!string.IsNullOrEmpty(prompt.description))
            {
                Label descLabel = new Label(prompt.description);
                descLabel.AddToClassList("tool-description");
                entry.Add(descLabel);
            }

            if (prompt.arguments != null && prompt.arguments.Count > 0)
            {
                int totalArgs = prompt.arguments.Count;
                int requiredArgs = prompt.arguments.Count(a => a.required);
                string argText = requiredArgs > 0
                    ? $"{totalArgs} args ({requiredArgs} required)"
                    : $"{totalArgs} args";

                Label argLabel = new Label(argText);
                argLabel.AddToClassList("tool-params");
                entry.Add(argLabel);
            }

            return entry;
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
