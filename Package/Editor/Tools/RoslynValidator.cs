using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;

#if USE_ROSLYN
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#endif

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Advanced script validation using Roslyn (when available) or enhanced structural analysis.
    /// Inspired by CoplayDev/unity-mcp's validate_script with full semantic diagnostics.
    ///
    /// To enable Roslyn:
    /// 1. Add Microsoft.CodeAnalysis.CSharp NuGet package to the project
    /// 2. Add USE_ROSLYN to Scripting Define Symbols (Player Settings)
    /// </summary>
    public static class RoslynValidator
    {
        [MCPTool("validate_script_advanced", "Validate C# script with enhanced structural analysis or full Roslyn semantic diagnostics",
            Category = "Asset", ReadOnlyHint = true)]
        public static object Validate(
            [MCPParam("path", "Script path relative to Assets/")] string path = null,
            [MCPParam("contents", "Inline script contents to validate (overrides path)")] string contents = null,
            [MCPParam("level", "Validation level: basic, standard, semantic (default: standard)",
                Enum = new[] { "basic", "standard", "semantic" })] string level = "standard")
        {
            if (string.IsNullOrEmpty(contents) && string.IsNullOrEmpty(path))
                throw MCPException.InvalidParams("Either 'path' or 'contents' is required.");

            // Load contents from file if not inline
            string scriptContents = contents;
            string scriptPath = null;
            if (string.IsNullOrEmpty(scriptContents))
            {
                scriptPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
                string fullPath = Path.GetFullPath(scriptPath);
                if (!File.Exists(fullPath))
                    throw MCPException.InvalidParams($"Script not found at '{scriptPath}'.");
                scriptContents = File.ReadAllText(fullPath, Encoding.UTF8);
            }

            var errors = new List<object>();
            var warnings = new List<object>();
            var info = new Dictionary<string, object>();

            // Level 1: Basic structural validation (always runs)
            ValidateStructure(scriptContents, errors, warnings, info);

            // Level 2: Standard - enhanced pattern checking
            if (level != "basic")
            {
                ValidatePatterns(scriptContents, errors, warnings, info);
            }

            // Level 3: Semantic - Roslyn if available
            if (level == "semantic")
            {
#if USE_ROSLYN
                ValidateWithRoslyn(scriptContents, errors, warnings, info);
#else
                info["roslyn_available"] = false;
                warnings.Add(new
                {
                    line = 0,
                    message = "Roslyn not available. Add Microsoft.CodeAnalysis.CSharp and define USE_ROSLYN for semantic validation."
                });
#endif
            }

            bool isValid = !errors.Any();

            return new
            {
                success = true,
                isValid,
                level,
                path = scriptPath,
                errors = errors.Count > 0 ? errors : null,
                warnings = warnings.Count > 0 ? warnings : null,
                info,
                lineCount = scriptContents.Split('\n').Length
            };
        }

        private static void ValidateStructure(string contents, List<object> errors, List<object> warnings, Dictionary<string, object> info)
        {
            // Balanced delimiters (ignoring strings and comments)
            int braces = 0, parens = 0, brackets = 0;
            bool inString = false, inChar = false, inLineComment = false, inBlockComment = false;
            int lineNumber = 1;

            for (int i = 0; i < contents.Length; i++)
            {
                char c = contents[i];
                char next = i + 1 < contents.Length ? contents[i + 1] : '\0';

                if (c == '\n') { lineNumber++; inLineComment = false; continue; }

                // Handle comment states
                if (!inString && !inChar)
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

                // Handle string/char states
                if (!inLineComment && !inBlockComment)
                {
                    if (c == '"' && !inChar) { inString = !inString; continue; }
                    if (c == '\'' && !inString) { inChar = !inChar; continue; }
                    if ((inString || inChar) && c == '\\') { i++; continue; }
                }

                if (inString || inChar || inLineComment || inBlockComment) continue;

                if (c == '{') braces++;
                else if (c == '}') braces--;
                else if (c == '(') parens++;
                else if (c == ')') parens--;
                else if (c == '[') brackets++;
                else if (c == ']') brackets--;
            }

            info["braces_balanced"] = braces == 0;
            info["parentheses_balanced"] = parens == 0;
            info["brackets_balanced"] = brackets == 0;

            if (braces != 0)
                errors.Add(new { line = 0, message = $"Unbalanced braces: {(braces > 0 ? $"{braces} unclosed" : $"{-braces} extra")}" });
            if (parens != 0)
                errors.Add(new { line = 0, message = $"Unbalanced parentheses: {(parens > 0 ? $"{parens} unclosed" : $"{-parens} extra")}" });
            if (brackets != 0)
                errors.Add(new { line = 0, message = $"Unbalanced brackets: {(brackets > 0 ? $"{brackets} unclosed" : $"{-brackets} extra")}" });

            // Class detection
            var classMatch = Regex.Match(contents, @"\bclass\s+(\w+)");
            info["has_class"] = classMatch.Success;
            if (classMatch.Success)
                info["class_name"] = classMatch.Groups[1].Value;

            // Namespace detection
            var nsMatch = Regex.Match(contents, @"\bnamespace\s+([\w.]+)");
            info["has_namespace"] = nsMatch.Success;
            if (nsMatch.Success)
                info["namespace"] = nsMatch.Groups[1].Value;
        }

        private static void ValidatePatterns(string contents, List<object> errors, List<object> warnings, Dictionary<string, object> info)
        {
            var lines = contents.Split('\n');

            // Check for common Unity-specific issues
            bool hasUnityUsing = contents.Contains("using UnityEngine;") || contents.Contains("using UnityEditor;");
            bool inheritsMonoBehaviour = Regex.IsMatch(contents, @":\s*MonoBehaviour");
            bool inheritsScriptableObject = Regex.IsMatch(contents, @":\s*ScriptableObject");
            bool inheritsEditorWindow = Regex.IsMatch(contents, @":\s*EditorWindow");

            info["is_monobehaviour"] = inheritsMonoBehaviour;
            info["is_scriptable_object"] = inheritsScriptableObject;
            info["is_editor_script"] = inheritsEditorWindow;

            if ((inheritsMonoBehaviour || inheritsScriptableObject) && !hasUnityUsing)
                warnings.Add(new { line = 1, message = "Inherits from Unity type but missing 'using UnityEngine;'" });

            // Check for deprecated API usage
            var deprecatedPatterns = new Dictionary<string, string>
            {
                { @"\bFindObjectOfType\b", "Use FindObjectsByType<T>(FindObjectsSortMode.None) instead (Unity 6)" },
                { @"\bFindObjectsOfType\b", "Use FindObjectsByType<T>(FindObjectsSortMode.None) instead (Unity 6)" },
                { @"\bInput\.GetKey\b", "Use Keyboard.current from Input System instead" },
                { @"\bInput\.GetAxis\b", "Use Input System actions instead" },
                { @"\bCinemachineVirtualCamera\b", "Use CinemachineCamera instead (Cinemachine 3.x)" },
                { @"\.Follow\s*=", "Use .Target.TrackingTarget instead (Cinemachine 3.x)" },
                { @"\.LookAt\s*=", "Use .Target.LookAtTarget instead (Cinemachine 3.x)" },
            };

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var pattern in deprecatedPatterns)
                {
                    if (Regex.IsMatch(lines[i], pattern.Key))
                    {
                        warnings.Add(new { line = i + 1, message = pattern.Value });
                    }
                }
            }

            // Check for common syntax errors
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimEnd();
                // Missing semicolons (heuristic: statement-like line not ending with { } ; , or being a comment/attribute)
                if (trimmed.Length > 0 &&
                    !trimmed.EndsWith(";") && !trimmed.EndsWith("{") && !trimmed.EndsWith("}") &&
                    !trimmed.EndsWith(",") && !trimmed.EndsWith("(") && !trimmed.EndsWith(")") &&
                    !trimmed.TrimStart().StartsWith("//") && !trimmed.TrimStart().StartsWith("*") &&
                    !trimmed.TrimStart().StartsWith("[") && !trimmed.TrimStart().StartsWith("#") &&
                    !trimmed.TrimStart().StartsWith("using") && !trimmed.TrimStart().StartsWith("namespace") &&
                    !trimmed.TrimStart().StartsWith("class") && !trimmed.TrimStart().StartsWith("public") &&
                    !trimmed.TrimStart().StartsWith("private") && !trimmed.TrimStart().StartsWith("protected") &&
                    !trimmed.TrimStart().StartsWith("internal") && !trimmed.TrimStart().StartsWith("static") &&
                    !trimmed.TrimStart().StartsWith("if") && !trimmed.TrimStart().StartsWith("else") &&
                    !trimmed.TrimStart().StartsWith("for") && !trimmed.TrimStart().StartsWith("while") &&
                    !trimmed.TrimStart().StartsWith("switch") && !trimmed.TrimStart().StartsWith("case") &&
                    !trimmed.TrimStart().StartsWith("try") && !trimmed.TrimStart().StartsWith("catch") &&
                    !trimmed.TrimStart().StartsWith("finally") && !trimmed.TrimStart().StartsWith("=>") &&
                    Regex.IsMatch(trimmed, @"^\s+\w+.*[^;{},)\s]$") &&
                    Regex.IsMatch(trimmed, @"=|return |throw "))
                {
                    warnings.Add(new { line = i + 1, message = "Possible missing semicolon" });
                }
            }
        }

