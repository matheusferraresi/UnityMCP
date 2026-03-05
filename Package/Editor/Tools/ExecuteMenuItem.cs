using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Tool for executing Unity Editor menu items by path.
    /// Includes a safety blacklist to prevent dangerous operations.
    /// </summary>
    public static class ExecuteMenuItem
    {
        /// <summary>
        /// Blacklist of menu paths that are not allowed to be executed for safety reasons.
        /// Uses case-insensitive comparison.
        /// </summary>
        private static readonly HashSet<string> MenuPathBlacklist = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // Application exit
            "File/Quit",
            "File/Exit",

            // Build operations that could be disruptive
            "File/Build And Run",

            // Dangerous project operations
            "Assets/Delete",

            // Editor preferences that could break things
            "Edit/Preferences...",
            "Edit/Project Settings...",

            // Package Manager operations
            "Window/Package Manager"
        };

        /// <summary>
        /// Executes a Unity Editor menu item by its path.
        /// </summary>
        /// <param name="menuPath">The menu item path (e.g., "File/Save", "GameObject/Create Empty").</param>
        /// <returns>Result object indicating success or failure with appropriate message.</returns>
        [MCPTool("execute_menu_item", "Execute a Unity Editor menu item by path", Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("menu_path", "The menu item path (e.g., 'File/Save', 'GameObject/Create Empty')", required: true)] string menuPath)
        {
            // Validate menu_path is provided
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return new
                {
                    success = false,
                    error = "The 'menu_path' parameter is required and cannot be empty."
                };
            }

            // Normalize the menu path (trim whitespace)
            string normalizedMenuPath = menuPath.Trim();

            // Check if the menu path is blacklisted
            if (IsMenuPathBlacklisted(normalizedMenuPath))
            {
                return new
                {
                    success = false,
                    error = $"The menu item '{normalizedMenuPath}' is blacklisted for safety reasons and cannot be executed.",
                    blacklisted = true
                };
            }

            // Check if Unity is still compiling — menu items may not be registered yet
            if (EditorApplication.isCompiling)
            {
                return new
                {
                    success = false,
                    error = "Unity is still compiling scripts. Wait for compilation to finish and retry.",
                    is_compiling = true,
                    menu_path = normalizedMenuPath
                };
            }

            // Pre-checks: existence and validation
            bool? menuExists = CheckMenuItemExists(normalizedMenuPath);
            bool menuEnabled = EditorApplication.ValidateMenuItem(normalizedMenuPath);

            // If we know for certain the menu doesn't exist, fail fast with diagnostics
            if (menuExists == false)
            {
                return new
                {
                    success = false,
                    error = $"Menu item not found: '{normalizedMenuPath}'.",
                    menu_path = normalizedMenuPath,
                    diagnostics = new { found = false, enabled = false }
                };
            }

            // If it exists but is disabled, report that clearly
            if (menuExists == true && !menuEnabled)
            {
                return new
                {
                    success = false,
                    error = $"Menu item '{normalizedMenuPath}' exists but is disabled. " +
                            "Its [MenuItem] validation function returned false — check required conditions (selection, scene state, etc.).",
                    menu_path = normalizedMenuPath,
                    diagnostics = new { found = true, enabled = false }
                };
            }

            // Execute with log capture to catch exceptions thrown inside the menu item
            string capturedError = null;
            string capturedStack = null;

            void LogHandler(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Exception || type == LogType.Error)
                {
                    capturedError ??= message;
                    capturedStack ??= stackTrace;
                }
            }

            Application.logMessageReceived += LogHandler;
            try
            {
                bool executed = EditorApplication.ExecuteMenuItem(normalizedMenuPath);

                if (executed)
                {
                    // Success — but check if errors were logged during execution
                    if (capturedError != null)
                    {
                        return new
                        {
                            success = true,
                            message = $"Menu item '{normalizedMenuPath}' executed but produced errors.",
                            menu_path = normalizedMenuPath,
                            warnings = new { exception = capturedError, stack_trace = capturedStack }
                        };
                    }

                    return new
                    {
                        success = true,
                        message = $"Successfully executed menu item: '{normalizedMenuPath}'",
                        menu_path = normalizedMenuPath
                    };
                }
                else
                {
                    // Execution returned false — provide diagnostics
                    return new
                    {
                        success = false,
                        error = capturedError != null
                            ? $"Menu item '{normalizedMenuPath}' threw an exception during execution."
                            : $"Menu item '{normalizedMenuPath}' returned false. It may require specific editor state (e.g., a selection, a scene open, play mode).",
                        menu_path = normalizedMenuPath,
                        diagnostics = new
                        {
                            found = menuExists ?? (bool?)null,
                            enabled = menuEnabled,
                            exception = capturedError,
                            stack_trace = capturedStack
                        }
                    };
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ExecuteMenuItem] Error executing menu item '{normalizedMenuPath}': {exception.Message}");
                return new
                {
                    success = false,
                    error = $"Menu item '{normalizedMenuPath}' threw an unhandled exception: {exception.Message}",
                    menu_path = normalizedMenuPath,
                    diagnostics = new
                    {
                        found = menuExists ?? (bool?)null,
                        enabled = menuEnabled,
                        exception = exception.Message,
                        stack_trace = exception.StackTrace
                    }
                };
            }
            finally
            {
                Application.logMessageReceived -= LogHandler;
            }
        }

        /// <summary>
        /// Checks if a menu item exists using Unity's internal Menu class via reflection.
        /// Returns null if the check is not available (reflection failed).
        /// </summary>
        private static bool? CheckMenuItemExists(string menuPath)
        {
            try
            {
                var menuType = typeof(EditorApplication).Assembly.GetType("UnityEditor.Menu");
                if (menuType == null) return null;

                // Unity 6 has Menu.MenuItemExists(string, bool)
                var existsMethod = menuType.GetMethod("MenuItemExists",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (existsMethod != null)
                {
                    var parameters = existsMethod.GetParameters();
                    if (parameters.Length == 1)
                        return (bool)existsMethod.Invoke(null, new object[] { menuPath });
                    if (parameters.Length == 2)
                        return (bool)existsMethod.Invoke(null, new object[] { menuPath, false });
                }

                // Fallback: try GetMenuItems
                var getItemsMethod = menuType.GetMethod("GetMenuItems",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (getItemsMethod != null)
                {
                    var result = getItemsMethod.Invoke(null, new object[] { menuPath, false, false });
                    if (result is System.Array arr)
                        return arr.Length > 0;
                }
            }
            catch
            {
                // Reflection failed — can't determine
            }

            return null;
        }

        /// <summary>
        /// Checks if a menu path is blacklisted.
        /// Performs case-insensitive matching and also checks for partial matches
        /// to catch variations of blacklisted paths.
        /// </summary>
        /// <param name="menuPath">The menu path to check.</param>
        /// <returns>True if the menu path is blacklisted, false otherwise.</returns>
        private static bool IsMenuPathBlacklisted(string menuPath)
        {
            // Direct match (case-insensitive due to HashSet comparer)
            if (MenuPathBlacklist.Contains(menuPath))
            {
                return true;
            }

            // Check for paths that start with blacklisted prefixes
            // This catches variations like "File/Quit " or "File/Quit..."
            foreach (string blacklistedPath in MenuPathBlacklist)
            {
                if (menuPath.StartsWith(blacklistedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
