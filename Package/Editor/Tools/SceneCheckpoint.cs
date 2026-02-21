using System;
using System.Collections.Generic;
using System.Linq;
using UnityMCP.Editor;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Services;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// MCP tools for saving, restoring, and comparing scene checkpoints.
    /// </summary>
    public static class SceneCheckpoint
    {
        #region Scene Checkpoint (Save / List)

        /// <summary>
        /// Saves a new scene checkpoint or lists all existing checkpoints.
        /// </summary>
        [MCPTool("scene_checkpoint", "Save or list scene checkpoints for undo/restore capability", Category = "Scene", DestructiveHint = true)]
        public static object Checkpoint(
            [MCPParam("action", "Action: 'save' to create checkpoint, 'list' to view all checkpoints", required: true, Enum = new[] { "save", "list" })] string action,
            [MCPParam("name", "Optional name for the checkpoint (save only)")] string name = null)
        {
            string normalizedAction = (action ?? "").ToLowerInvariant().Trim();

            return normalizedAction switch
            {
                "save" => SaveCheckpoint(name),
                "list" => ListCheckpoints(),
                _ => throw MCPException.InvalidParams($"Unknown action: '{action}'. Valid actions: save, list")
            };
        }

        /// <summary>
        /// Saves the current scene state as a checkpoint.
        /// </summary>
        private static object SaveCheckpoint(string checkpointName)
        {
            try
            {
                CheckpointMetadata metadata = CheckpointManager.SaveCheckpoint(checkpointName);
                if (metadata == null)
                {
                    return new
                    {
                        success = false,
                        error = "Failed to save checkpoint. Ensure the scene is saved and has a valid path."
                    };
                }

                return new
                {
                    success = true,
                    message = $"Checkpoint '{metadata.name}' saved.",
                    checkpoint = metadata.ToSerializable()
                };
            }
            catch (Exception exception)
            {
                return new
                {
                    success = false,
                    error = $"Error saving checkpoint: {exception.Message}"
                };
            }
        }

        /// <summary>
        /// Lists all existing checkpoints sorted by timestamp descending.
        /// </summary>
        private static object ListCheckpoints()
        {
            try
            {
                List<CheckpointMetadata> checkpoints = CheckpointManager.ListCheckpoints();

                return new
                {
                    success = true,
                    count = checkpoints.Count,
                    checkpoints = checkpoints.Select(checkpoint => checkpoint.ToSerializable()).ToList()
                };
            }
            catch (Exception exception)
            {
                return new
                {
                    success = false,
                    error = $"Error listing checkpoints: {exception.Message}"
                };
            }
        }

        #endregion

        #region Scene Restore

        /// <summary>
        /// Restores a previously saved scene checkpoint.
        /// Automatically creates a "before restore" checkpoint before restoring.
        /// </summary>
        [MCPTool("scene_restore", "Restore a previously saved scene checkpoint", Category = "Scene", DestructiveHint = true)]
        public static object Restore(
            [MCPParam("checkpoint_id", "ID of the checkpoint to restore", required: true)] string checkpointId)
        {
            if (string.IsNullOrWhiteSpace(checkpointId))
            {
                throw MCPException.InvalidParams("checkpoint_id is required.");
            }

            try
            {
                // Get the current scene state for diff comparison
                string beforeSnapshotId = null;
                CheckpointMetadata beforeMetadata = CheckpointManager.SaveCheckpoint("Before restore (auto)");
                if (beforeMetadata != null)
                {
                    beforeSnapshotId = beforeMetadata.id;
                }

                // Restore the requested checkpoint
                CheckpointMetadata restoredMetadata = CheckpointManager.RestoreCheckpoint(checkpointId);
                if (restoredMetadata == null)
                {
                    return new
                    {
                        success = false,
                        error = $"Failed to restore checkpoint '{checkpointId}'. Checkpoint may not exist or scene file may be missing."
                    };
                }

                // Compute diff showing what changed
                object diffResult = null;
                if (beforeSnapshotId != null)
                {
                    CheckpointDiff diff = CheckpointManager.GetDiff(beforeSnapshotId, checkpointId);
                    if (diff != null)
                    {
                        diffResult = diff.ToSerializable();
                    }
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "message", $"Restored checkpoint '{restoredMetadata.name}'." },
                    { "restored", restoredMetadata.ToSerializable() }
                };

                if (beforeSnapshotId != null)
                {
                    result["before_restore_id"] = beforeSnapshotId;
                }

                if (diffResult != null)
                {
                    result["diff"] = diffResult;
                }

                return result;
            }
            catch (Exception exception)
            {
                return new
                {
                    success = false,
                    error = $"Error restoring checkpoint: {exception.Message}"
                };
            }
        }

        #endregion

        #region Scene Diff

        /// <summary>
        /// Compares two checkpoints or the current scene against a checkpoint.
        /// Reports added and removed root objects and count changes.
        /// </summary>
        [MCPTool("scene_diff", "Compare two checkpoints or current scene vs a checkpoint", Category = "Scene", ReadOnlyHint = true)]
        public static object Diff(
            [MCPParam("checkpoint_a", "First checkpoint ID (or 'current' for active scene)", required: true)] string checkpointA,
            [MCPParam("checkpoint_b", "Second checkpoint ID (or 'current' for active scene)")] string checkpointB = "current")
        {
            if (string.IsNullOrWhiteSpace(checkpointA))
            {
                throw MCPException.InvalidParams("checkpoint_a is required.");
            }

            try
            {
                CheckpointDiff diff = CheckpointManager.GetDiff(checkpointA, checkpointB);
                if (diff == null)
                {
                    return new
                    {
                        success = false,
                        error = "Failed to compute diff. One or both checkpoint IDs may be invalid."
                    };
                }

                return new
                {
                    success = true,
                    checkpoint_a = checkpointA,
                    checkpoint_b = checkpointB ?? "current",
                    diff = diff.ToSerializable()
                };
            }
            catch (Exception exception)
            {
                return new
                {
                    success = false,
                    error = $"Error computing diff: {exception.Message}"
                };
            }
        }

        #endregion
    }
}
