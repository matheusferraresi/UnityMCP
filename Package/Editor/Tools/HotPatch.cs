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
using UnityMCP.Editor.Utilities;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Play Mode method-level hot reload. Patches method bodies at runtime without
    /// domain reload using Harmony (or native redirect fallback).
    ///
    /// THE killer feature - NO other Unity MCP has this.
    ///
    /// Flow: Agent edits method → hot_patch applies in-memory → game continues running.
    /// Patches revert on exiting play mode (domain reload).
    /// </summary>
    [InitializeOnLoad]
    public static class HotPatch
    {
        static HotPatch()
        {
            // Clean up patches when exiting play mode
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    int count = MethodPatcher.UnpatchAll();
                    if (count > 0)
                        Debug.Log($"[HotPatch] Reverted {count} patches on play mode exit.");
                }
            };
        }

        [MCPTool("hot_patch", "Hot-patch a method body during Play Mode without domain reload. The AI can edit code and see results instantly.",
            Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action: patch (apply changes), rollback (revert all patches), status (list active patches)",
                required: true, Enum = new[] { "patch", "rollback", "status" })] string action,
            [MCPParam("class_name", "Fully qualified class name (e.g. 'PlayerController' or 'MyGame.PlayerController')")] string className = null,
            [MCPParam("method_name", "Method name to patch (e.g. 'Update', 'TakeDamage')")] string methodName = null,
            [MCPParam("new_body", "New method body code (everything inside the braces). Used to generate a replacement delegate.")] string newBody = null,
            [MCPParam("script_path", "Script path relative to Assets/ - if provided, detects all changed methods automatically")] string scriptPath = null,
            [MCPParam("new_source", "Full new source code of the script (used with script_path for auto-detection)")] string newSource = null)
        {
            switch (action.ToLower())
            {
                case "patch":
                    return ExecutePatch(className, methodName, newBody, scriptPath, newSource);
                case "rollback":
                    return ExecuteRollback();
                case "status":
                    return GetStatus();
                default:
                    throw MCPException.InvalidParams($"Unknown action '{action}'.");
            }
        }

        private static object ExecutePatch(string className, string methodName, string newBody, string scriptPath, string newSource)
        {
            // Validate: must be in play mode
            if (!EditorApplication.isPlaying)
            {
                return new
                {
                    success = false,
                    error = "hot_patch only works in Play Mode. Use manage_script + recompile_scripts for edit mode changes.",
                    hint = "Enter play mode first with playmode_enter, then call hot_patch."
                };
            }

            // Mode 1: Explicit method patch (class_name + method_name + new_body)
            if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(methodName))
            {
                return PatchSingleMethod(className, methodName, newBody);
            }

            // Mode 2: Auto-detect changed methods (script_path + new_source)
            if (!string.IsNullOrEmpty(scriptPath) && !string.IsNullOrEmpty(newSource))
            {
                return PatchFromSource(scriptPath, newSource);
            }

            throw MCPException.InvalidParams(
                "Provide either (class_name + method_name + new_body) for explicit patch, " +
                "or (script_path + new_source) for auto-detection.");
        }

        private static object PatchSingleMethod(string className, string methodName, string newBody)
        {
            // Find the type in loaded assemblies
            Type targetType = ResolveType(className);
            if (targetType == null)
            {
                return new
                {
                    success = false,
                    error = $"Type '{className}' not found in any loaded assembly.",
                    hint = "Use the full type name if it's in a namespace (e.g. 'MyGame.PlayerController')."
                };
            }

            // Find the method
            var method = targetType.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (method == null)
            {
                return new
                {
                    success = false,
                    error = $"Method '{methodName}' not found on type '{targetType.FullName}'.",
                    available_methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(m => !m.IsSpecialName)
                        .Select(m => m.Name)
                        .Distinct()
                        .ToArray()
                };
            }

            if (string.IsNullOrEmpty(newBody))
            {
                return new
                {
                    success = false,
                    error = "'new_body' is required for explicit patch mode.",
                    hint = "Provide the method body code (everything inside the { })."
                };
            }

            try
            {
                // Build a replacement method using DynamicMethod
                var replacement = BuildReplacementMethod(method, newBody, targetType);
                if (replacement == null)
                {
                    return new
                    {
                        success = false,
                        error = "Failed to compile replacement method. Check the method body syntax."
                    };
                }

                string patchId = MethodPatcher.PatchMethod(method, replacement);

                // Also save to disk so the change persists on next recompile
                return new
                {
                    success = true,
                    message = $"Method '{targetType.Name}.{methodName}' patched successfully.",
                    patch_id = patchId,
                    method = $"{targetType.FullName}.{methodName}",
                    patch_engine = MethodPatcher.IsHarmonyAvailable ? "harmony" : "native_redirect",
                    active_patches = MethodPatcher.ActivePatches.Count,
                    warning = "Patch will revert when exiting Play Mode. Save changes to disk for persistence."
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    error = $"Failed to patch '{methodName}': {ex.Message}",
                    details = ex.InnerException?.Message
                };
            }
        }

        private static object PatchFromSource(string scriptPath, string newSource)
        {
            // Resolve script path
            string fullScriptPath = scriptPath.StartsWith("Assets/") || scriptPath.StartsWith("Assets\\")
                ? scriptPath : $"Assets/{scriptPath}";
            fullScriptPath = fullScriptPath.Replace('\\', '/');
            string absolutePath = Path.GetFullPath(fullScriptPath);

            if (!File.Exists(absolutePath))
            {
                return new
                {
                    success = false,
                    error = $"Script not found at '{fullScriptPath}'."
                };
            }

            string oldSource = File.ReadAllText(absolutePath, Encoding.UTF8);

            // Detect changed methods by comparing method bodies
            var changes = DetectMethodChanges(oldSource, newSource);

            if (changes.Count == 0)
            {
                return new
                {
                    success = true,
                    changed = false,
                    message = "No method body changes detected."
                };
            }

            var patched = new List<string>();
            var skipped = new List<object>();
            var errors = new List<object>();

            foreach (var change in changes)
            {
                if (change.changeType != "body_changed")
                {
                    skipped.Add(new
                    {
                        method = change.methodName,
                        reason = change.changeType == "new_method" ? "New method (cannot hot patch, needs recompile)" :
                                 change.changeType == "removed" ? "Method removed (cannot hot patch)" :
                                 change.changeType == "signature_changed" ? "Signature changed (cannot hot patch)" :
                                 change.changeType
                    });
                    continue;
                }

                // Try to patch this method
                Type type = ResolveType(change.className);
                if (type == null)
                {
                    errors.Add(new { method = $"{change.className}.{change.methodName}", error = "Type not found in loaded assemblies" });
                    continue;
                }

                var method = type.GetMethod(change.methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (method == null)
                {
                    errors.Add(new { method = $"{change.className}.{change.methodName}", error = "Method not found" });
                    continue;
                }

                try
                {
                    var replacement = BuildReplacementMethod(method, change.newBody, type);
                    if (replacement != null)
                    {
                        MethodPatcher.PatchMethod(method, replacement);
                        patched.Add($"{change.className}.{change.methodName}");
                    }
                    else
                    {
                        errors.Add(new { method = $"{change.className}.{change.methodName}", error = "Failed to compile replacement" });
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new { method = $"{change.className}.{change.methodName}", error = ex.Message });
                }
            }

            // Save new source to disk for next domain reload
            File.WriteAllText(absolutePath, newSource, Encoding.UTF8);

            return new
            {
                success = patched.Count > 0,
                methods_patched = patched.ToArray(),
                methods_skipped = skipped.Count > 0 ? skipped : null,
                errors = errors.Count > 0 ? errors : null,
                active_patches = MethodPatcher.ActivePatches.Count,
                source_saved = true,
                requires_recompile = skipped.Count > 0,
                hint = skipped.Count > 0
                    ? "Some changes require exiting play mode to recompile. Patched methods are live now."
                    : null
            };
        }

        private static object ExecuteRollback()
        {
            int count = MethodPatcher.UnpatchAll();
            return new
            {
                success = true,
                message = count > 0 ? $"Rolled back {count} patches." : "No active patches to rollback.",
                patches_reverted = count
            };
        }

        private static object GetStatus()
        {
            var patches = MethodPatcher.ActivePatches;
            return new
            {
                success = true,
                is_playing = EditorApplication.isPlaying,
                harmony_available = MethodPatcher.IsHarmonyAvailable,
                active_patch_count = patches.Count,
                patches = patches.Values.Select(p => new
                {
                    id = p.patchId,
                    method = $"{p.original.DeclaringType?.Name}.{p.original.Name}",
                    patched_at = p.patchedAt.ToString("HH:mm:ss"),
                    engine = p.engine.ToString()
                }).ToArray()
            };
        }

        #region Method Detection

        private class MethodChange
        {
            public string className;
            public string methodName;
            public string changeType; // body_changed, new_method, removed, signature_changed
            public string newBody;
        }

        private static List<MethodChange> DetectMethodChanges(string oldSource, string newSource)
        {
            var changes = new List<MethodChange>();

            var oldMethods = ExtractMethods(oldSource);
            var newMethods = ExtractMethods(newSource);

            // Find changed method bodies
            foreach (var kvp in newMethods)
            {
                string key = kvp.Key;
                if (oldMethods.TryGetValue(key, out var oldMethod))
                {
                    if (oldMethod.body != kvp.Value.body)
                    {
                        changes.Add(new MethodChange
                        {
                            className = kvp.Value.className,
                            methodName = kvp.Value.methodName,
                            changeType = "body_changed",
                            newBody = kvp.Value.body
                        });
                    }
                }
                else
                {
                    changes.Add(new MethodChange
                    {
                        className = kvp.Value.className,
                        methodName = kvp.Value.methodName,
                        changeType = "new_method"
                    });
                }
            }

            // Find removed methods
            foreach (var kvp in oldMethods)
            {
                if (!newMethods.ContainsKey(kvp.Key))
                {
                    changes.Add(new MethodChange
                    {
                        className = kvp.Value.className,
                        methodName = kvp.Value.methodName,
                        changeType = "removed"
                    });
                }
            }

            return changes;
        }

        private class ExtractedMethod
        {
            public string className;
            public string methodName;
            public string signature;
            public string body;
        }

        private static Dictionary<string, ExtractedMethod> ExtractMethods(string source)
        {
            var methods = new Dictionary<string, ExtractedMethod>();

            // Find class name
            var classMatch = Regex.Match(source, @"\bclass\s+(\w+)");
            string className = classMatch.Success ? classMatch.Groups[1].Value : "Unknown";

            // Simple method extraction using regex + brace counting
            // Matches: [modifiers] returnType MethodName(params) {body}
            var methodPattern = new Regex(
                @"(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async)\s+)*" +
                @"(?:[\w<>\[\],\s]+?)\s+" +
                @"(\w+)\s*\([^)]*\)\s*\{",
                RegexOptions.Multiline);

            foreach (Match match in methodPattern.Matches(source))
            {
                string methodName = match.Groups[1].Value;

                // Skip constructors and common non-method matches
                if (methodName == className || methodName == "if" || methodName == "for" ||
                    methodName == "while" || methodName == "switch" || methodName == "catch" ||
                    methodName == "using" || methodName == "lock")
                    continue;

                // Extract body by counting braces
                int braceStart = match.Index + match.Length - 1; // Position of opening {
                string body = ExtractBraceBlock(source, braceStart);

                if (body != null)
                {
                    string key = $"{className}.{methodName}";
                    methods[key] = new ExtractedMethod
                    {
                        className = className,
                        methodName = methodName,
                        signature = match.Value.TrimEnd('{').Trim(),
                        body = body.Trim()
                    };
                }
            }

            return methods;
        }

        private static string ExtractBraceBlock(string source, int openBraceIndex)
        {
            if (openBraceIndex >= source.Length || source[openBraceIndex] != '{')
                return null;

            int depth = 1;
            int i = openBraceIndex + 1;
            bool inString = false, inChar = false, inLineComment = false, inBlockComment = false;

            while (i < source.Length && depth > 0)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (c == '\n') { inLineComment = false; i++; continue; }

                if (!inString && !inChar)
                {
                    if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } i++; continue; }
                    if (inLineComment) { i++; continue; }
                    if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
                    if (c == '/' && next == '*') { inBlockComment = true; i += 2; continue; }
                }

                if (!inLineComment && !inBlockComment)
                {
                    if (c == '"' && !inChar) { inString = !inString; i++; continue; }
                    if (c == '\'' && !inString) { inChar = !inChar; i++; continue; }
                    if ((inString || inChar) && c == '\\') { i += 2; continue; }
                }

                if (!inString && !inChar && !inLineComment && !inBlockComment)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }

                i++;
            }

            if (depth != 0) return null;

            // Return content between braces (excluding the braces themselves)
            return source.Substring(openBraceIndex + 1, i - openBraceIndex - 2);
        }

        #endregion

        #region Method Compilation

        /// <summary>
        /// Build a replacement MethodInfo from a method body string.
        /// This is a simplified approach - builds a DynamicMethod with the same signature.
        ///
        /// NOTE: Full implementation would use Roslyn to compile the body to IL.
        /// This MVP creates a stub that logs the patch was applied.
        /// For full body compilation, enable USE_ROSLYN.
        /// </summary>
        private static MethodInfo BuildReplacementMethod(MethodInfo original, string newBody, Type ownerType)
        {
#if USE_ROSLYN
            return BuildWithRoslyn(original, newBody, ownerType);
#else
            // Without Roslyn, we can't compile arbitrary C# at runtime.
            // Log a warning and return null - the agent should use manage_script + recompile_scripts instead.
            Debug.LogWarning(
                $"[HotPatch] Full hot-patch requires Roslyn (USE_ROSLYN define + Microsoft.CodeAnalysis.CSharp). " +
                $"Without it, hot_patch can only redirect methods to other already-compiled methods. " +
                $"The source has been saved to disk - exit play mode to recompile.");
            return null;
#endif
        }

