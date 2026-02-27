using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Utilities;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Play Mode method-level hot reload using Harmony 2.x.
    ///
    /// THE killer feature - NO other Unity MCP has this.
    ///
    /// Flow: Agent edits method → hot_patch applies in-memory via Harmony → game continues running.
    /// Patches revert on exiting play mode.
    ///
    /// Supports two modes:
    /// 1. Redirect: Point an existing method at another already-compiled method
    /// 2. Auto-detect: Compare old vs new source, detect changed methods, save to disk
    ///
    /// Runtime compilation uses Roslyn loaded dynamically from Unity's editor installation.
    /// No extra DLLs or defines needed - works on any Unity version that ships Roslyn.
    /// </summary>
    [InitializeOnLoad]
    public static class HotPatch
    {
        static HotPatch()
        {
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

        [MCPTool("hot_patch", "Hot-patch method bodies during Play Mode using Harmony. Edit code and see results instantly without domain reload.",
            Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action: patch (apply changes), redirect (point method at another), rollback (revert all), status (list active)",
                required: true, Enum = new[] { "patch", "redirect", "rollback", "status" })] string action,
            [MCPParam("class_name", "Fully qualified class name (e.g. 'PlayerController' or 'MyGame.PlayerController')")] string className = null,
            [MCPParam("method_name", "Method name to patch (e.g. 'Update', 'TakeDamage')")] string methodName = null,
            [MCPParam("new_body", "New method body code (compiled at runtime via Roslyn)")] string newBody = null,
            [MCPParam("target_class", "Target class containing the replacement method (for 'redirect' action)")] string targetClass = null,
            [MCPParam("target_method", "Target method name to redirect to (for 'redirect' action)")] string targetMethod = null,
            [MCPParam("script_path", "Script path relative to Assets/ - detects all changed methods automatically")] string scriptPath = null,
            [MCPParam("new_source", "Full new source code of the script (used with script_path for auto-detection)")] string newSource = null)
        {
            switch (action.ToLower())
            {
                case "patch":
                    return ExecutePatch(className, methodName, newBody, scriptPath, newSource);
                case "redirect":
                    return ExecuteRedirect(className, methodName, targetClass, targetMethod);
                case "rollback":
                    return ExecuteRollback();
                case "status":
                    return GetStatus();
                default:
                    throw MCPException.InvalidParams($"Unknown action '{action}'.");
            }
        }

        private static object ExecuteRedirect(string className, string methodName, string targetClass, string targetMethod)
        {
            if (!EditorApplication.isPlaying)
            {
                return new
                {
                    success = false,
                    error = "hot_patch only works in Play Mode.",
                    hint = "Enter play mode first with playmode_enter, then call hot_patch."
                };
            }

            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(methodName))
                throw MCPException.InvalidParams("class_name and method_name are required for redirect.");
            if (string.IsNullOrEmpty(targetClass) || string.IsNullOrEmpty(targetMethod))
                throw MCPException.InvalidParams("target_class and target_method are required for redirect.");

            Type sourceType = ResolveType(className);
            if (sourceType == null)
                return new { success = false, error = $"Source type '{className}' not found." };

            Type destType = ResolveType(targetClass);
            if (destType == null)
                return new { success = false, error = $"Target type '{targetClass}' not found." };

            var original = sourceType.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (original == null)
                return new { success = false, error = $"Method '{methodName}' not found on '{sourceType.FullName}'." };

            var replacement = destType.GetMethod(targetMethod,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (replacement == null)
                return new { success = false, error = $"Method '{targetMethod}' not found on '{destType.FullName}'." };

            try
            {
                string patchId = MethodPatcher.PatchMethod(original, replacement);
                return new
                {
                    success = true,
                    message = $"Redirected '{sourceType.Name}.{methodName}' → '{destType.Name}.{targetMethod}'",
                    patch_id = patchId,
                    engine = "harmony",
                    active_patches = MethodPatcher.ActivePatches.Count,
                    warning = "Patch will revert when exiting Play Mode."
                };
            }
            catch (Exception ex)
            {
                return new { success = false, error = $"Redirect failed: {ex.Message}" };
            }
        }

        private static object ExecutePatch(string className, string methodName, string newBody, string scriptPath, string newSource)
        {
            if (!EditorApplication.isPlaying)
            {
                return new
                {
                    success = false,
                    error = "hot_patch only works in Play Mode. Use manage_script + recompile_scripts for edit mode.",
                    hint = "Enter play mode first with playmode_enter, then call hot_patch."
                };
            }

            // Mode 1: Explicit method patch
            if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(methodName))
            {
                return PatchSingleMethod(className, methodName, newBody);
            }

            // Mode 2: Auto-detect changed methods from source diff
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
            Type targetType = ResolveType(className);
            if (targetType == null)
            {
                return new
                {
                    success = false,
                    error = $"Type '{className}' not found in any loaded assembly.",
                    hint = "Use the full type name if it's in a namespace."
                };
            }

            var method = targetType.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (method == null)
            {
                return new
                {
                    success = false,
                    error = $"Method '{methodName}' not found on '{targetType.FullName}'.",
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
                var replacement = BuildReplacementMethod(method, newBody, targetType);
                if (replacement == null)
                {
                    return new
                    {
                        success = false,
                        error = $"Runtime compilation failed. {(RoslynCompiler.IsAvailable ? "Check Unity console for details." : RoslynCompiler.LoadError)}",
                        roslyn_available = RoslynCompiler.IsAvailable,
                        hint = "Use action 'redirect' to point at an already-compiled method, " +
                               "or use manage_script to save changes to disk and exit play mode to recompile.",
                        body_saved = false
                    };
                }

                string patchId = MethodPatcher.PatchMethod(method, replacement);

                return new
                {
                    success = true,
                    message = $"Method '{targetType.Name}.{methodName}' patched successfully via Harmony.",
                    patch_id = patchId,
                    method = $"{targetType.FullName}.{methodName}",
                    engine = "harmony",
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
            string fullScriptPath = scriptPath.StartsWith("Assets/") || scriptPath.StartsWith("Assets\\")
                ? scriptPath : $"Assets/{scriptPath}";
            fullScriptPath = fullScriptPath.Replace('\\', '/');
            string absolutePath = Path.GetFullPath(fullScriptPath);

            if (!File.Exists(absolutePath))
                return new { success = false, error = $"Script not found at '{fullScriptPath}'." };

            string oldSource = File.ReadAllText(absolutePath, Encoding.UTF8);
            var changes = DetectMethodChanges(oldSource, newSource);

            if (changes.Count == 0)
                return new { success = true, changed = false, message = "No method body changes detected." };

            var patchable = new List<object>();
            var unpatchable = new List<object>();

            foreach (var change in changes)
            {
                if (change.changeType == "body_changed")
                {
                    patchable.Add(new
                    {
                        className = change.className,
                        methodName = change.methodName,
                        status = "body_changed",
                        can_hot_patch = true
                    });
                }
                else
                {
                    unpatchable.Add(new
                    {
                        className = change.className,
                        methodName = change.methodName,
                        status = change.changeType,
                        reason = change.changeType == "new_method" ? "New methods require recompilation" :
                                 change.changeType == "removed" ? "Removed methods require recompilation" :
                                 "Signature changes require recompilation"
                    });
                }
            }

            // Save new source to disk
            File.WriteAllText(absolutePath, newSource, Encoding.UTF8);

            return new
            {
                success = true,
                source_saved = true,
                script_path = fullScriptPath,
                patchable_methods = patchable.Count > 0 ? patchable : null,
                unpatchable_methods = unpatchable.Count > 0 ? unpatchable : null,
                requires_recompile = unpatchable.Count > 0,
                active_patches = MethodPatcher.ActivePatches.Count,
                message = $"Source saved. {patchable.Count} methods can be hot-patched, {unpatchable.Count} require recompile.",
                hint = patchable.Count > 0
                    ? "Use action 'redirect' to redirect patchable methods to replacement implementations, or exit play mode to recompile all changes."
                    : "Exit play mode and recompile to apply changes."
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
                harmony_version = typeof(Harmony).Assembly.GetName().Version.ToString(),
                roslyn_available = RoslynCompiler.IsAvailable,
                patch_engine = "harmony",
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

            foreach (var kvp in newMethods)
            {
                if (oldMethods.TryGetValue(kvp.Key, out var oldMethod))
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
            var classMatch = Regex.Match(source, @"\bclass\s+(\w+)");
            string className = classMatch.Success ? classMatch.Groups[1].Value : "Unknown";

            var methodPattern = new Regex(
                @"(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async)\s+)*" +
                @"(?:[\w<>\[\],\s]+?)\s+" +
                @"(\w+)\s*\([^)]*\)\s*\{",
                RegexOptions.Multiline);

            foreach (Match match in methodPattern.Matches(source))
            {
                string name = match.Groups[1].Value;
                if (name == className || name == "if" || name == "for" ||
                    name == "while" || name == "switch" || name == "catch" ||
                    name == "using" || name == "lock")
                    continue;

                int braceStart = match.Index + match.Length - 1;
                string body = ExtractBraceBlock(source, braceStart);

                if (body != null)
                {
                    string key = $"{className}.{name}";
                    methods[key] = new ExtractedMethod
                    {
                        className = className,
                        methodName = name,
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
            return source.Substring(openBraceIndex + 1, i - openBraceIndex - 2);
        }

        #endregion

        #region Method Compilation

        /// <summary>
        /// Build a replacement MethodInfo from a method body string.
        /// Dynamically loads Roslyn from Unity's editor installation for runtime compilation.
        /// </summary>
        private static MethodInfo BuildReplacementMethod(MethodInfo original, string newBody, Type ownerType)
        {
            if (!RoslynCompiler.IsAvailable)
            {
                Debug.LogWarning(
                    $"[HotPatch] Runtime compilation unavailable: {RoslynCompiler.LoadError} " +
                    $"Use action 'redirect' to redirect to an already-compiled method, " +
                    $"or save changes to disk and exit play mode to recompile.");
                return null;
            }

            return RoslynCompiler.CompileHarmonyPrefix(original, newBody, ownerType);
        }

        #endregion

        #region Type Resolution

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            var type = Type.GetType(typeName);
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) return type;
            }

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
