using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Writes messages to the Unity Console from the AI assistant.
    /// Inspired by CoderGamester/mcp-unity's send_console_log tool.
    /// </summary>
    public static class ConsoleWrite
    {
        [MCPTool("console_write", "Write a message to the Unity Console (useful for debugging and communication)",
            Category = "Console", DestructiveHint = true)]
        public static object Write(
            [MCPParam("message", "Message to write to the console", required: true)] string message,
            [MCPParam("type", "Log type: log, warning, error (default: log)", Enum = new[] { "log", "warning", "error" })] string type = "log")
        {
            if (string.IsNullOrEmpty(message))
                throw MCPException.InvalidParams("'message' is required.");

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
    }
}
