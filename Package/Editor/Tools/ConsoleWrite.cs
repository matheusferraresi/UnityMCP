using System;
using System.Reflection;
using UnityEngine;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Writes messages to the Unity Console or clears it.
    /// Inspired by CoderGamester/mcp-unity's send_console_log tool.
    /// </summary>
    public static class ConsoleWrite
    {
        [MCPTool("console_write", "Write a message to the Unity Console or clear it (useful for debugging and isolating errors)",
            Category = "Console", DestructiveHint = true)]
        public static object Write(
            [MCPParam("action", "Action: write (default) or clear", Enum = new[] { "write", "clear" })] string action = "write",
            [MCPParam("message", "Message to write (required for 'write' action)")] string message = null,
            [MCPParam("type", "Log type: log, warning, error (default: log)", Enum = new[] { "log", "warning", "error" })] string type = "log")
        {
            if (action?.ToLower() == "clear")
                return ClearConsole();

            if (string.IsNullOrEmpty(message))
                throw MCPException.InvalidParams("'message' is required for 'write' action.");

            switch (type?.ToLower())
            {
                case "warning":
                    Debug.LogWarning($"[MCP] {message}");
                    break;
                case "error":
                    Debug.LogError($"[MCP] {message}");
                    break;
                default:
                    Debug.Log($"[MCP] {message}");
                    break;
            }

            return new
            {
                success = true,
                message = $"Message written to console as {type ?? "log"}.",
                type = type ?? "log"
            };
        }

        private static object ClearConsole()
        {
            var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType == null)
                throw MCPException.InternalError("Failed to access LogEntries type via reflection.");

            var clearMethod = logEntriesType.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
            if (clearMethod == null)
                throw MCPException.InternalError("Failed to find LogEntries.Clear method.");

            clearMethod.Invoke(null, null);

            return new
            {
                success = true,
                message = "Console cleared."
            };
        }
    }
}