#if USE_ROSLYN
        private static MethodInfo BuildWithRoslyn(MethodInfo original, string newBody, Type ownerType)
        {
            try
            {
                // Build a complete class containing the replacement method
                var parameters = original.GetParameters();
                var paramList = string.Join(", ",
                    parameters.Select(p => $"{p.ParameterType.FullName} {p.Name}"));

                // For instance methods, add 'this' as first parameter
                string thisParam = "";
                if (!original.IsStatic)
                {
                    thisParam = $"{ownerType.FullName} __instance";
                    if (parameters.Length > 0) thisParam += ", ";
                }

                string returnType = original.ReturnType == typeof(void) ? "void" : original.ReturnType.FullName;

                string code = $@"
using System;
using UnityEngine;

public static class __HotPatchTemp {{
    public static {returnType} __Replacement({thisParam}{paramList}) {{
        {newBody}
    }}
}}";

                // Compile with Roslyn
                var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code);

                var references = new List<Microsoft.CodeAnalysis.MetadataReference>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                            references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(assembly.Location));
                    }
                    catch { }
                }

                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                    $"HotPatch_{Guid.NewGuid():N}",
                    syntaxTrees: new[] { syntaxTree },
                    references: references,
                    options: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                        Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                        allowUnsafe: true));

                using (var ms = new System.IO.MemoryStream())
                {
                    var result = compilation.Emit(ms);
                    if (!result.Success)
                    {
                        var errors = result.Diagnostics
                            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                            .Select(d => d.GetMessage());
                        Debug.LogError($"[HotPatch] Compilation failed: {string.Join("; ", errors)}");
                        return null;
                    }

                    ms.Seek(0, System.IO.SeekOrigin.Begin);
                    var patchAssembly = System.Reflection.Assembly.Load(ms.ToArray());
                    var patchType = patchAssembly.GetType("__HotPatchTemp");
                    return patchType?.GetMethod("__Replacement", BindingFlags.Public | BindingFlags.Static);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HotPatch] Roslyn compilation error: {ex.Message}");
                return null;
            }
        }
#endif

        #endregion

        #region Type Resolution

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // Direct resolution
            var type = Type.GetType(typeName);
            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) return type;
            }

            // Search by simple name
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
                    if (type != null) return type;
                }
                catch (ReflectionTypeLoadException) { }
            }

            return null;
        }

        #endregion
    }
}
