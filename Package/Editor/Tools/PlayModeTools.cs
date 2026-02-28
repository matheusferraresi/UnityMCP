using System;
using UnityEditor;
using UnityEngine;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Tools for controlling Unity Editor play mode state.
    /// </summary>
    public static class PlayModeTools
    {
        /// <summary>
        /// Enters play mode.
        /// </summary>
        /// <returns>Result object with current play mode state.</returns>
        [MCPTool("playmode_enter", "Enter play mode", Category = "Editor", DestructiveHint = true)]
        public static object Enter()
        {
            try
            {
                if (EditorApplication.isPlaying)
                {
                    return new
                    {
                        success = true,
                        message = "Already in play mode.",
                        isPlaying = true,
                        isPaused = EditorApplication.isPaused
                    };
                }

                if (EditorApplication.isCompiling)
                {
                    return new
                    {
                        success = false,
                        error = "Cannot enter play mode while scripts are compiling.",
                        isPlaying = false,
                        isPaused = false
                    };
                }

                if (EditorApplication.isUpdating)
                {
                    return new
                    {
                        success = false,
                        error = "Cannot enter play mode while assets are importing.",
                        isPlaying = false,
                        isPaused = false
                    };
                }

                EditorApplication.isPlaying = true;

                return new
                {
                    success = true,
                    message = "Entering play mode.",
                    isPlaying = true,
                    isPaused = false
                };
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[PlayModeTools] Error entering play mode: {exception.Message}");
                return new
                {
                    success = false,
                    error = $"Error entering play mode: {exception.Message}"
                };
            }
        }

        /// <summary>
        /// Exits play mode. Note: Unity triggers a domain reload after exiting play mode.
        /// During the reload (~1-3 seconds), subsequent MCP calls may fail.
        /// </summary>
        /// <returns>Result object with current play mode state and reload warning.</returns>
        [MCPTool("playmode_exit",
            "Exit play mode. IMPORTANT: Unity performs a domain reload after exiting play mode (1-3 seconds). " +
            "Wait at least 2 seconds before making the next MCP call, or poll editor://state until isCompiling is false.",
            Category = "Editor", DestructiveHint = true)]
        public static object Exit()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return new
                    {
                        success = true,
                        message = "Already stopped (not in play mode).",
                        isPlaying = false,
                        isPaused = false,
                        domain_reload_pending = false
                    };
                }

                EditorApplication.isPlaying = false;

                return new
                {
                    success = true,
                    message = "Exiting play mode. Domain reload will occur — wait 2+ seconds before next MCP call, or poll editor://state.",
                    isPlaying = false,
                    isPaused = false,
                    domain_reload_pending = true,
                    recommended_delay_ms = 2000
                };
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[PlayModeTools] Error exiting play mode: {exception.Message}");
                return new
                {
                    success = false,
                    error = $"Error exiting play mode: {exception.Message}"
                };
            }
        }

        /// <summary>
        /// Toggles or sets the pause state during play mode.
        /// </summary>
        /// <param name="paused">Optional explicit pause state. If omitted, toggles current state.</param>
        /// <returns>Result object with current play mode state.</returns>
        [MCPTool("playmode_pause", "Toggle or set pause state", Category = "Editor", DestructiveHint = true)]
        public static object Pause(
            [MCPParam("paused", "Set pause state (true/false), omit to toggle")] bool? paused = null)
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return new
                    {
                        success = false,
                        error = "Cannot pause when not in play mode. Use playmode_enter first.",
                        isPlaying = false,
                        isPaused = false
                    };
                }

                bool newPauseState;
                string actionDescription;

                if (paused.HasValue)
                {
                    newPauseState = paused.Value;
                    if (EditorApplication.isPaused == newPauseState)
                    {
                        actionDescription = newPauseState ? "Already paused." : "Already running.";
                    }
                    else
                    {
                        EditorApplication.isPaused = newPauseState;
                        actionDescription = newPauseState ? "Play mode paused." : "Play mode resumed.";
                    }
                }
                else
                {
                    newPauseState = !EditorApplication.isPaused;
                    EditorApplication.isPaused = newPauseState;
                    actionDescription = newPauseState ? "Play mode paused." : "Play mode resumed.";
                }

                return new
                {
                    success = true,
                    message = actionDescription,
                    isPlaying = true,
                    isPaused = newPauseState
                };
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[PlayModeTools] Error toggling pause: {exception.Message}");
                return new
                {
                    success = false,
                    error = $"Error toggling pause: {exception.Message}"
                };
            }
        }

        /// <summary>
        /// Advances a single frame while in play mode (paused).
        /// </summary>
        /// <returns>Result object with current play mode state.</returns>
        [MCPTool("playmode_step", "Advance single frame", Category = "Editor", DestructiveHint = true)]
        public static object Step()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    return new
                    {
                        success = false,
                        error = "Cannot step when not in play mode. Use playmode_enter first.",
                        isPlaying = false,
                        isPaused = false
                    };
                }

                // Step advances one frame and pauses if not already paused
                EditorApplication.Step();

                return new
                {
                    success = true,
                    message = "Advanced one frame.",
                    isPlaying = true,
                    isPaused = true // Step always results in paused state
                };
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[PlayModeTools] Error stepping frame: {exception.Message}");
                return new
                {
                    success = false,
                    error = $"Error stepping frame: {exception.Message}"
                };
            }
        }
    }
}
