using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnixxtyMCP.Editor.Core;
using UnixxtyMCP.Editor.Utilities;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Execute arbitrary C# code in the Unity Editor outside Play Mode.
    /// Uses Roslyn runtime compilation (same infrastructure as hot_patch and validate_script_advanced).
    /// Covers operations like modifying Build Settings, Project Settings, Editor Preferences, etc.
    /// </summary>
    public static class EditorEval
    {
        [MCPTool("editor_eval", "Compile and execute arbitrary C# code in the Unity Editor. " +
            "Uses Roslyn runtime compilation. Ideal for one-off editor operations like modifying " +
            "Build Settings, Project Settings, or running editor automation that has no dedicated tool.",
            Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("code", "C# statements to compile and execute. Has access to UnityEngine, UnityEditor, " +
                "System, System.Collections.Generic, System.Linq, and System.IO namespaces. " +
                "Use 'return <expr>;' to return a value, or omit return for void operations.",
                required: true)] string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw MCPException.InvalidParams("'code' parameter is required and cannot be empty.");

            if (!RoslynCompiler.IsAvailable)
            {
                return new
                {
                    success = false,
                    error = $"Roslyn compiler not available: {RoslynCompiler.LoadError}",
                    hint = "Roslyn is loaded from Unity's editor installation. This should work on any standard Unity install."
                };
            }

            // Detect if code contains a return statement — if not, append one
            string bodyCode = code.TrimEnd();
            if (!ContainsReturn(bodyCode))
            {
                // Ensure trailing semicolon
                if (!bodyCode.EndsWith(";"))
                    bodyCode += ";";
                bodyCode += "\nreturn null;";
            }

            string wrappedCode = WrapInClass(bodyCode);

            // Log for auditability
            Debug.Log($"[EditorEval] Compiling:\n{code}");

            // Compile
            var result = RoslynCompiler.CompileWithDiagnostics(wrappedCode);
            if (!result.success)
            {
                return new
                {
                    success = false,
                    error = "Compilation failed",
                    errors = result.errors,
                    hint = "Check your code syntax. The code is wrapped in a static method with " +
                           "access to System, UnityEngine, and UnityEditor namespaces."
                };
            }

            // Load and execute
            try
            {
                var assembly = Assembly.Load(result.assemblyBytes);
                var type = assembly.GetType("__EditorEvalTemp");
                if (type == null)
                {
                    return new
                    {
                        success = false,
                        error = "Internal error: compiled assembly missing __EditorEvalTemp type."
                    };
                }

                var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    return new
                    {
                        success = false,
                        error = "Internal error: compiled assembly missing Execute method."
                    };
                }

                object returnValue = method.Invoke(null, null);

                string resultStr = null;
                if (returnValue != null)
                {
                    resultStr = Stringify(returnValue);
                }

                Debug.Log($"[EditorEval] Executed successfully. Result: {resultStr ?? "(void)"}");

                return new
                {
                    success = true,
                    result = resultStr,
                    result_type = returnValue?.GetType().FullName
                };
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Debug.LogWarning($"[EditorEval] Runtime error: {inner.Message}");
                return new
                {
                    success = false,
                    error = "Runtime exception",
                    exception_type = inner.GetType().FullName,
                    message = inner.Message,
                    stack_trace = inner.StackTrace
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EditorEval] Error: {ex.Message}");
                return new
                {
                    success = false,
                    error = ex.Message
                };
            }
        }

        private static string WrapInClass(string bodyCode)
        {
            return $@"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

public static class __EditorEvalTemp
{{
    public static object Execute()
    {{
        {bodyCode}
    }}
}}";
        }

        /// <summary>
        /// Simple check for whether the code contains a return statement.
        /// Looks for 'return' as a standalone keyword (not inside a string or comment).
        /// </summary>
        private static bool ContainsReturn(string code)
        {
            // Simple regex-free check: look for 'return' preceded by whitespace/start and followed by whitespace/semicolon
            int idx = 0;
            while (idx < code.Length)
            {
                int pos = code.IndexOf("return", idx, StringComparison.Ordinal);
                if (pos < 0) return false;

                // Check it's not part of a larger identifier
                bool startOk = pos == 0 || !char.IsLetterOrDigit(code[pos - 1]) && code[pos - 1] != '_';
                bool endOk = pos + 6 >= code.Length || !char.IsLetterOrDigit(code[pos + 6]) && code[pos + 6] != '_';

                if (startOk && endOk)
                    return true;

                idx = pos + 6;
            }
            return false;
        }

        private static string Stringify(object value)
        {
            if (value == null) return null;
            if (value is string s) return s;

            // Try JsonUtility for Unity objects
            if (value is UnityEngine.Object)
            {
                try
                {
                    string json = JsonUtility.ToJson(value, true);
                    if (!string.IsNullOrEmpty(json) && json != "{}")
                        return json;
                }
                catch { /* Fall through to ToString */ }
            }

            // Arrays and collections
            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                    items.Add(item?.ToString() ?? "null");
                return $"[{string.Join(", ", items)}]";
            }

            return value.ToString();
        }
    }
}
