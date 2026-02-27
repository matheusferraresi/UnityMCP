using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Manages custom server instructions sent to AI clients on MCP initialization.
    /// Instructions can be stored in a file (Assets/UnityMCPInstructions.md) or via EditorPrefs.
    /// </summary>
    public static class ServerInstructions
    {
        [MCPTool("server_instructions", "Manage custom instructions sent to AI on MCP connection. Get, set, or clear per-project AI instructions.", Category = "Configuration")]
        public static object Execute(
            [MCPParam("action", "Action: get, set, clear, set_file", required: true,
                Enum = new[] { "get", "set", "clear", "set_file" })] string action,
            [MCPParam("instructions", "Instructions text (for 'set' action)")] string instructions = null,
            [MCPParam("file_content", "Content to write to the instructions file (for 'set_file' action)")] string fileContent = null)
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "get" => GetInstructions(),
                    "set" => SetInstructions(instructions),
                    "clear" => ClearInstructions(),
                    "set_file" => SetFileInstructions(fileContent),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'. Valid: get, set, clear, set_file")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException(-32603, $"Server instructions operation failed: {ex.Message}");
            }
        }

        private static object GetInstructions()
        {
            string filePath = ServerInstructionsProvider.GetInstructionsFilePath();
            string fullPath = Path.Combine(Application.dataPath, "..", filePath);
            fullPath = Path.GetFullPath(fullPath);
            bool fileExists = File.Exists(fullPath);

            string current = ServerInstructionsProvider.GetInstructions();
            string editorPrefs = EditorPrefs.GetString("UnityMCP_ServerInstructions", "");

            return new
            {
                success = true,
                instructions = current,
                source = fileExists && !string.IsNullOrEmpty(File.ReadAllText(fullPath).Trim()) ? "file" : (!string.IsNullOrEmpty(editorPrefs) ? "editorprefs" : "none"),
                filePath,
                fileExists,
                hasEditorPrefs = !string.IsNullOrEmpty(editorPrefs),
                message = string.IsNullOrEmpty(current)
                    ? $"No instructions configured. Create '{filePath}' or use action 'set' to add instructions."
                    : $"Instructions loaded ({current.Length} chars)"
            };
        }

        private static object SetInstructions(string instructions)
        {
            if (string.IsNullOrEmpty(instructions))
                throw MCPException.InvalidParams("instructions parameter is required for 'set' action.");

            ServerInstructionsProvider.SetInstructions(instructions);

            return new
            {
                success = true,
                length = instructions.Length,
                source = "editorprefs",
                message = $"Instructions set ({instructions.Length} chars). Will be sent to AI on next MCP connection."
            };
        }

        private static object ClearInstructions()
        {
            ServerInstructionsProvider.ClearInstructions();

            // Check if file still provides instructions
            string remaining = ServerInstructionsProvider.GetInstructions();

            return new
            {
                success = true,
                message = "EditorPrefs instructions cleared." + (remaining != null ? $" File-based instructions still active ({remaining.Length} chars)." : ""),
                hasFileInstructions = remaining != null
            };
        }

        private static object SetFileInstructions(string fileContent)
        {
            if (string.IsNullOrEmpty(fileContent))
                throw MCPException.InvalidParams("file_content parameter is required for 'set_file' action.");

            string filePath = ServerInstructionsProvider.GetInstructionsFilePath();
            string fullPath = Path.Combine(Application.dataPath, "..", filePath);
            fullPath = Path.GetFullPath(fullPath);

            File.WriteAllText(fullPath, fileContent);
            AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceUpdate);

            return new
            {
                success = true,
                filePath,
                length = fileContent.Length,
                source = "file",
                message = $"Instructions file created/updated at '{filePath}' ({fileContent.Length} chars). Takes priority over EditorPrefs instructions."
            };
        }
    }
}