#if USE_ROSLYN
        private static void ValidateWithRoslyn(string contents, List<object> errors, List<object> warnings, Dictionary<string, object> info)
        {
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(contents);

                // Collect assembly references from loaded assemblies
                var references = new List<MetadataReference>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                        {
                            references.Add(MetadataReference.CreateFromFile(assembly.Location));
                        }
                    }
                    catch { /* Skip assemblies that can't be referenced */ }
                }

                var compilation = CSharpCompilation.Create(
                    "ValidationAssembly",
                    syntaxTrees: new[] { syntaxTree },
                    references: references,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                var diagnostics = compilation.GetDiagnostics();
                info["roslyn_available"] = true;
                info["roslyn_diagnostic_count"] = diagnostics.Length;

                foreach (var diag in diagnostics)
                {
                    var lineSpan = diag.Location.GetMappedLineSpan();
                    var entry = new
                    {
                        line = lineSpan.StartLinePosition.Line + 1,
                        column = lineSpan.StartLinePosition.Character + 1,
                        code = diag.Id,
                        message = diag.GetMessage()
                    };

                    if (diag.Severity == DiagnosticSeverity.Error)
                        errors.Add(entry);
                    else if (diag.Severity == DiagnosticSeverity.Warning)
                        warnings.Add(entry);
                }
            }
            catch (Exception ex)
            {
                info["roslyn_error"] = ex.Message;
            }
        }
#endif
    }
}
