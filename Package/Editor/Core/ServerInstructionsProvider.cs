using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Provides custom server instructions that get sent to AI clients on MCP initialization.
    /// Instructions are loaded from Assets/UnityMCPInstructions.md or EditorPrefs fallback.
    /// </summary>
    public static class ServerInstructionsProvider
    {
        private const string InstructionsFilePath = "Assets/UnityMCPInstructions.md";
        private const string EditorPrefsKey = "UnityMCP_ServerInstructions";

        /// <summary>
        /// Get the current server instructions. Prioritizes file-based instructions,
        /// falls back to EditorPrefs-stored instructions.
        /// </summary>
        public static string GetInstructions()
        {
            // Priority 1: File-based instructions
            string fullPath = Path.Combine(Application.dataPath, "..", InstructionsFilePath);
            fullPath = Path.GetFullPath(fullPath);

            if (File.Exists(fullPath))
            {
                string content = File.ReadAllText(fullPath).Trim();
                if (!string.IsNullOrEmpty(content))
                    return content;
            }

            // Priority 2: EditorPrefs
            string prefs = EditorPrefs.GetString(EditorPrefsKey, "");
            if (!string.IsNullOrEmpty(prefs))
                return prefs;

            return null;
        }

        /// <summary>
        /// Set instructions via EditorPrefs (used by the MCP tool).
        /// </summary>
        public static void SetInstructions(string instructions)
        {
            EditorPrefs.SetString(EditorPrefsKey, instructions ?? "");
        }

        /// <summary>
        /// Clear EditorPrefs instructions.
        /// </summary>
        public static void ClearInstructions()
        {
            EditorPrefs.DeleteKey(EditorPrefsKey);
        }

        /// <summary>
        /// Get the file path used for file-based instructions.
        /// </summary>
        public static string GetInstructionsFilePath() => InstructionsFilePath;
    }
}
