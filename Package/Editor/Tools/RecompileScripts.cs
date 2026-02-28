using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnixxtyMCP.Editor.Core;
using UnixxtyMCP.Editor.Services;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Forces script recompilation and returns compilation logs.
    /// Inspired by CoderGamester/mcp-unity's recompile_scripts tool.
    /// </summary>
    public static class RecompileScripts
    {
        [MCPTool("recompile_scripts",
            "Force Unity to recompile all scripts and return compilation results. " +
            "NOTE: Compilation is async — returned logs may be from a previous compilation. " +
            "For reliable compilation tracking with structured errors, use compile_and_watch instead.",
            Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("return_logs", "Include compilation-related console logs in response (default: true)")] bool returnLogs = true,
            [MCPParam("log_limit", "Maximum compilation logs to return (default: 100)", Minimum = 0, Maximum = 1000)] int logLimit = 100)
        {
            // Check if already compiling
            if (EditorApplication.isCompiling)
            {
                return new
                {
                    success = true,
                    message = "Unity is already compiling. Check compilation status with console_read.",
                    state = "already_compiling"
                };
            }

            // Check if tests are running
            if (TestJobManager.IsRunning)
            {
                return new
                {
                    success = false,
                    error = "Cannot recompile while tests are running."
                };
            }

            try
            {
                // Get pre-compilation error count for comparison
                int preErrorCount = GetCompilationErrorCount();

                // Request compilation
                CompilationPipeline.RequestScriptCompilation();

                // Collect compilation logs if requested
                List<object> logs = null;
                if (returnLogs)
                {
                    logs = GetRecentCompilationLogs(logLimit);
                }

                return new
                {
                    success = true,
                    message = "Script recompilation requested. Unity will recompile on next editor update.",
                    state = "compilation_requested",
                    preCompilationErrors = preErrorCount,
                    logs,
                    hint = "Poll editor://state resource or use console_read with types='error' to check for compilation errors."
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    error = $"Failed to request recompilation: {ex.Message}"
                };
            }
        }

        private static int GetCompilationErrorCount()
        {
            try
            {
                var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType == null) return -1;

                var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public);
                if (getCountMethod == null) return -1;

                return (int)getCountMethod.Invoke(null, null);
            }
            catch
            {
                return -1;
            }
        }

        private static List<object> GetRecentCompilationLogs(int limit)
        {
            var logs = new List<object>();

            try
            {
                var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntriesType == null || logEntryType == null) return logs;

                var startMethod = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var endMethod = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public);
                var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);

                if (startMethod == null || endMethod == null || getCountMethod == null || getEntryMethod == null) return logs;

                var modeField = logEntryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public);
                var messageField = logEntryType.GetField("message", BindingFlags.Instance | BindingFlags.Public);

                startMethod.Invoke(null, null);
                try
                {
                    int count = (int)getCountMethod.Invoke(null, null);
                    var logEntry = Activator.CreateInstance(logEntryType);

                    // Read from the end (most recent) looking for compilation-related logs
                    int startIdx = Math.Max(0, count - limit);
                    for (int i = startIdx; i < count && logs.Count < limit; i++)
                    {
                        getEntryMethod.Invoke(null, new object[] { i, logEntry });
                        int mode = (int)modeField.GetValue(logEntry);
                        string message = (string)messageField.GetValue(logEntry);

                        // Filter for compilation-related entries (compile errors/warnings)
                        bool isCompileError = (mode & (1 << 11)) != 0;   // ScriptCompileError
                        bool isCompileWarning = (mode & (1 << 12)) != 0; // ScriptCompileWarning

                        if (isCompileError || isCompileWarning)
                        {
                            string firstLine = message;
                            int nlIdx = message?.IndexOf('\n') ?? -1;
                            if (nlIdx >= 0) firstLine = message.Substring(0, nlIdx);

                            logs.Add(new
                            {
                                type = isCompileError ? "error" : "warning",
                                message = firstLine?.Length > 500 ? firstLine.Substring(0, 500) + "..." : firstLine
                            });
                        }
                    }
                }
                finally
                {
                    endMethod.Invoke(null, null);
                }
            }
            catch
            {
                // Swallow reflection errors
            }

            return logs;
        }
    }
}
