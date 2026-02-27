using System;
using System.Collections.Generic;
using UnityEditor;
using UnityMCP.Editor;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Exposes Unity's Undo/Redo system for AI-driven scene editing.
    /// Allows agents to undo mistakes, redo operations, and inspect undo history.
    /// </summary>
    public static class UndoRedo
    {
        [MCPTool("undo_redo", "Control Unity's undo/redo system: undo, redo, get history, or collapse undo groups", Category = "Editor")]
        public static object Execute(
            [MCPParam("action", "Action: undo, redo, get_history, collapse_last", required: true,
                Enum = new[] { "undo", "redo", "get_history", "collapse_last" })] string action,
            [MCPParam("count", "Number of undo/redo operations to perform (default: 1)")] int count = 1,
            [MCPParam("group_name", "Name for collapse_last action (groups recent operations under one undo step)")] string groupName = null)
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "undo" => PerformUndo(count),
                    "redo" => PerformRedo(count),
                    "get_history" => GetHistory(),
                    "collapse_last" => CollapseLast(groupName),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'. Valid: undo, redo, get_history, collapse_last")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"Undo/Redo operation failed: {ex.Message}");
            }
        }

        private static object PerformUndo(int count)
        {
            count = Math.Max(1, Math.Min(count, 50));
            var undone = new List<string>();

            for (int i = 0; i < count; i++)
            {
                string currentGroup = Undo.GetCurrentGroupName();
                if (string.IsNullOrEmpty(currentGroup) && i > 0) break;

                Undo.PerformUndo();
                undone.Add(string.IsNullOrEmpty(currentGroup) ? "(unnamed)" : currentGroup);
            }

            return new
            {
                success = true,
                undoneCount = undone.Count,
                operations = undone,
                currentGroup = Undo.GetCurrentGroupName()
            };
        }

        private static object PerformRedo(int count)
        {
            count = Math.Max(1, Math.Min(count, 50));
            var redone = new List<string>();

            for (int i = 0; i < count; i++)
            {
                Undo.PerformRedo();
                string currentGroup = Undo.GetCurrentGroupName();
                redone.Add(string.IsNullOrEmpty(currentGroup) ? "(unnamed)" : currentGroup);
            }

            return new
            {
                success = true,
                redoneCount = redone.Count,
                operations = redone,
                currentGroup = Undo.GetCurrentGroupName()
            };
        }

        private static object GetHistory()
        {
            string currentGroup = Undo.GetCurrentGroupName();

            return new
            {
                success = true,
                currentGroup = string.IsNullOrEmpty(currentGroup) ? "(none)" : currentGroup,
                currentGroupId = Undo.GetCurrentGroup(),
                message = "Use 'undo' or 'redo' actions to navigate the undo history."
            };
        }

        private static object CollapseLast(string groupName)
        {
            int groupId = Undo.GetCurrentGroup();
            Undo.CollapseUndoOperations(groupId);

            if (!string.IsNullOrEmpty(groupName))
                Undo.SetCurrentGroupName(groupName);

            return new
            {
                success = true,
                groupId,
                groupName = groupName ?? Undo.GetCurrentGroupName(),
                message = $"Collapsed recent operations into group '{groupName ?? "(current)"}'"
            };
        }
    }
}
