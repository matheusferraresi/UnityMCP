using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnixxtyMCP.Editor;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    public static class SearchTools
    {
        [MCPTool("search_tools", "Search available tools by name, description, or category. Use with no args for a category overview.", Category = "Editor", ReadOnlyHint = true)]
        public static object SearchAvailableTools(
            [MCPParam("query", "Search names and descriptions")] string query = null,
            [MCPParam("category", "Filter by category")] string category = null,
            [MCPParam("include_schema", "Include inputSchema with parameter details (default: true)")] bool includeSchema = true)
        {
            bool hasQuery = !string.IsNullOrEmpty(query);
            bool hasCategory = !string.IsNullOrEmpty(category);

            // No args: return compact category summary
            if (!hasQuery && !hasCategory)
            {
                return BuildCategorySummary();
            }

            // With category and/or query: return matching tool details
            return BuildToolDetails(query, category, includeSchema);
        }

        private static string BuildCategorySummary()
        {
            var categoryGroups = ToolRegistry.GetDefinitionsByCategory();

            var summaryBuilder = new StringBuilder();
            summaryBuilder.AppendLine("Available tool categories:");

            foreach (var categoryGroup in categoryGroups)
            {
                var toolNames = categoryGroup.Select(tool => tool.name).ToList();
                int toolCount = toolNames.Count;
                string toolNameList = string.Join(", ", toolNames);
                summaryBuilder.AppendLine($"- {categoryGroup.Key} ({toolCount} tools): {toolNameList}");
            }

            summaryBuilder.Append("Use search_tools with 'category' or 'query' to explore further.");

            return summaryBuilder.ToString();
        }

        private static object BuildToolDetails(string query, string category, bool includeSchema)
        {
            var allDefinitions = ToolRegistry.GetDefinitions();
            var filteredDefinitions = allDefinitions;

            // Filter by category (case-insensitive)
            if (!string.IsNullOrEmpty(category))
            {
                filteredDefinitions = filteredDefinitions.Where(
                    tool => tool.category != null &&
                            tool.category.ToLowerInvariant() == category.ToLowerInvariant());
            }

            // Filter by query (case-insensitive substring match on name and description)
            if (!string.IsNullOrEmpty(query))
            {
                string lowerQuery = query.ToLowerInvariant();
                filteredDefinitions = filteredDefinitions.Where(
                    tool => (tool.name != null && tool.name.ToLowerInvariant().Contains(lowerQuery)) ||
                            (tool.description != null && tool.description.ToLowerInvariant().Contains(lowerQuery)));
            }

            var matchingTools = filteredDefinitions.ToList();

            var toolEntries = new List<Dictionary<string, object>>();

            foreach (var tool in matchingTools)
            {
                var toolEntry = new Dictionary<string, object>
                {
                    { "name", tool.name },
                    { "description", tool.description },
                    { "category", tool.category ?? "Uncategorized" }
                };

                if (tool.annotations != null)
                {
                    var annotationsDict = new Dictionary<string, object>();

                    if (tool.annotations.readOnlyHint.HasValue)
                        annotationsDict["readOnlyHint"] = tool.annotations.readOnlyHint.Value;
                    if (tool.annotations.destructiveHint.HasValue)
                        annotationsDict["destructiveHint"] = tool.annotations.destructiveHint.Value;
                    if (tool.annotations.idempotentHint.HasValue)
                        annotationsDict["idempotentHint"] = tool.annotations.idempotentHint.Value;
                    if (tool.annotations.openWorldHint.HasValue)
                        annotationsDict["openWorldHint"] = tool.annotations.openWorldHint.Value;
                    if (tool.annotations.title != null)
                        annotationsDict["title"] = tool.annotations.title;

                    if (annotationsDict.Count > 0)
                    {
                        toolEntry["annotations"] = annotationsDict;
                    }
                }

                if (includeSchema && tool.inputSchema != null)
                {
                    toolEntry["inputSchema"] = tool.inputSchema;
                }

                toolEntries.Add(toolEntry);
            }

            var result = new Dictionary<string, object>
            {
                { "tools", toolEntries }
            };

            // When query returns no matches, suggest similar tool names
            if (toolEntries.Count == 0 && !string.IsNullOrEmpty(query))
            {
                var suggestions = FindSimilarToolNames(query, allDefinitions);
                if (suggestions.Count > 0)
                    result["did_you_mean"] = suggestions;
            }

            return result;
        }

        /// <summary>
        /// Find tool names similar to the query using word overlap and substring matching.
        /// </summary>
        private static List<string> FindSimilarToolNames(string query, IEnumerable<ToolDefinition> allTools)
        {
            string lowerQuery = query.ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            // Split query into individual words for partial matching
            var queryWords = lowerQuery.Split('_').Where(w => w.Length > 0).ToList();

            var scored = new List<(string name, int score)>();

            foreach (var tool in allTools)
            {
                string toolName = tool.name.ToLowerInvariant();
                int score = 0;

                // Exact substring match in name
                if (toolName.Contains(lowerQuery))
                    score += 10;

                // Word overlap: each query word that appears in the tool name
                foreach (var word in queryWords)
                {
                    if (word.Length < 2) continue;
                    if (toolName.Contains(word))
                        score += 3;
                }

                // Edit distance bonus for very close names (within 3 edits)
                int dist = LevenshteinDistance(lowerQuery, toolName);
                if (dist <= 3)
                    score += (4 - dist) * 2;

                if (score > 0)
                    scored.Add((tool.name, score));
            }

            return scored
                .OrderByDescending(s => s.score)
                .Take(5)
                .Select(s => s.name)
                .ToList();
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            // Optimization: skip if lengths differ by more than threshold
            if (Math.Abs(a.Length - b.Length) > 5) return 100;

            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(
                        d[i - 1, j] + 1,
                        d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }
    }
}
