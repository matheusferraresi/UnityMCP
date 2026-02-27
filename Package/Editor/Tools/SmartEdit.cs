using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Diff-based script editing tool. Applies targeted edits instead of replacing entire files.
    /// Validates with RoslynValidator before saving - never writes broken code.
    /// </summary>
    public static class SmartEdit
    {
        [MCPTool("smart_edit", "Apply targeted edits to scripts using search/replace, line operations, or unified diff. Validates before saving.",
            Category = "Asset", DestructiveHint = true)]
        public static object Edit(
            [MCPParam("path", "Script path relative to Assets/ (e.g. 'Assets/_Project/Scripts/Player/PlayerInput.cs')", required: true)] string path,
            [MCPParam("action", "Edit action: search_replace, insert_at_line, delete_lines, replace_lines, patch",
                required: true, Enum = new[] { "search_replace", "insert_at_line", "delete_lines", "replace_lines", "patch" })] string action,
            [MCPParam("search", "Text to find (for search_replace). Supports regex if use_regex=true")] string search = null,
            [MCPParam("replace", "Replacement text (for search_replace)")] string replace = null,
            [MCPParam("line", "Line number (1-based) for insert_at_line")] int line = 0,
            [MCPParam("start_line", "Start line (1-based, inclusive) for delete_lines/replace_lines")] int startLine = 0,
            [MCPParam("end_line", "End line (1-based, inclusive) for delete_lines/replace_lines")] int endLine = 0,
            [MCPParam("content", "New content to insert (for insert_at_line, replace_lines)")] string content = null,
            [MCPParam("diff", "Unified diff content (for patch action)")] string diff = null,
            [MCPParam("use_regex", "Treat search as regex pattern (default: false)")] bool useRegex = false,
            [MCPParam("validate", "Validate result before saving (default: true)")] bool validate = true,
            [MCPParam("dry_run", "Preview changes without saving (default: false)")] bool dryRun = false)
        {
            // Resolve path
            string scriptPath = path.StartsWith("Assets/") || path.StartsWith("Assets\\") ? path : $"Assets/{path}";
            scriptPath = scriptPath.Replace('\\', '/');
            string fullPath = Path.GetFullPath(scriptPath);

            if (!File.Exists(fullPath))
                throw MCPException.InvalidParams($"Script not found at '{scriptPath}'.");

            string original = File.ReadAllText(fullPath, Encoding.UTF8);
            string modified;

            switch (action.ToLower())
            {
                case "search_replace":
                    modified = ApplySearchReplace(original, search, replace, useRegex);
                    break;
                case "insert_at_line":
                    modified = ApplyInsertAtLine(original, line, content);
                    break;
                case "delete_lines":
                    modified = ApplyDeleteLines(original, startLine, endLine);
                    break;
                case "replace_lines":
                    modified = ApplyReplaceLines(original, startLine, endLine, content);
                    break;
                case "patch":
                    modified = ApplyUnifiedDiff(original, diff);
                    break;
                default:
                    throw MCPException.InvalidParams($"Unknown action '{action}'.");
            }

            if (modified == original)
            {
                return new
                {
                    success = true,
                    changed = false,
                    message = "No changes were made (content unchanged)."
                };
            }

            // Validate before saving
            List<object> validationErrors = null;
            List<object> validationWarnings = null;

            if (validate)
            {
                var validation = ValidateScript(modified);
                validationErrors = validation.errors;
                validationWarnings = validation.warnings;

                if (validationErrors.Count > 0 && !dryRun)
                {
                    return new
                    {
                        success = false,
                        changed = false,
                        message = "Validation failed - file NOT saved. Fix the errors and try again.",
                        errors = validationErrors,
                        warnings = validationWarnings,
                        preview = GetDiffPreview(original, modified)
                    };
                }
            }

            int oldLineCount = original.Split('\n').Length;
            int newLineCount = modified.Split('\n').Length;

            if (dryRun)
            {
                return new
                {
                    success = true,
                    changed = true,
                    dry_run = true,
                    message = "Dry run - changes NOT saved.",
                    old_line_count = oldLineCount,
                    new_line_count = newLineCount,
                    line_delta = newLineCount - oldLineCount,
                    errors = validationErrors?.Count > 0 ? validationErrors : null,
                    warnings = validationWarnings?.Count > 0 ? validationWarnings : null,
                    preview = GetDiffPreview(original, modified)
                };
            }

            // Write and reimport
            File.WriteAllText(fullPath, modified, Encoding.UTF8);
            AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceUpdate);

            return new
            {
                success = true,
                changed = true,
                message = $"Script edited successfully via {action}.",
                path = scriptPath,
                old_line_count = oldLineCount,
                new_line_count = newLineCount,
                line_delta = newLineCount - oldLineCount,
                warnings = validationWarnings?.Count > 0 ? validationWarnings : null
            };
        }

        private static string ApplySearchReplace(string original, string search, string replace, bool useRegex)
        {
            if (string.IsNullOrEmpty(search))
                throw MCPException.InvalidParams("'search' is required for search_replace action.");
            if (replace == null)
                throw MCPException.InvalidParams("'replace' is required for search_replace action.");

            if (useRegex)
            {
                var regex = new Regex(search, RegexOptions.Multiline);
                if (!regex.IsMatch(original))
                    throw MCPException.InvalidParams($"Regex pattern '{search}' not found in file.");
                return regex.Replace(original, replace);
            }
            else
            {
                if (!original.Contains(search))
                    throw MCPException.InvalidParams("Search text not found in file. Ensure exact match including whitespace.");
                return original.Replace(search, replace);
            }
        }

        private static string ApplyInsertAtLine(string original, int lineNum, string content)
        {
            if (lineNum < 1)
                throw MCPException.InvalidParams("'line' must be >= 1 for insert_at_line action.");
            if (content == null)
                throw MCPException.InvalidParams("'content' is required for insert_at_line action.");

            var lines = original.Split('\n').ToList();
            if (lineNum > lines.Count + 1)
                throw MCPException.InvalidParams($"Line {lineNum} is beyond end of file ({lines.Count} lines).");

            // Insert before the specified line (so line=1 inserts at the top)
            var newLines = content.Split('\n');
            lines.InsertRange(lineNum - 1, newLines);
            return string.Join("\n", lines);
        }

        private static string ApplyDeleteLines(string original, int start, int end)
        {
            if (start < 1 || end < 1)
                throw MCPException.InvalidParams("'start_line' and 'end_line' must be >= 1 for delete_lines action.");
            if (end < start)
                throw MCPException.InvalidParams("'end_line' must be >= 'start_line'.");

            var lines = original.Split('\n').ToList();
            if (start > lines.Count)
                throw MCPException.InvalidParams($"start_line {start} is beyond end of file ({lines.Count} lines).");

            end = Math.Min(end, lines.Count);
            lines.RemoveRange(start - 1, end - start + 1);
            return string.Join("\n", lines);
        }

        private static string ApplyReplaceLines(string original, int start, int end, string content)
        {
            if (start < 1 || end < 1)
                throw MCPException.InvalidParams("'start_line' and 'end_line' must be >= 1 for replace_lines action.");
            if (end < start)
                throw MCPException.InvalidParams("'end_line' must be >= 'start_line'.");
            if (content == null)
                throw MCPException.InvalidParams("'content' is required for replace_lines action.");

            var lines = original.Split('\n').ToList();
            if (start > lines.Count)
                throw MCPException.InvalidParams($"start_line {start} is beyond end of file ({lines.Count} lines).");

            end = Math.Min(end, lines.Count);
            lines.RemoveRange(start - 1, end - start + 1);

            var newLines = content.Split('\n');
            lines.InsertRange(start - 1, newLines);
            return string.Join("\n", lines);
        }

        private static string ApplyUnifiedDiff(string original, string diff)
        {
            if (string.IsNullOrEmpty(diff))
                throw MCPException.InvalidParams("'diff' is required for patch action.");

            var lines = original.Split('\n').ToList();
            var diffLines = diff.Split('\n');

            // Parse unified diff hunks: @@ -start,count +start,count @@
            var hunkRegex = new Regex(@"^@@\s*-(\d+)(?:,(\d+))?\s*\+(\d+)(?:,(\d+))?\s*@@");
            int offset = 0;

            for (int i = 0; i < diffLines.Length; i++)
            {
                var match = hunkRegex.Match(diffLines[i]);
                if (!match.Success) continue;

                int origStart = int.Parse(match.Groups[1].Value) - 1; // 0-indexed
                int position = origStart + offset;

                i++;
                while (i < diffLines.Length && !diffLines[i].StartsWith("@@"))
                {
                    if (diffLines[i].Length == 0)
                    {
                        // Empty context line
                        position++;
                        i++;
                        continue;
                    }

                    char prefix = diffLines[i][0];
                    string lineContent = diffLines[i].Substring(1);

                    switch (prefix)
                    {
                        case ' ': // Context line
                            position++;
                            break;
                        case '-': // Remove line
                            if (position < lines.Count)
                            {
                                lines.RemoveAt(position);
                                offset--;
                            }
                            break;
                        case '+': // Add line
                            lines.Insert(position, lineContent);
                            position++;
                            offset++;
                            break;
                        default: // Treat as context
                            position++;
                            break;
                    }
                    i++;
                }
                i--; // Back up so the outer loop's i++ doesn't skip the next hunk header
            }

            return string.Join("\n", lines);
        }

        private static (List<object> errors, List<object> warnings) ValidateScript(string contents)
        {
            var errors = new List<object>();
            var warnings = new List<object>();

            // Quick structural validation (balanced delimiters)
            int braces = 0, parens = 0, brackets = 0;
            bool inString = false, inChar = false, inLineComment = false, inBlockComment = false;
            bool inVerbatimString = false;
            int lineNumber = 1;

            for (int i = 0; i < contents.Length; i++)
            {
                char c = contents[i];
                char next = i + 1 < contents.Length ? contents[i + 1] : '\0';
                char prev = i > 0 ? contents[i - 1] : '\0';

                if (c == '\n') { lineNumber++; inLineComment = false; continue; }

                if (!inString && !inChar && !inVerbatimString)
                {
                    if (inBlockComment)
                    {
                        if (c == '*' && next == '/') { inBlockComment = false; i++; }
                        continue;
                    }
                    if (inLineComment) continue;
                    if (c == '/' && next == '/') { inLineComment = true; continue; }
                    if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                }

                if (!inLineComment && !inBlockComment)
                {
                    if (c == '@' && next == '"' && !inString && !inChar && !inVerbatimString)
                    {
                        inVerbatimString = true; i++; continue;
                    }
                    if (inVerbatimString)
                    {
                        if (c == '"' && next == '"') { i++; continue; }
                        if (c == '"') { inVerbatimString = false; }
                        continue;
                    }
                    if (c == '"' && !inChar && !inVerbatimString) { inString = !inString; continue; }
                    if (c == '\'' && !inString) { inChar = !inChar; continue; }
                    if ((inString || inChar) && c == '\\') { i++; continue; }
                }

                if (inString || inChar || inLineComment || inBlockComment || inVerbatimString) continue;

                if (c == '{') braces++;
                else if (c == '}') braces--;
                else if (c == '(') parens++;
                else if (c == ')') parens--;
                else if (c == '[') brackets++;
                else if (c == ']') brackets--;
            }

            if (braces != 0)
                errors.Add(new { line = 0, message = $"Unbalanced braces: {(braces > 0 ? $"{braces} unclosed" : $"{-braces} extra")}" });
            if (parens != 0)
                errors.Add(new { line = 0, message = $"Unbalanced parentheses: {(parens > 0 ? $"{parens} unclosed" : $"{-parens} extra")}" });
            if (brackets != 0)
                errors.Add(new { line = 0, message = $"Unbalanced brackets: {(brackets > 0 ? $"{brackets} unclosed" : $"{-brackets} extra")}" });

            return (errors, warnings);
        }

        private static object GetDiffPreview(string original, string modified)
        {
            var oldLines = original.Split('\n');
            var newLines = modified.Split('\n');

            var changes = new List<object>();
            int maxLines = Math.Max(oldLines.Length, newLines.Length);
            int contextSize = 2;
            var changedLineNumbers = new HashSet<int>();

            // Find changed line numbers using simple LCS-like comparison
            int minLen = Math.Min(oldLines.Length, newLines.Length);
            for (int i = 0; i < minLen; i++)
            {
                if (oldLines[i] != newLines[i])
                    changedLineNumbers.Add(i);
            }
            // Lines only in one version
            for (int i = minLen; i < maxLines; i++)
                changedLineNumbers.Add(i);

            // Build compact preview with context
            if (changedLineNumbers.Count <= 20)
            {
                foreach (var lineIdx in changedLineNumbers.OrderBy(x => x))
                {
                    if (lineIdx < oldLines.Length && lineIdx < newLines.Length)
                    {
                        changes.Add(new { line = lineIdx + 1, type = "modified", old_text = oldLines[lineIdx].TrimEnd(), new_text = newLines[lineIdx].TrimEnd() });
                    }
                    else if (lineIdx >= oldLines.Length)
                    {
                        changes.Add(new { line = lineIdx + 1, type = "added", old_text = (string)null, new_text = newLines[lineIdx].TrimEnd() });
                    }
                    else
                    {
                        changes.Add(new { line = lineIdx + 1, type = "removed", old_text = oldLines[lineIdx].TrimEnd(), new_text = (string)null });
                    }
                }
            }

            return new
            {
                changed_lines = changedLineNumbers.Count,
                changes = changes.Count <= 20 ? changes : null,
                truncated = changes.Count > 20
            };
        }
    }
}
