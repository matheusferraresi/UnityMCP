#if UNITY_MCP_FEEL
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// MCP tool for managing Feel/MMFeedbacks: create players, add feedbacks, configure, play/stop.
    /// All Feel access via reflection — no direct type references.
    /// </summary>
    public static class FeelTools
    {
        #region Cached Types

        private static Type _mmfPlayerType;
        private static Type _mmfFeedbackType;
        private static Type _mmfFeedbacksType;

        private static Type MMFPlayerType => _mmfPlayerType ??= FindType("MoreMountains.Feedbacks.MMF_Player");
        private static Type MMFFeedbackBaseType => _mmfFeedbackType ??= FindType("MoreMountains.Feedbacks.MMF_Feedback");
        private static Type MMFFeedbacksType => _mmfFeedbacksType ??= FindType("MoreMountains.Feedbacks.MMF_Feedbacks");

        #endregion

        #region Main Tool Entry Point

        [MCPTool("feel_manage", "Manages Feel/MMFeedbacks: create players, add/remove/configure feedbacks, play/stop",
            Category = "VFX", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "create_player", "add_feedback", "remove_feedback", "list_players",
                               "inspect", "play", "stop", "configure", "list_types",
                               "duplicate" })] string action,
            [MCPParam("target", "GameObject name/path or instance ID")] string target = null,
            [MCPParam("player_index", "Which MMF_Player if multiple on one object (default 0)")] int playerIndex = 0,
            [MCPParam("feedback_type", "Feedback type name, e.g. MMF_CameraShake, MMF_Flash")] string feedbackType = null,
            [MCPParam("feedback_index", "Index in the feedback list")] int feedbackIndex = -1,
            [MCPParam("feedback_label", "Custom label for identification")] string feedbackLabel = null,
            [MCPParam("properties", "JSON dict of property names to values for configure")] string properties = null,
            [MCPParam("intensity", "Playback intensity multiplier 0-1")] float intensity = 1f,
            [MCPParam("search_query", "Search filter for list_types")] string searchQuery = null,
            [MCPParam("destination", "Destination GameObject for duplicate")] string destination = null)
        {
            if (string.IsNullOrEmpty(action))
                throw MCPException.InvalidParams("Action parameter is required.");

            EnsureFeelAvailable();

            try
            {
                return action.ToLowerInvariant() switch
                {
                    "create_player" => HandleCreatePlayer(target),
                    "add_feedback" => HandleAddFeedback(target, playerIndex, feedbackType, feedbackLabel),
                    "remove_feedback" => HandleRemoveFeedback(target, playerIndex, feedbackIndex, feedbackLabel),
                    "list_players" => HandleListPlayers(),
                    "inspect" => HandleInspect(target, playerIndex),
                    "play" => HandlePlay(target, playerIndex, intensity),
                    "stop" => HandleStop(target, playerIndex),
                    "configure" => HandleConfigure(target, playerIndex, feedbackIndex, feedbackLabel, properties),
                    "list_types" => HandleListTypes(searchQuery),
                    "duplicate" => HandleDuplicate(target, playerIndex, destination),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"Feel operation failed: {ex.Message}");
            }
        }

        #endregion

        #region Action Handlers

        private static object HandleCreatePlayer(string target)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for create_player.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null) throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            // Use MMF_Player (new) or MMF_Feedbacks (legacy) — prefer MMF_Player
            var playerType = MMFPlayerType ?? MMFFeedbacksType;
            if (playerType == null)
                throw new MCPException("Cannot find MMF_Player or MMF_Feedbacks type.");

            var comp = go.AddComponent(playerType);
            Undo.RegisterCreatedObjectUndo(comp, "Add MMF_Player");
            EditorUtility.SetDirty(go);

            return new
            {
                success = true,
                message = $"Added {playerType.Name} to '{go.name}'",
                gameObject = go.name,
                instanceId = go.GetInstanceID()
            };
        }

        private static object HandleAddFeedback(string target, int playerIndex, string feedbackType, string feedbackLabel)
        {
            if (string.IsNullOrEmpty(feedbackType))
                throw MCPException.InvalidParams("'feedback_type' is required for add_feedback.");

            var (player, go) = ResolvePlayer(target, playerIndex);

            // Find the feedback type
            Type fbType = FindFeedbackType(feedbackType);
            if (fbType == null)
                throw MCPException.InvalidParams($"Feedback type '{feedbackType}' not found. Use list_types to see available types.");

            // Add feedback via MMF_Player.AddFeedback(type) or reflection
            var addMethod = player.GetType().GetMethod("AddFeedback", new[] { typeof(Type) });
            object newFeedback;
            if (addMethod != null)
            {
                newFeedback = addMethod.Invoke(player, new object[] { fbType });
            }
            else
            {
                // Fallback: create instance and add to FeedbacksList
                newFeedback = Activator.CreateInstance(fbType);
                var listProp = GetFeedbacksList(player);
                if (listProp is System.Collections.IList list)
                {
                    list.Add(newFeedback);
                }
                else
                {
                    throw new MCPException("Cannot add feedback — FeedbacksList not accessible.");
                }
            }

            // Set label if provided
            if (!string.IsNullOrEmpty(feedbackLabel) && newFeedback != null)
            {
                var labelField = newFeedback.GetType().GetField("Label", BindingFlags.Public | BindingFlags.Instance);
                labelField?.SetValue(newFeedback, feedbackLabel);
            }

            EditorUtility.SetDirty((UnityEngine.Object)player);

            return new
            {
                success = true,
                message = $"Added {fbType.Name} to player on '{go.name}'",
                gameObject = go.name,
                feedbackType = fbType.Name,
                label = feedbackLabel ?? ""
            };
        }

        private static object HandleRemoveFeedback(string target, int playerIndex, int feedbackIndex, string feedbackLabel)
        {
            var (player, go) = ResolvePlayer(target, playerIndex);
            var list = GetFeedbacksList(player) as System.Collections.IList;
            if (list == null) throw new MCPException("Cannot access feedbacks list.");

            int removeIdx = feedbackIndex;
            if (removeIdx < 0 && !string.IsNullOrEmpty(feedbackLabel))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var label = list[i]?.GetType().GetField("Label", BindingFlags.Public | BindingFlags.Instance)?.GetValue(list[i]) as string;
                    if (label == feedbackLabel) { removeIdx = i; break; }
                }
            }

            if (removeIdx < 0 || removeIdx >= list.Count)
                throw MCPException.InvalidParams($"Feedback index {removeIdx} out of range (0-{list.Count - 1}). Provide feedback_index or feedback_label.");

            var removedType = list[removeIdx]?.GetType().Name ?? "unknown";
            list.RemoveAt(removeIdx);
            EditorUtility.SetDirty((UnityEngine.Object)player);

            return new { success = true, message = $"Removed feedback at index {removeIdx} ({removedType})", gameObject = go.name };
        }

        private static object HandleListPlayers()
        {
            var playerType = MMFPlayerType ?? MMFFeedbacksType;
            if (playerType == null) return new { success = true, count = 0, players = new List<object>() };

            var allPlayers = UnityEngine.Object.FindObjectsByType(playerType, FindObjectsSortMode.None);
            var results = new List<object>();

            foreach (Component p in allPlayers)
            {
                var list = GetFeedbacksList(p) as System.Collections.IList;
                results.Add(new
                {
                    gameObject = p.gameObject.name,
                    instanceId = p.gameObject.GetInstanceID(),
                    feedbackCount = list?.Count ?? 0
                });
            }

            return new { success = true, count = results.Count, players = results };
        }

        private static object HandleInspect(string target, int playerIndex)
        {
            var (player, go) = ResolvePlayer(target, playerIndex);
            var list = GetFeedbacksList(player) as System.Collections.IList;

            var feedbacks = new List<object>();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var fb = list[i];
                    if (fb == null) continue;

                    string label = fb.GetType().GetField("Label", BindingFlags.Public | BindingFlags.Instance)?.GetValue(fb) as string ?? "";
                    bool active = true;
                    try
                    {
                        var activeProp = fb.GetType().GetField("Active", BindingFlags.Public | BindingFlags.Instance);
                        if (activeProp != null) active = (bool)activeProp.GetValue(fb);
                    }
                    catch { }

                    // Get timing info
                    float delay = 0;
                    try
                    {
                        var timingField = fb.GetType().GetField("Timing", BindingFlags.Public | BindingFlags.Instance);
                        if (timingField != null)
                        {
                            var timing = timingField.GetValue(fb);
                            var delayField = timing?.GetType().GetField("InitialDelay", BindingFlags.Public | BindingFlags.Instance);
                            if (delayField != null) delay = (float)delayField.GetValue(timing);
                        }
                    }
                    catch { }

                    feedbacks.Add(new
                    {
                        index = i,
                        type = fb.GetType().Name,
                        label,
                        active,
                        initialDelay = delay
                    });
                }
            }

            return new
            {
                success = true,
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                feedbackCount = feedbacks.Count,
                feedbacks
            };
        }

        private static object HandlePlay(string target, int playerIndex, float intensity)
        {
            var (player, go) = ResolvePlayer(target, playerIndex);

            if (!Application.isPlaying)
                return new { success = false, message = "Play only works in Play Mode. Use debug_play to enter Play Mode first." };

            var playMethod = player.GetType().GetMethod("PlayFeedbacks", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                          ?? player.GetType().GetMethod("PlayFeedbacks", BindingFlags.Public | BindingFlags.Instance);

            if (playMethod != null)
            {
                var parameters = playMethod.GetParameters();
                if (parameters.Length == 0)
                    playMethod.Invoke(player, null);
                else if (parameters.Length >= 2)
                    playMethod.Invoke(player, new object[] { player.GetType().GetMethod("get_transform")?.Invoke(player, null), intensity });
                else
                    playMethod.Invoke(player, null);
            }
            else
            {
                throw new MCPException("Cannot find PlayFeedbacks method.");
            }

            return new { success = true, message = $"Playing feedbacks on '{go.name}'", intensity };
        }

        private static object HandleStop(string target, int playerIndex)
        {
            var (player, go) = ResolvePlayer(target, playerIndex);

            if (!Application.isPlaying)
                return new { success = false, message = "Stop only works in Play Mode." };

            var stopMethod = player.GetType().GetMethod("StopFeedbacks", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            stopMethod?.Invoke(player, null);

            return new { success = true, message = $"Stopped feedbacks on '{go.name}'" };
        }

        private static object HandleConfigure(string target, int playerIndex, int feedbackIndex, string feedbackLabel, string properties)
        {
            if (string.IsNullOrEmpty(properties))
                throw MCPException.InvalidParams("'properties' JSON dict is required for configure.");

            var (player, go) = ResolvePlayer(target, playerIndex);
            var list = GetFeedbacksList(player) as System.Collections.IList;
            if (list == null) throw new MCPException("Cannot access feedbacks list.");

            int idx = feedbackIndex;
            if (idx < 0 && !string.IsNullOrEmpty(feedbackLabel))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var label = list[i]?.GetType().GetField("Label", BindingFlags.Public | BindingFlags.Instance)?.GetValue(list[i]) as string;
                    if (label == feedbackLabel) { idx = i; break; }
                }
            }

            if (idx < 0 || idx >= list.Count)
                throw MCPException.InvalidParams($"Feedback not found. Provide feedback_index or feedback_label.");

            var feedback = list[idx];
            int setCount = SetPropertiesFromJson(feedback, properties);

            EditorUtility.SetDirty((UnityEngine.Object)player);

            return new
            {
                success = true,
                message = $"Configured {setCount} properties on {feedback.GetType().Name}",
                gameObject = go.name,
                feedbackType = feedback.GetType().Name
            };
        }

        private static object HandleListTypes(string searchQuery)
        {
            var baseType = MMFFeedbackBaseType;
            if (baseType == null)
                throw new MCPException("Cannot find MMF_Feedback base type.");

            var types = new List<object>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.IsAbstract || !baseType.IsAssignableFrom(type)) continue;

                        string name = type.Name;
                        string fullName = type.FullName;

                        if (!string.IsNullOrEmpty(searchQuery))
                        {
                            bool match = name.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0
                                      || fullName.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!match) continue;
                        }

                        // Get FeedbackPath attribute for category
                        string path = "";
                        var pathAttr = type.GetCustomAttributes(false)
                            .FirstOrDefault(a => a.GetType().Name == "FeedbackPathAttribute");
                        if (pathAttr != null)
                        {
                            path = pathAttr.GetType().GetProperty("Path")?.GetValue(pathAttr) as string ?? "";
                        }

                        // Get FeedbackHelp for description
                        string help = "";
                        var helpAttr = type.GetCustomAttributes(false)
                            .FirstOrDefault(a => a.GetType().Name == "FeedbackHelpAttribute");
                        if (helpAttr != null)
                        {
                            help = helpAttr.GetType().GetProperty("HelpText")?.GetValue(helpAttr) as string ?? "";
                        }

                        types.Add(new { name, fullName, path, help });
                    }
                }
                catch { }
            }

            var sorted = types.OrderBy(t => ((dynamic)t).name).ToList();
            int total = sorted.Count;
            if (total > 200) sorted = sorted.Take(200).ToList();

            return new
            {
                success = true,
                total,
                returned = sorted.Count,
                note = total > 200 ? "Showing first 200. Use search_query to filter." : null,
                feedbackTypes = sorted
            };
        }

        private static object HandleDuplicate(string target, int playerIndex, string destination)
        {
            if (string.IsNullOrEmpty(destination))
                throw MCPException.InvalidParams("'destination' is required for duplicate.");

            var (sourcePlayer, sourceGo) = ResolvePlayer(target, playerIndex);
            var destGo = GameObjectResolver.Resolve(destination);
            if (destGo == null)
                throw MCPException.InvalidParams($"Destination '{destination}' not found.");

            // Copy the component using UnityEditorInternal.ComponentUtility
            UnityEditorInternal.ComponentUtility.CopyComponent((Component)sourcePlayer);
            var destPlayer = destGo.AddComponent(sourcePlayer.GetType());
            UnityEditorInternal.ComponentUtility.PasteComponentValues((Component)destPlayer);

            EditorUtility.SetDirty(destGo);

            return new
            {
                success = true,
                message = $"Duplicated feedback player from '{sourceGo.name}' to '{destGo.name}'",
                source = sourceGo.name,
                destination = destGo.name
            };
        }

        #endregion

        #region Helpers

        private static void EnsureFeelAvailable()
        {
            if (MMFPlayerType == null && MMFFeedbacksType == null)
                throw new MCPException("Feel is not installed or not loaded. Ensure Feel is in the project and UNITY_MCP_FEEL define is set.");
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private static (Component player, GameObject go) ResolvePlayer(string target, int playerIndex)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            var playerType = MMFPlayerType ?? MMFFeedbacksType;
            var players = go.GetComponents(playerType);
            if (players.Length == 0)
                throw MCPException.InvalidParams($"No MMF_Player found on '{go.name}'.");

            if (playerIndex >= players.Length)
                throw MCPException.InvalidParams($"Player index {playerIndex} out of range (0-{players.Length - 1}).");

            return (players[playerIndex], go);
        }

        private static object GetFeedbacksList(object player)
        {
            // MMF_Player has FeedbacksList property (List<MMF_Feedback>)
            var prop = player.GetType().GetProperty("FeedbacksList", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) return prop.GetValue(player);

            // Fallback to field
            var field = player.GetType().GetField("FeedbacksList", BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(player);

            // Try Feedbacks (older API)
            var feedbacksField = player.GetType().GetField("Feedbacks", BindingFlags.Public | BindingFlags.Instance);
            return feedbacksField?.GetValue(player);
        }

        private static Type FindFeedbackType(string typeName)
        {
            var baseType = MMFFeedbackBaseType;
            if (baseType == null) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.IsAbstract || !baseType.IsAssignableFrom(type)) continue;
                        if (type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                            || type.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                            return type;
                    }
                }
                catch { }
            }
            return null;
        }

        private static int SetPropertiesFromJson(object feedback, string propertiesJson)
        {
            var trimmed = propertiesJson.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return 0;

            trimmed = trimmed.Substring(1, trimmed.Length - 2);
            int setCount = 0;

            var pairs = SplitJsonPairs(trimmed);
            foreach (var pair in pairs)
            {
                var colonIndex = pair.IndexOf(':');
                if (colonIndex < 0) continue;

                var key = pair.Substring(0, colonIndex).Trim().Trim('"');
                var val = pair.Substring(colonIndex + 1).Trim().Trim('"');

                var field = feedback.GetType().GetField(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    try
                    {
                        object converted = ConvertValue(val, field.FieldType);
                        if (converted != null) { field.SetValue(feedback, converted); setCount++; }
                    }
                    catch { }
                    continue;
                }

                var prop = feedback.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop?.CanWrite == true)
                {
                    try
                    {
                        object converted = ConvertValue(val, prop.PropertyType);
                        if (converted != null) { prop.SetValue(feedback, converted); setCount++; }
                    }
                    catch { }
                }
            }

            return setCount;
        }

        private static object ConvertValue(string val, Type targetType)
        {
            if (targetType == typeof(float)) return float.Parse(val);
            if (targetType == typeof(int)) return int.Parse(val);
            if (targetType == typeof(bool)) return bool.Parse(val);
            if (targetType == typeof(string)) return val;
            if (targetType == typeof(double)) return double.Parse(val);
            if (targetType == typeof(Color))
            {
                var parts = val.Trim('(', ')').Split(',');
                if (parts.Length >= 3)
                    return new Color(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()), float.Parse(parts[2].Trim()), parts.Length > 3 ? float.Parse(parts[3].Trim()) : 1f);
            }
            if (targetType == typeof(Vector3))
            {
                var parts = val.Trim('(', ')').Split(',');
                return new Vector3(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()), float.Parse(parts[2].Trim()));
            }
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, val, ignoreCase: true);
            }
            try { return Convert.ChangeType(val, targetType); } catch { return null; }
        }

        private static List<string> SplitJsonPairs(string json)
        {
            var pairs = new List<string>();
            int depth = 0;
            int start = 0;
            bool inString = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
                if (!inString)
                {
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        pairs.Add(json.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                }
            }
            if (start < json.Length)
                pairs.Add(json.Substring(start).Trim());

            return pairs;
        }

        #endregion
    }
}
#endif
