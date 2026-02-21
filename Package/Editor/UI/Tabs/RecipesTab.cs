using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.UI.Tabs
{
    /// <summary>
    /// Recipes tab: browse and execute scene recipes.
    /// </summary>
    public class RecipesTab : ITab
    {
        public VisualElement Root { get; }

        private readonly Label _summaryLabel;
        private readonly ScrollView _recipeListScrollView;
        private readonly VisualElement _recipeListContainer;
        private readonly VisualElement _emptyState;

        private List<RecipeInfo> _cachedRecipes;

        public RecipesTab()
        {
            Root = new VisualElement();
            Root.style.flexGrow = 1;

            // Summary bar
            VisualElement summaryBar = new VisualElement();
            summaryBar.AddToClassList("row--spaced");
            summaryBar.style.marginBottom = 8;

            _summaryLabel = new Label();
            _summaryLabel.AddToClassList("muted");
            summaryBar.Add(_summaryLabel);

            Button refreshButton = new Button(OnRefresh) { text = "Refresh" };
            refreshButton.AddToClassList("button--small");
            refreshButton.AddToClassList("button--accent");
            summaryBar.Add(refreshButton);

            Root.Add(summaryBar);

            // Recipe list scroll view
            _recipeListScrollView = new ScrollView(ScrollViewMode.Vertical);
            _recipeListScrollView.AddToClassList("scroll-view");
            Root.Add(_recipeListScrollView);

            _recipeListContainer = _recipeListScrollView.contentContainer;

            // Empty state
            _emptyState = new VisualElement();
            _emptyState.AddToClassList("empty-state");
            Label emptyLabel = new Label("No recipes registered.\nCreate recipes with the [MCPRecipe] attribute on static methods.");
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
            RecipeRegistry.RefreshRecipes();
            RefreshCache();
            RebuildList();
        }

        private void RefreshCache()
        {
            _cachedRecipes = RecipeRegistry.GetDefinitions().ToList();
            _summaryLabel.text = $"{_cachedRecipes.Count} recipes available";
        }

        #region List Building

        private void RebuildList()
        {
            _recipeListContainer.Clear();
            _emptyState.RemoveFromHierarchy();

            if (_cachedRecipes == null || _cachedRecipes.Count == 0)
            {
                _recipeListScrollView.Add(_emptyState);
                return;
            }

            foreach (RecipeInfo recipe in _cachedRecipes)
            {
                _recipeListContainer.Add(BuildRecipeCard(recipe));
            }
        }

        private VisualElement BuildRecipeCard(RecipeInfo recipe)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList("recipe-card");

            // Header: recipe name
            Label nameLabel = new Label(recipe.Name);
            nameLabel.AddToClassList("recipe-name");
            card.Add(nameLabel);

            // Description
            if (!string.IsNullOrEmpty(recipe.Description))
            {
                Label descLabel = new Label(recipe.Description);
                descLabel.AddToClassList("recipe-description");
                card.Add(descLabel);
            }

            // Parameters section
            List<RecipeParameterMetadata> parameters = recipe.GetParameterMetadata().ToList();
            if (parameters.Count > 0)
            {
                VisualElement paramsSection = new VisualElement();
                paramsSection.style.marginTop = 6;

                Label paramsHeader = new Label("Parameters");
                paramsHeader.AddToClassList("muted");
                paramsHeader.style.marginBottom = 2;
                paramsSection.Add(paramsHeader);

                foreach (RecipeParameterMetadata param in parameters)
                {
                    VisualElement paramRow = new VisualElement();
                    paramRow.AddToClassList("recipe-param-row");

                    Label paramName = new Label(param.Name);
                    paramName.AddToClassList("recipe-param-name");
                    paramName.AddToClassList("mono");
                    paramRow.Add(paramName);

                    Label paramType = new Label(param.JsonType);
                    paramType.AddToClassList("recipe-param-type");
                    paramRow.Add(paramType);

                    if (param.ParameterInfo.HasDefaultValue && param.ParameterInfo.DefaultValue != null)
                    {
                        Label paramDefault = new Label($"default: {param.ParameterInfo.DefaultValue}");
                        paramDefault.AddToClassList("recipe-param-default");
                        paramRow.Add(paramDefault);
                    }
                    else if (param.Required)
                    {
                        Label requiredLabel = new Label("required");
                        requiredLabel.AddToClassList("pill");
                        requiredLabel.AddToClassList("pill--warning");
                        requiredLabel.style.marginLeft = 4;
                        paramRow.Add(requiredLabel);
                    }

                    paramsSection.Add(paramRow);
                }

                card.Add(paramsSection);
            }

            // Execute button
            string recipeName = recipe.Name;
            Button executeButton = new Button(() => OnExecuteClicked(card, recipeName, parameters))
            {
                text = "Execute"
            };
            executeButton.AddToClassList("recipe-execute-button");
            card.Add(executeButton);

            return card;
        }

        #endregion

        #region Execute Flow

        private void OnExecuteClicked(VisualElement card, string recipeName, List<RecipeParameterMetadata> parameters)
        {
            // If no parameters, execute immediately
            if (parameters.Count == 0)
            {
                ExecuteRecipe(card, recipeName, new Dictionary<string, object>());
                return;
            }

            // Check if param form already open -- toggle off
            VisualElement existingForm = card.Q(name: "param-form");
            if (existingForm != null)
            {
                existingForm.RemoveFromHierarchy();
                return;
            }

            // Build parameter input form
            VisualElement paramForm = new VisualElement();
            paramForm.name = "param-form";
            paramForm.style.marginTop = 6;
            paramForm.style.paddingTop = 6;
            paramForm.style.borderTopWidth = 1;
            paramForm.style.borderTopColor = new Color(0.5f, 0.5f, 0.5f, 0.2f);

            Dictionary<string, VisualElement> inputFields = new Dictionary<string, VisualElement>();

            foreach (RecipeParameterMetadata param in parameters)
            {
                VisualElement inputField = CreateInputField(param);
                inputFields[param.Name] = inputField;
                paramForm.Add(inputField);
            }

            // Run + Cancel buttons
            VisualElement buttonRow = new VisualElement();
            buttonRow.AddToClassList("row");
            buttonRow.style.marginTop = 6;
            buttonRow.style.justifyContent = Justify.FlexEnd;

            Button cancelButton = new Button(() => paramForm.RemoveFromHierarchy()) { text = "Cancel" };
            cancelButton.AddToClassList("button--small");
            buttonRow.Add(cancelButton);

            Button runButton = new Button(() =>
            {
                Dictionary<string, object> arguments = CollectArguments(parameters, inputFields);
                ExecuteRecipe(card, recipeName, arguments);
                paramForm.RemoveFromHierarchy();
            }) { text = "Run" };
            runButton.AddToClassList("button--small");
            runButton.AddToClassList("button--accent");
            runButton.style.marginLeft = 4;
            buttonRow.Add(runButton);

            paramForm.Add(buttonRow);

            // Insert before the Execute button
            int executeButtonIndex = card.IndexOf(card.Q<Button>(className: "recipe-execute-button"));
            if (executeButtonIndex >= 0)
                card.Insert(executeButtonIndex, paramForm);
            else
                card.Add(paramForm);
        }

        private void ExecuteRecipe(VisualElement card, string recipeName, Dictionary<string, object> arguments)
        {
            try
            {
                RecipeRegistry.Invoke(recipeName, arguments);
                ShowResultIndicator(card, true, "Success");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[MCPServerWindow] Recipe '{recipeName}' failed: {exception.Message}");
                ShowResultIndicator(card, false, exception.Message);
            }
        }

        private void ShowResultIndicator(VisualElement card, bool success, string message)
        {
            // Remove existing indicator
            VisualElement existing = card.Q(name: "result-indicator");
            existing?.RemoveFromHierarchy();

            Label indicator = new Label(success ? "Executed successfully" : $"Failed: {message}");
            indicator.name = "result-indicator";
            indicator.AddToClassList("pill");
            indicator.AddToClassList(success ? "pill--success" : "pill--error");
            indicator.style.marginTop = 4;
            indicator.style.alignSelf = Align.FlexStart;
            card.Add(indicator);

            // Auto-remove after 3 seconds
            indicator.schedule.Execute(() => indicator.RemoveFromHierarchy()).StartingIn(3000);
        }

        #endregion

        #region Input Field Creation

        private static VisualElement CreateInputField(RecipeParameterMetadata param)
        {
            string label = param.Name;
            string jsonType = param.JsonType ?? "string";

            switch (jsonType)
            {
                case "boolean":
                {
                    Toggle toggle = new Toggle(label);
                    if (param.ParameterInfo.HasDefaultValue && param.ParameterInfo.DefaultValue is bool defaultBool)
                        toggle.value = defaultBool;
                    toggle.name = $"param-{param.Name}";
                    return toggle;
                }

                case "integer":
                {
                    IntegerField intField = new IntegerField(label);
                    if (param.ParameterInfo.HasDefaultValue && param.ParameterInfo.DefaultValue != null)
                        intField.value = Convert.ToInt32(param.ParameterInfo.DefaultValue);
                    intField.name = $"param-{param.Name}";
                    return intField;
                }

                case "number":
                {
                    FloatField floatField = new FloatField(label);
                    if (param.ParameterInfo.HasDefaultValue && param.ParameterInfo.DefaultValue != null)
                        floatField.value = Convert.ToSingle(param.ParameterInfo.DefaultValue);
                    floatField.name = $"param-{param.Name}";
                    return floatField;
                }

                default: // "string" and others
                {
                    TextField textField = new TextField(label);
                    if (param.ParameterInfo.HasDefaultValue && param.ParameterInfo.DefaultValue != null)
                        textField.value = param.ParameterInfo.DefaultValue.ToString();
                    textField.name = $"param-{param.Name}";
                    return textField;
                }
            }
        }

        private static Dictionary<string, object> CollectArguments(
            List<RecipeParameterMetadata> parameters,
            Dictionary<string, VisualElement> inputFields)
        {
            Dictionary<string, object> arguments = new Dictionary<string, object>();

            foreach (RecipeParameterMetadata param in parameters)
            {
                if (!inputFields.TryGetValue(param.Name, out VisualElement field)) continue;

                string fieldName = $"param-{param.Name}";
                string jsonType = param.JsonType ?? "string";

                switch (jsonType)
                {
                    case "boolean":
                        if (field is Toggle toggle)
                            arguments[param.Name] = toggle.value;
                        break;

                    case "integer":
                        if (field is IntegerField intField)
                            arguments[param.Name] = intField.value;
                        break;

                    case "number":
                        if (field is FloatField floatField)
                            arguments[param.Name] = (double)floatField.value;
                        break;

                    default:
                        if (field is TextField textField && !string.IsNullOrEmpty(textField.value))
                            arguments[param.Name] = textField.value;
                        break;
                }
            }

            return arguments;
        }

        #endregion
    }
}
