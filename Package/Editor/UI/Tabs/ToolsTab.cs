using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.UI.Tabs
{
    /// <summary>
    /// Registry tab: browsable catalog of MCP tools, resources, and prompts
    /// with type filtering, search, and expandable parameter detail.
    /// </summary>
    public class ToolsTab : ITab
    {
        private enum TypeFilter { All, Tools, Resources, Prompts }

        public VisualElement Root { get; }

        private readonly Label _summaryLabel;
        private readonly TextField _searchField;
        private readonly ScrollView _scrollView;
        private readonly VisualElement _listContainer;
        private readonly VisualElement _emptyState;

        private readonly Button _filterAll;
        private readonly Button _filterTools;
        private readonly Button _filterResources;
        private readonly Button _filterPrompts;

        private string _searchFilter = "";
        private TypeFilter _currentTypeFilter = TypeFilter.All;
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
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue ?? "";
                RebuildList();
            });
            Root.Add(_searchField);

            _searchField.value = "";
            SetPlaceholder(_searchField, "Search by name, description, or parameter...");

            // Type filter bar
            VisualElement typeBar = new VisualElement();
            typeBar.AddToClassList("filter-bar");
            typeBar.style.marginBottom = 6;

            _filterAll = CreateTypePill("All", TypeFilter.All);
            _filterTools = CreateTypePill("Tools", TypeFilter.Tools);
            _filterResources = CreateTypePill("Resources", TypeFilter.Resources);
            _filterPrompts = CreateTypePill("Prompts", TypeFilter.Prompts);

            typeBar.Add(_filterAll);
            typeBar.Add(_filterTools);
            typeBar.Add(_filterResources);
            typeBar.Add(_filterPrompts);

            _filterAll.AddToClassList("filter-pill--active");
            Root.Add(typeBar);

            // Scroll view
            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.AddToClassList("scroll-view");
            Root.Add(_scrollView);

            _listContainer = _scrollView.contentContainer;

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
            RebuildList();
        }

        public void OnDeactivate() { }

        public void Refresh() { }

        private void OnRefresh()
        {
            ToolRegistry.RefreshTools();
            RefreshCache();
            RebuildList();
        }

        private void RefreshCache()
        {
            _cachedToolsByCategory = ToolRegistry.GetDefinitionsByCategory().ToList();
            int toolCount = ToolRegistry.Count;
            int resourceCount = ResourceRegistry.Count;
            int promptCount = PromptRegistry.Count;
            _summaryLabel.text = $"{toolCount} tools \u00b7 {resourceCount} resources \u00b7 {promptCount} prompts";
        }

        #region Type Filter

        private Button CreateTypePill(string label, TypeFilter filter)
        {
            Button pill = new Button(() => SetTypeFilter(filter)) { text = label };
            pill.AddToClassList("filter-pill");
            return pill;
        }

        private void SetTypeFilter(TypeFilter filter)
        {
            _currentTypeFilter = filter;

            _filterAll.EnableInClassList("filter-pill--active", filter == TypeFilter.All);
            _filterTools.EnableInClassList("filter-pill--active", filter == TypeFilter.Tools);
            _filterResources.EnableInClassList("filter-pill--active", filter == TypeFilter.Resources);
            _filterPrompts.EnableInClassList("filter-pill--active", filter == TypeFilter.Prompts);

            RebuildList();
        }

        #endregion

        #region List Building

        private void RebuildList()
        {
            _listContainer.Clear();
            _emptyState.RemoveFromHierarchy();

            bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);
            string filterLower = hasFilter ? _searchFilter.ToLowerInvariant() : "";
            bool hasVisibleItems = false;

            // --- Tools ---
            if (_currentTypeFilter == TypeFilter.All || _currentTypeFilter == TypeFilter.Tools)
            {
                hasVisibleItems |= BuildToolsSection(filterLower, hasFilter);
            }

            // --- Resources ---
            if (_currentTypeFilter == TypeFilter.All || _currentTypeFilter == TypeFilter.Resources)
            {
                hasVisibleItems |= BuildResourcesSection(filterLower, hasFilter);
            }

            // --- Prompts ---
            if (_currentTypeFilter == TypeFilter.All || _currentTypeFilter == TypeFilter.Prompts)
            {
                hasVisibleItems |= BuildPromptsSection(filterLower, hasFilter);
            }

            if (!hasVisibleItems)
            {
                if (hasFilter || _currentTypeFilter != TypeFilter.All)
                {
                    VisualElement noResults = new VisualElement();
                    noResults.AddToClassList("empty-state");
                    string context = hasFilter ? $"\"{_searchFilter}\"" : _currentTypeFilter.ToString().ToLowerInvariant();
                    Label noResultsLabel = new Label($"No items matching {context}");
                    noResultsLabel.AddToClassList("empty-state-text");
                    noResults.Add(noResultsLabel);
                    _listContainer.Add(noResults);
                }
                else
                {
                    _scrollView.Add(_emptyState);
                }
            }
        }

        #endregion

        #region Tools Section

        private bool BuildToolsSection(string filterLower, bool hasFilter)
        {
            if (_cachedToolsByCategory == null || _cachedToolsByCategory.Count == 0)
                return false;

            bool hasVisibleTools = false;

            foreach (IGrouping<string, ToolDefinition> group in _cachedToolsByCategory)
            {
                List<ToolDefinition> tools = group.ToList();

                if (hasFilter)
                {
                    tools = tools.Where(t =>
                        MatchesFilter(t.name, filterLower) ||
                        MatchesFilter(t.description, filterLower) ||
                        MatchesParamFilter(t.inputSchema, filterLower)
                    ).ToList();

                    if (tools.Count == 0) continue;
                }

                hasVisibleTools = true;

                // Collapsible category group
                VisualElement categoryGroup = new VisualElement();

                VisualElement sectionHeader = new VisualElement();
                sectionHeader.AddToClassList("reg-section-header");
                sectionHeader.AddToClassList("reg-section-header--clickable");

                Label arrowLabel = new Label("\u25B6");
                arrowLabel.AddToClassList("reg-section-arrow");
                sectionHeader.Add(arrowLabel);

                Label sectionLabel = new Label($"{group.Key}");
                sectionLabel.AddToClassList("reg-section-label");
                sectionHeader.Add(sectionLabel);

                Label sectionCount = new Label($"{tools.Count}");
                sectionCount.AddToClassList("reg-section-count");
                sectionHeader.Add(sectionCount);

                categoryGroup.Add(sectionHeader);

                VisualElement cardsContainer = new VisualElement();
                // Expand when filtering, collapse otherwise
                bool startExpanded = hasFilter;
                cardsContainer.style.display = startExpanded ? DisplayStyle.Flex : DisplayStyle.None;
                arrowLabel.text = startExpanded ? "\u25BC" : "\u25B6";

                foreach (ToolDefinition tool in tools)
                {
                    cardsContainer.Add(BuildToolCard(tool));
                }

                categoryGroup.Add(cardsContainer);

                // Click header to toggle
                sectionHeader.RegisterCallback<ClickEvent>(evt =>
                {
                    bool isExpanded = cardsContainer.style.display == DisplayStyle.Flex;
                    cardsContainer.style.display = isExpanded ? DisplayStyle.None : DisplayStyle.Flex;
                    arrowLabel.text = isExpanded ? "\u25B6" : "\u25BC";
                });

                _listContainer.Add(categoryGroup);
            }

            return hasVisibleTools;
        }

        private static bool MatchesParamFilter(InputSchema schema, string filterLower)
        {
            if (schema?.properties == null) return false;
            foreach (KeyValuePair<string, PropertySchema> kvp in schema.properties)
            {
                if (MatchesFilter(kvp.Key, filterLower)) return true;
                if (MatchesFilter(kvp.Value?.description, filterLower)) return true;
            }
            return false;
        }

        private VisualElement BuildToolCard(ToolDefinition tool)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList("reg-card");
            card.AddToClassList("reg-card--tool");

            // Header row: name + pills
            VisualElement headerRow = new VisualElement();
            headerRow.AddToClassList("reg-card__header");

            Label nameLabel = new Label(tool.name);
            nameLabel.AddToClassList("reg-card__name");
            headerRow.Add(nameLabel);

            // Param count pill (inline)
            if (tool.inputSchema?.properties != null && tool.inputSchema.properties.Count > 0)
            {
                int totalParams = tool.inputSchema.properties.Count;
                int requiredParams = tool.inputSchema.required?.Count ?? 0;
                string paramText = requiredParams > 0
                    ? $"{totalParams} params ({requiredParams} req)"
                    : $"{totalParams} params";

                Label paramPill = new Label(paramText);
                paramPill.AddToClassList("pill");
                paramPill.AddToClassList("pill--stat");
                headerRow.Add(paramPill);
            }

            // Annotation pills (inline)
            if (tool.annotations != null)
            {
                if (tool.annotations.readOnlyHint == true)
                    headerRow.Add(CreatePill("read-only", "pill--readonly"));
                if (tool.annotations.destructiveHint == true)
                    headerRow.Add(CreatePill("destructive", "pill--destructive"));
                if (tool.annotations.idempotentHint == true)
                    headerRow.Add(CreatePill("idempotent", "pill--idempotent"));
            }

            card.Add(headerRow);

            // Description line
            if (!string.IsNullOrEmpty(tool.description))
            {
                Label descLabel = new Label(tool.description);
                descLabel.AddToClassList("reg-card__desc");
                card.Add(descLabel);
            }

            // Expandable parameter detail
            bool hasParams = tool.inputSchema?.properties != null && tool.inputSchema.properties.Count > 0;
            if (hasParams)
            {
                VisualElement detailPanel = BuildParamDetailPanel(tool.inputSchema);
                detailPanel.style.display = DisplayStyle.None;
                card.Add(detailPanel);

                // Click to toggle
                card.RegisterCallback<ClickEvent>(evt =>
                {
                    bool isExpanded = detailPanel.style.display == DisplayStyle.Flex;
                    detailPanel.style.display = isExpanded ? DisplayStyle.None : DisplayStyle.Flex;
                    card.EnableInClassList("reg-card--expanded", !isExpanded);
                });
                card.AddToClassList("reg-card--clickable");
            }

            return card;
        }

        private static VisualElement BuildParamDetailPanel(InputSchema schema)
        {
            VisualElement panel = new VisualElement();
            panel.AddToClassList("reg-detail");

            // Divider
            VisualElement divider = new VisualElement();
            divider.AddToClassList("reg-detail__divider");
            panel.Add(divider);

            Label headerLabel = new Label("PARAMETERS");
            headerLabel.AddToClassList("reg-detail__header");
            panel.Add(headerLabel);

            HashSet<string> requiredSet = new HashSet<string>(schema.required ?? new List<string>());

            foreach (KeyValuePair<string, PropertySchema> kvp in schema.properties)
            {
                string paramName = kvp.Key;
                PropertySchema prop = kvp.Value;
                bool isRequired = requiredSet.Contains(paramName);

                VisualElement paramRow = new VisualElement();
                paramRow.AddToClassList("reg-param");

                // Name + type row
                VisualElement nameTypeRow = new VisualElement();
                nameTypeRow.AddToClassList("reg-param__header");

                Label nameLabel = new Label(paramName);
                nameLabel.AddToClassList("reg-param__name");
                nameTypeRow.Add(nameLabel);

                if (!string.IsNullOrEmpty(prop?.type))
                {
                    Label typeLabel = new Label(prop.type);
                    typeLabel.AddToClassList("reg-param__type");
                    nameTypeRow.Add(typeLabel);
                }

                if (isRequired)
                {
                    Label reqLabel = new Label("required");
                    reqLabel.AddToClassList("reg-param__required");
                    nameTypeRow.Add(reqLabel);
                }

                paramRow.Add(nameTypeRow);

                // Description
                if (prop != null && !string.IsNullOrEmpty(prop.description))
                {
                    Label descLabel = new Label(prop.description);
                    descLabel.AddToClassList("reg-param__desc");
                    paramRow.Add(descLabel);
                }

                // Metadata line (default, enum, range)
                string meta = BuildParamMeta(prop);
                if (!string.IsNullOrEmpty(meta))
                {
                    Label metaLabel = new Label(meta);
                    metaLabel.AddToClassList("reg-param__meta");
                    paramRow.Add(metaLabel);
                }

                panel.Add(paramRow);
            }

            return panel;
        }

        private static string BuildParamMeta(PropertySchema prop)
        {
            if (prop == null) return null;

            List<string> parts = new List<string>();

            if (prop.@default != null)
                parts.Add($"default: {prop.@default}");

            if (prop.@enum != null && prop.@enum.Count > 0)
            {
                string enumValues = string.Join(", ", prop.@enum.Take(6));
                if (prop.@enum.Count > 6)
                    enumValues += $", +{prop.@enum.Count - 6} more";
                parts.Add(enumValues);
            }

            if (prop.minimum.HasValue && !double.IsNaN(prop.minimum.Value))
                parts.Add($"min: {prop.minimum.Value}");

            if (prop.maximum.HasValue && !double.IsNaN(prop.maximum.Value))
                parts.Add($"max: {prop.maximum.Value}");

            return parts.Count > 0 ? string.Join(" \u00b7 ", parts) : null;
        }

        #endregion

        #region Resources Section

        private bool BuildResourcesSection(string filterLower, bool hasFilter)
        {
            List<ResourceDefinition> resources = ResourceRegistry.GetDefinitions().ToList();
            List<ResourceTemplate> templates = ResourceRegistry.GetTemplateDefinitions().ToList();

            if (hasFilter)
            {
                resources = resources.Where(r =>
                    MatchesFilter(r.name, filterLower) ||
                    MatchesFilter(r.uri, filterLower) ||
                    MatchesFilter(r.description, filterLower)
                ).ToList();

                templates = templates.Where(t =>
                    MatchesFilter(t.name, filterLower) ||
                    MatchesFilter(t.uriTemplate, filterLower) ||
                    MatchesFilter(t.description, filterLower)
                ).ToList();
            }

            int totalCount = resources.Count + templates.Count;
            if (totalCount == 0) return false;

            // Collapsible section
            VisualElement sectionGroup = new VisualElement();

            VisualElement sectionHeader = new VisualElement();
            sectionHeader.AddToClassList("reg-section-header");
            sectionHeader.AddToClassList("reg-section-header--resource");
            sectionHeader.AddToClassList("reg-section-header--clickable");

            Label arrowLabel = new Label(hasFilter ? "\u25BC" : "\u25B6");
            arrowLabel.AddToClassList("reg-section-arrow");
            sectionHeader.Add(arrowLabel);

            Label sectionLabel = new Label("Resources");
            sectionLabel.AddToClassList("reg-section-label");
            sectionHeader.Add(sectionLabel);

            Label sectionCount = new Label($"{totalCount}");
            sectionCount.AddToClassList("reg-section-count");
            sectionHeader.Add(sectionCount);

            sectionGroup.Add(sectionHeader);

            VisualElement cardsContainer = new VisualElement();
            cardsContainer.style.display = hasFilter ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (ResourceDefinition resource in resources)
            {
                cardsContainer.Add(BuildResourceCard(resource));
            }

            foreach (ResourceTemplate template in templates)
            {
                cardsContainer.Add(BuildTemplateCard(template));
            }

            sectionGroup.Add(cardsContainer);

            sectionHeader.RegisterCallback<ClickEvent>(evt =>
            {
                bool isExpanded = cardsContainer.style.display == DisplayStyle.Flex;
                cardsContainer.style.display = isExpanded ? DisplayStyle.None : DisplayStyle.Flex;
                arrowLabel.text = isExpanded ? "\u25B6" : "\u25BC";
            });

            _listContainer.Add(sectionGroup);

            return true;
        }

        private static VisualElement BuildResourceCard(ResourceDefinition resource)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList("reg-card");
            card.AddToClassList("reg-card--resource");

            // Header row: name + mime pill
            VisualElement headerRow = new VisualElement();
            headerRow.AddToClassList("reg-card__header");

            Label nameLabel = new Label(resource.name ?? resource.uri);
            nameLabel.AddToClassList("reg-card__name");
            headerRow.Add(nameLabel);

            if (!string.IsNullOrEmpty(resource.mimeType))
            {
                headerRow.Add(CreatePill(resource.mimeType, "pill--readonly"));
            }

            card.Add(headerRow);

            // URI
            if (!string.IsNullOrEmpty(resource.uri))
            {
                Label uriLabel = new Label(resource.uri);
                uriLabel.AddToClassList("reg-card__uri");
                card.Add(uriLabel);
            }

            // Description
            if (!string.IsNullOrEmpty(resource.description))
            {
                Label descLabel = new Label(resource.description);
                descLabel.AddToClassList("reg-card__desc");
                card.Add(descLabel);
            }

            return card;
        }

        private static VisualElement BuildTemplateCard(ResourceTemplate template)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList("reg-card");
            card.AddToClassList("reg-card--resource");

            // Header row
            VisualElement headerRow = new VisualElement();
            headerRow.AddToClassList("reg-card__header");

            Label nameLabel = new Label(template.name ?? template.uriTemplate);
            nameLabel.AddToClassList("reg-card__name");
            headerRow.Add(nameLabel);

            headerRow.Add(CreatePill("template", "pill--idempotent"));

            if (!string.IsNullOrEmpty(template.mimeType))
            {
                headerRow.Add(CreatePill(template.mimeType, "pill--readonly"));
            }

            card.Add(headerRow);

            // URI template
            if (!string.IsNullOrEmpty(template.uriTemplate))
            {
                Label uriLabel = new Label(template.uriTemplate);
                uriLabel.AddToClassList("reg-card__uri");
                card.Add(uriLabel);
            }

            // Description
            if (!string.IsNullOrEmpty(template.description))
            {
                Label descLabel = new Label(template.description);
                descLabel.AddToClassList("reg-card__desc");
                card.Add(descLabel);
            }

            return card;
        }

        #endregion

        #region Prompts Section

        private bool BuildPromptsSection(string filterLower, bool hasFilter)
        {
            List<PromptDefinition> prompts = PromptRegistry.GetDefinitions().ToList();

            if (hasFilter)
            {
                prompts = prompts.Where(p =>
                    MatchesFilter(p.name, filterLower) ||
                    MatchesFilter(p.description, filterLower)
                ).ToList();
            }

            if (prompts.Count == 0) return false;

            // Collapsible section
            VisualElement sectionGroup = new VisualElement();

            VisualElement sectionHeader = new VisualElement();
            sectionHeader.AddToClassList("reg-section-header");
            sectionHeader.AddToClassList("reg-section-header--prompt");
            sectionHeader.AddToClassList("reg-section-header--clickable");

            Label arrowLabel = new Label(hasFilter ? "\u25BC" : "\u25B6");
            arrowLabel.AddToClassList("reg-section-arrow");
            sectionHeader.Add(arrowLabel);

            Label sectionLabel = new Label("Prompts");
            sectionLabel.AddToClassList("reg-section-label");
            sectionHeader.Add(sectionLabel);

            Label sectionCount = new Label($"{prompts.Count}");
            sectionCount.AddToClassList("reg-section-count");
            sectionHeader.Add(sectionCount);

            sectionGroup.Add(sectionHeader);

            VisualElement cardsContainer = new VisualElement();
            cardsContainer.style.display = hasFilter ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (PromptDefinition prompt in prompts)
            {
                cardsContainer.Add(BuildPromptCard(prompt));
            }

            sectionGroup.Add(cardsContainer);

            sectionHeader.RegisterCallback<ClickEvent>(evt =>
            {
                bool isExpanded = cardsContainer.style.display == DisplayStyle.Flex;
                cardsContainer.style.display = isExpanded ? DisplayStyle.None : DisplayStyle.Flex;
                arrowLabel.text = isExpanded ? "\u25B6" : "\u25BC";
            });

            _listContainer.Add(sectionGroup);

            return true;
        }

        private static VisualElement BuildPromptCard(PromptDefinition prompt)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList("reg-card");
            card.AddToClassList("reg-card--prompt");

            // Header row
            VisualElement headerRow = new VisualElement();
            headerRow.AddToClassList("reg-card__header");

            Label nameLabel = new Label(prompt.name);
            nameLabel.AddToClassList("reg-card__name");
            headerRow.Add(nameLabel);

            if (prompt.arguments != null && prompt.arguments.Count > 0)
            {
                int totalArgs = prompt.arguments.Count;
                int requiredArgs = prompt.arguments.Count(a => a.required);
                string argText = requiredArgs > 0
                    ? $"{totalArgs} args ({requiredArgs} req)"
                    : $"{totalArgs} args";

                headerRow.Add(CreatePill(argText, "pill--stat"));
            }

            card.Add(headerRow);

            // Description
            if (!string.IsNullOrEmpty(prompt.description))
            {
                Label descLabel = new Label(prompt.description);
                descLabel.AddToClassList("reg-card__desc");
                card.Add(descLabel);
            }

            // Expandable argument detail
            if (prompt.arguments != null && prompt.arguments.Count > 0)
            {
                VisualElement detailPanel = BuildPromptDetailPanel(prompt.arguments);
                detailPanel.style.display = DisplayStyle.None;
                card.Add(detailPanel);

                card.RegisterCallback<ClickEvent>(evt =>
                {
                    bool isExpanded = detailPanel.style.display == DisplayStyle.Flex;
                    detailPanel.style.display = isExpanded ? DisplayStyle.None : DisplayStyle.Flex;
                    card.EnableInClassList("reg-card--expanded", !isExpanded);
                });
                card.AddToClassList("reg-card--clickable");
            }

            return card;
        }

        private static VisualElement BuildPromptDetailPanel(List<PromptArgument> arguments)
        {
            VisualElement panel = new VisualElement();
            panel.AddToClassList("reg-detail");

            VisualElement divider = new VisualElement();
            divider.AddToClassList("reg-detail__divider");
            panel.Add(divider);

            Label headerLabel = new Label("ARGUMENTS");
            headerLabel.AddToClassList("reg-detail__header");
            panel.Add(headerLabel);

            foreach (PromptArgument arg in arguments)
            {
                VisualElement paramRow = new VisualElement();
                paramRow.AddToClassList("reg-param");

                VisualElement nameRow = new VisualElement();
                nameRow.AddToClassList("reg-param__header");

                Label nameLabel = new Label(arg.name);
                nameLabel.AddToClassList("reg-param__name");
                nameRow.Add(nameLabel);

                if (arg.required)
                {
                    Label reqLabel = new Label("required");
                    reqLabel.AddToClassList("reg-param__required");
                    nameRow.Add(reqLabel);
                }

                paramRow.Add(nameRow);

                if (!string.IsNullOrEmpty(arg.description))
                {
                    Label descLabel = new Label(arg.description);
                    descLabel.AddToClassList("reg-param__desc");
                    paramRow.Add(descLabel);
                }

                panel.Add(paramRow);
            }

            return panel;
        }

        #endregion

        #region Helpers

        private static bool MatchesFilter(string value, string filterLower)
        {
            return value != null && value.ToLowerInvariant().Contains(filterLower);
        }

        private static Label CreatePill(string text, string styleClass)
        {
            Label pill = new Label(text);
            pill.AddToClassList("pill");
            pill.AddToClassList(styleClass);
            return pill;
        }

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

            if (!string.IsNullOrEmpty(textField.value))
                placeholderLabel.style.display = DisplayStyle.None;
        }

        #endregion
    }
}
