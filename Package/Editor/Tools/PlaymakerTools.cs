#if UNITY_MCP_PLAYMAKER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// MCP tool for managing PlayMaker FSMs: create, inspect, send events, add states/transitions/actions.
    /// All PlayMaker access via reflection — no direct type references.
    /// </summary>
    public static class PlaymakerTools
    {
        #region Cached Types

        private static Type _playMakerFSMType;
        private static Type _fsmType;
        private static Type _fsmStateType;
        private static Type _fsmTransitionType;
        private static Type _fsmEventType;
        private static Type _fsmVariablesType;
        private static Type _fsmTemplateType;

        private static Type PlayMakerFSMType => _playMakerFSMType ??= FindType("PlayMakerFSM");
        private static Type FsmType => _fsmType ??= FindType("HutongGames.PlayMaker.Fsm");
        private static Type FsmStateType => _fsmStateType ??= FindType("HutongGames.PlayMaker.FsmState");
        private static Type FsmTransitionType => _fsmTransitionType ??= FindType("HutongGames.PlayMaker.FsmTransition");
        private static Type FsmEventType => _fsmEventType ??= FindType("HutongGames.PlayMaker.FsmEvent");
        private static Type FsmVariablesType => _fsmVariablesType ??= FindType("HutongGames.PlayMaker.FsmVariables");
        private static Type FsmTemplateType => _fsmTemplateType ??= FindType("FsmTemplate");

        #endregion

        #region Main Tool Entry Point

        [MCPTool("playmaker_manage", "Manages PlayMaker FSMs: add, inspect, send events, create states/transitions/actions",
            Category = "StateMachine", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "add_fsm", "list_fsms", "inspect", "get_variables", "set_variable",
                               "send_event", "list_states", "list_events", "list_global_events",
                               "create_from_template", "open_editor", "list_actions",
                               "add_state", "add_transition", "add_action" })] string action,
            [MCPParam("target", "GameObject name/path or instance ID")] string target = null,
            [MCPParam("fsm_name", "FSM name if multiple on one object (default: first)")] string fsmName = null,
            [MCPParam("state_name", "State name for state-specific operations")] string stateName = null,
            [MCPParam("event_name", "Event name for send_event or add_transition")] string eventName = null,
            [MCPParam("variable_name", "Variable name for get/set")] string variableName = null,
            [MCPParam("variable_type", "Variable type: float, int, bool, string, vector3, color, rect, gameobject, material, texture, object")] string variableType = null,
            [MCPParam("variable_value", "Value to set (JSON-compatible)")] string variableValue = null,
            [MCPParam("action_type", "Full action class name, e.g. HutongGames.PlayMaker.Actions.SetPosition")] string actionType = null,
            [MCPParam("action_params", "JSON dict of action parameter names to values")] string actionParams = null,
            [MCPParam("template_path", "Asset path to FsmTemplate")] string templatePath = null,
            [MCPParam("search_query", "Search filter for list_actions")] string searchQuery = null,
            [MCPParam("from_state", "Source state name for add_transition")] string fromState = null,
            [MCPParam("to_state", "Target state name for add_transition")] string toState = null)
        {
            if (string.IsNullOrEmpty(action))
                throw MCPException.InvalidParams("Action parameter is required.");

            EnsurePlayMakerAvailable();

            try
            {
                return action.ToLowerInvariant() switch
                {
                    "add_fsm" => HandleAddFsm(target, fsmName),
                    "list_fsms" => HandleListFsms(),
                    "inspect" => HandleInspect(target, fsmName),
                    "get_variables" => HandleGetVariables(target, fsmName),
                    "set_variable" => HandleSetVariable(target, fsmName, variableName, variableType, variableValue),
                    "send_event" => HandleSendEvent(target, fsmName, eventName),
                    "list_states" => HandleListStates(target, fsmName),
                    "list_events" => HandleListEvents(target, fsmName),
                    "list_global_events" => HandleListGlobalEvents(),
                    "create_from_template" => HandleCreateFromTemplate(target, templatePath, fsmName),
                    "open_editor" => HandleOpenEditor(target, fsmName),
                    "list_actions" => HandleListActions(searchQuery),
                    "add_state" => HandleAddState(target, fsmName, stateName),
                    "add_transition" => HandleAddTransition(target, fsmName, fromState, toState, eventName),
                    "add_action" => HandleAddAction(target, fsmName, stateName, actionType, actionParams),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"PlayMaker operation failed: {ex.Message}");
            }
        }

        #endregion

        #region Action Handlers

        private static object HandleAddFsm(string target, string fsmName)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for add_fsm.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null) throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            var comp = go.AddComponent(PlayMakerFSMType);
            if (!string.IsNullOrEmpty(fsmName))
            {
                var fsmProp = PlayMakerFSMType.GetProperty("FsmName");
                fsmProp?.SetValue(comp, fsmName);
            }

            Undo.RegisterCreatedObjectUndo(comp, "Add PlayMakerFSM");
            EditorUtility.SetDirty(go);

            return new
            {
                success = true,
                message = $"Added PlayMakerFSM to '{go.name}'",
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                fsmName = fsmName ?? "FSM"
            };
        }

        private static object HandleListFsms()
        {
            var allFsms = UnityEngine.Object.FindObjectsByType(PlayMakerFSMType, FindObjectsSortMode.None);
            var results = new List<object>();

            foreach (var fsmComp in allFsms)
            {
                var fsm = GetFsmFromComponent(fsmComp);
                var go = ((Component)fsmComp).gameObject;

                string activeStateName = "";
                try
                {
                    var activeState = FsmType.GetProperty("ActiveStateName")?.GetValue(fsm) as string;
                    activeStateName = activeState ?? "";
                }
                catch { }

                int stateCount = 0;
                try
                {
                    var states = FsmType.GetProperty("States")?.GetValue(fsm) as Array;
                    stateCount = states?.Length ?? 0;
                }
                catch { }

                string name = PlayMakerFSMType.GetProperty("FsmName")?.GetValue(fsmComp) as string ?? "FSM";

                results.Add(new
                {
                    gameObject = go.name,
                    instanceId = go.GetInstanceID(),
                    fsmName = name,
                    activeState = activeStateName,
                    stateCount
                });
            }

            return new { success = true, count = results.Count, fsms = results };
        }

        private static object HandleInspect(string target, string fsmName)
        {
            var (comp, fsm, go) = ResolveFsm(target, fsmName);

            // States
            var statesArr = FsmType.GetProperty("States")?.GetValue(fsm) as Array;
            var stateList = new List<object>();
            if (statesArr != null)
            {
                foreach (var state in statesArr)
                {
                    var sName = FsmStateType.GetProperty("Name")?.GetValue(state) as string;
                    var transitions = FsmStateType.GetProperty("Transitions")?.GetValue(state) as Array;
                    var actions = FsmStateType.GetProperty("Actions")?.GetValue(state) as Array;

                    var transitionList = new List<object>();
                    if (transitions != null)
                    {
                        foreach (var t in transitions)
                        {
                            var evtName = GetTransitionEventName(t);
                            var toStateName = FsmTransitionType.GetField("ToState")?.GetValue(t) as string;
                            transitionList.Add(new { eventName = evtName, toState = toStateName });
                        }
                    }

                    stateList.Add(new
                    {
                        name = sName,
                        actionCount = actions?.Length ?? 0,
                        actionTypes = GetActionTypeNames(actions),
                        transitions = transitionList
                    });
                }
            }

            // Variables
            var varsObj = FsmType.GetProperty("Variables")?.GetValue(fsm);
            var varsSummary = GetVariablesSummary(varsObj);

            string activeState = FsmType.GetProperty("ActiveStateName")?.GetValue(fsm) as string ?? "";
            string startState = "";
            try
            {
                var startStateObj = FsmType.GetProperty("StartState")?.GetValue(fsm) as string;
                startState = startStateObj ?? "";
            }
            catch { }

            return new
            {
                success = true,
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                fsmName = PlayMakerFSMType.GetProperty("FsmName")?.GetValue(comp) as string ?? "FSM",
                activeState,
                startState,
                stateCount = stateList.Count,
                states = stateList,
                variables = varsSummary
            };
        }

        private static object HandleGetVariables(string target, string fsmName)
        {
            var (comp, fsm, go) = ResolveFsm(target, fsmName);
            var varsObj = FsmType.GetProperty("Variables")?.GetValue(fsm);
            if (varsObj == null)
                return new { success = true, variables = new List<object>() };

            var allVars = GetAllVariables(varsObj);
            return new { success = true, gameObject = go.name, count = allVars.Count, variables = allVars };
        }

        private static object HandleSetVariable(string target, string fsmName, string variableName, string variableType, string variableValue)
        {
            if (string.IsNullOrEmpty(variableName))
                throw MCPException.InvalidParams("'variable_name' is required for set_variable.");
            if (string.IsNullOrEmpty(variableValue))
                throw MCPException.InvalidParams("'variable_value' is required for set_variable.");

            var (comp, fsm, go) = ResolveFsm(target, fsmName);
            var varsObj = FsmType.GetProperty("Variables")?.GetValue(fsm);
            if (varsObj == null)
                throw new MCPException("Cannot access FSM variables.");

            // Try to find existing variable first
            bool found = TrySetExistingVariable(varsObj, variableName, variableValue);
            if (!found)
                throw MCPException.InvalidParams($"Variable '{variableName}' not found in FSM. Available variables: {GetVariableNames(varsObj)}");

            EditorUtility.SetDirty((UnityEngine.Object)comp);
            return new { success = true, message = $"Set '{variableName}' = {variableValue}", gameObject = go.name };
        }

        private static object HandleSendEvent(string target, string fsmName, string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
                throw MCPException.InvalidParams("'event_name' is required for send_event.");

            var (comp, fsm, go) = ResolveFsm(target, fsmName);

            // FsmEvent.GetFsmEvent(name)
            var getEventMethod = FsmEventType.GetMethod("GetFsmEvent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (getEventMethod == null)
                throw new MCPException("Cannot find FsmEvent.GetFsmEvent method.");

            var fsmEvent = getEventMethod.Invoke(null, new object[] { eventName });

            // fsm.Event(fsmEvent)
            var eventMethod = FsmType.GetMethod("Event", BindingFlags.Public | BindingFlags.Instance, null, new[] { FsmEventType }, null);
            if (eventMethod == null)
            {
                // Try SendEvent alternative
                var sendMethod = PlayMakerFSMType.GetMethod("SendEvent", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (sendMethod != null)
                {
                    sendMethod.Invoke(comp, new object[] { eventName });
                    return new { success = true, message = $"Sent event '{eventName}' via SendEvent", gameObject = go.name };
                }
                throw new MCPException("Cannot find event send method on FSM.");
            }

            eventMethod.Invoke(fsm, new[] { fsmEvent });
            return new { success = true, message = $"Sent event '{eventName}'", gameObject = go.name };
        }

        private static object HandleListStates(string target, string fsmName)
        {
            var (comp, fsm, go) = ResolveFsm(target, fsmName);
            var statesArr = FsmType.GetProperty("States")?.GetValue(fsm) as Array;

            var stateList = new List<object>();
            if (statesArr != null)
            {
                foreach (var state in statesArr)
                {
                    var sName = FsmStateType.GetProperty("Name")?.GetValue(state) as string;
                    var actions = FsmStateType.GetProperty("Actions")?.GetValue(state) as Array;
                    stateList.Add(new
                    {
                        name = sName,
                        actionCount = actions?.Length ?? 0,
                        actionTypes = GetActionTypeNames(actions)
                    });
                }
            }

            return new { success = true, gameObject = go.name, count = stateList.Count, states = stateList };
        }

        private static object HandleListEvents(string target, string fsmName)
        {
            var (comp, fsm, go) = ResolveFsm(target, fsmName);

            // Get events from FSM
            var eventsArr = FsmType.GetProperty("Events")?.GetValue(fsm) as Array;
            var eventList = new List<object>();
            if (eventsArr != null)
            {
                foreach (var evt in eventsArr)
                {
                    var eName = FsmEventType.GetProperty("Name")?.GetValue(evt) as string;
                    var isGlobal = FsmEventType.GetProperty("IsGlobal")?.GetValue(evt);
                    eventList.Add(new { name = eName, isGlobal = isGlobal ?? false });
                }
            }

            return new { success = true, gameObject = go.name, count = eventList.Count, events = eventList };
        }

        private static object HandleListGlobalEvents()
        {
            // FsmEvent.GlobalEvents (static property)
            var globalEvents = FsmEventType.GetProperty("GlobalEvents", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Array;
            var list = new List<string>();
            if (globalEvents != null)
            {
                foreach (var evt in globalEvents)
                {
                    var name = FsmEventType.GetProperty("Name")?.GetValue(evt) as string;
                    if (!string.IsNullOrEmpty(name)) list.Add(name);
                }
            }

            return new { success = true, count = list.Count, globalEvents = list };
        }

        private static object HandleCreateFromTemplate(string target, string templatePath, string fsmName)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for create_from_template.");
            if (string.IsNullOrEmpty(templatePath))
                throw MCPException.InvalidParams("'template_path' is required for create_from_template.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null) throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            var template = AssetDatabase.LoadAssetAtPath(templatePath, FsmTemplateType);
            if (template == null)
                throw MCPException.InvalidParams($"FsmTemplate not found at '{templatePath}'.");

            // Add FSM component and set template
            var comp = go.AddComponent(PlayMakerFSMType);
            var fsmTemplateProp = PlayMakerFSMType.GetProperty("FsmTemplate");
            fsmTemplateProp?.SetValue(comp, template);

            if (!string.IsNullOrEmpty(fsmName))
            {
                PlayMakerFSMType.GetProperty("FsmName")?.SetValue(comp, fsmName);
            }

            Undo.RegisterCreatedObjectUndo(comp, "Create FSM from template");
            EditorUtility.SetDirty(go);

            return new { success = true, message = $"Applied template '{templatePath}' to '{go.name}'", gameObject = go.name };
        }

        private static object HandleOpenEditor(string target, string fsmName)
        {
            var (comp, fsm, go) = ResolveFsm(target, fsmName);

            // Try to open PlayMaker editor
            var editorWindowType = FindType("PlayMakerEditor.FsmEditorWindow")
                                ?? FindType("HutongGames.PlayMakerEditor.FsmEditorWindow");

            if (editorWindowType != null)
            {
                // Try OpenInEditor or similar
                var openMethod = editorWindowType.GetMethod("OpenWindow", BindingFlags.Public | BindingFlags.Static);
                if (openMethod != null)
                {
                    openMethod.Invoke(null, null);
                }
                else
                {
                    EditorWindow.GetWindow(editorWindowType);
                }

                // Select the object to focus the editor on it
                Selection.activeGameObject = go;
            }
            else
            {
                // Fallback: just select the object
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
            }

            return new { success = true, message = $"Opened editor for FSM on '{go.name}'", gameObject = go.name };
        }

        private static object HandleListActions(string searchQuery)
        {
            // Find all types that inherit from FsmStateAction
            var baseActionType = FindType("HutongGames.PlayMaker.FsmStateAction");
            if (baseActionType == null)
                throw new MCPException("Cannot find FsmStateAction base type.");

            var actionTypes = new List<object>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.IsAbstract || !baseActionType.IsAssignableFrom(type)) continue;

                        string fullName = type.FullName;
                        string shortName = type.Name;

                        // Apply search filter
                        if (!string.IsNullOrEmpty(searchQuery))
                        {
                            bool match = shortName.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0
                                      || fullName.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!match) continue;
                        }

                        // Get category from ActionCategory attribute if available
                        string category = "";
                        var categoryAttr = type.GetCustomAttributes(false)
                            .FirstOrDefault(a => a.GetType().Name == "ActionCategoryAttribute");
                        if (categoryAttr != null)
                        {
                            category = categoryAttr.GetType().GetProperty("Category")?.GetValue(categoryAttr) as string ?? "";
                        }

                        // Get tooltip from Tooltip attribute
                        string tooltip = "";
                        var tooltipAttr = type.GetCustomAttributes(false)
                            .FirstOrDefault(a => a.GetType().Name == "TooltipAttribute" || a.GetType().Name == "ActionTooltip");
                        if (tooltipAttr != null)
                        {
                            var tooltipProp = tooltipAttr.GetType().GetProperty("Text")
                                           ?? tooltipAttr.GetType().GetProperty("tooltip");
                            tooltip = tooltipProp?.GetValue(tooltipAttr) as string ?? "";
                        }

                        actionTypes.Add(new
                        {
                            name = shortName,
                            fullName,
                            category
                        });
                    }
                }
                catch { /* Skip assemblies that fail to load types */ }
            }

            // Sort by name and limit
            var sorted = actionTypes.OrderBy(a => ((dynamic)a).name).ToList();
            int total = sorted.Count;
            if (total > 200) sorted = sorted.Take(200).ToList();

            return new
            {
                success = true,
                total,
                returned = sorted.Count,
                note = total > 200 ? "Showing first 200 results. Use search_query to filter." : null,
                actions = sorted
            };
        }

        private static object HandleAddState(string target, string fsmName, string stateName)
        {
            if (string.IsNullOrEmpty(stateName))
                throw MCPException.InvalidParams("'state_name' is required for add_state.");

            var (comp, fsm, go) = ResolveFsm(target, fsmName);

            // Get current states array
            var statesProp = FsmType.GetProperty("States");
            var statesArr = statesProp?.GetValue(fsm) as Array;
            if (statesArr == null)
                throw new MCPException("Cannot access FSM states.");

            // Check for duplicate name
            foreach (var existing in statesArr)
            {
                var existingName = FsmStateType.GetProperty("Name")?.GetValue(existing) as string;
                if (existingName == stateName)
                    throw MCPException.InvalidParams($"State '{stateName}' already exists in FSM.");
            }

            // Create new FsmState
            var newState = Activator.CreateInstance(FsmStateType, new object[] { fsm });
            FsmStateType.GetProperty("Name")?.SetValue(newState, stateName);

            // Expand the states array
            var newStatesArr = Array.CreateInstance(FsmStateType, statesArr.Length + 1);
            Array.Copy(statesArr, newStatesArr, statesArr.Length);
            newStatesArr.SetValue(newState, statesArr.Length);
            statesProp.SetValue(fsm, newStatesArr);

            Undo.RecordObject((UnityEngine.Object)comp, "Add FSM State");
            EditorUtility.SetDirty((UnityEngine.Object)comp);

            return new
            {
                success = true,
                message = $"Added state '{stateName}' to FSM on '{go.name}'",
                gameObject = go.name,
                stateCount = newStatesArr.Length
            };
        }

        private static object HandleAddTransition(string target, string fsmName, string fromState, string toState, string eventName)
        {
            if (string.IsNullOrEmpty(fromState))
                throw MCPException.InvalidParams("'from_state' is required for add_transition.");
            if (string.IsNullOrEmpty(toState))
                throw MCPException.InvalidParams("'to_state' is required for add_transition.");
            if (string.IsNullOrEmpty(eventName))
                throw MCPException.InvalidParams("'event_name' is required for add_transition.");

            var (comp, fsm, go) = ResolveFsm(target, fsmName);

            // Find the source state
            var statesArr = FsmType.GetProperty("States")?.GetValue(fsm) as Array;
            object sourceState = null;
            bool targetStateExists = false;

            if (statesArr != null)
            {
                foreach (var state in statesArr)
                {
                    var sName = FsmStateType.GetProperty("Name")?.GetValue(state) as string;
                    if (sName == fromState) sourceState = state;
                    if (sName == toState) targetStateExists = true;
                }
            }

            if (sourceState == null)
                throw MCPException.InvalidParams($"Source state '{fromState}' not found.");
            if (!targetStateExists)
                throw MCPException.InvalidParams($"Target state '{toState}' not found.");

            // Get or create the FsmEvent
            var getEventMethod = FsmEventType.GetMethod("GetFsmEvent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            var fsmEvent = getEventMethod?.Invoke(null, new object[] { eventName });

            // Create new FsmTransition
            var newTransition = Activator.CreateInstance(FsmTransitionType);
            FsmTransitionType.GetField("FsmEvent")?.SetValue(newTransition, fsmEvent);
            FsmTransitionType.GetField("ToState")?.SetValue(newTransition, toState);

            // Add transition to source state
            var transitionsProp = FsmStateType.GetProperty("Transitions");
            var transitionsArr = transitionsProp?.GetValue(sourceState) as Array;
            int oldLen = transitionsArr?.Length ?? 0;
            var newTransitionsArr = Array.CreateInstance(FsmTransitionType, oldLen + 1);
            if (transitionsArr != null && oldLen > 0)
                Array.Copy(transitionsArr, newTransitionsArr, oldLen);
            newTransitionsArr.SetValue(newTransition, oldLen);
            transitionsProp?.SetValue(sourceState, newTransitionsArr);

            Undo.RecordObject((UnityEngine.Object)comp, "Add FSM Transition");
            EditorUtility.SetDirty((UnityEngine.Object)comp);

            return new
            {
                success = true,
                message = $"Added transition '{fromState}' --[{eventName}]--> '{toState}'",
                gameObject = go.name
            };
        }

        private static object HandleAddAction(string target, string fsmName, string stateName, string actionType, string actionParams)
        {
            if (string.IsNullOrEmpty(stateName))
                throw MCPException.InvalidParams("'state_name' is required for add_action.");
            if (string.IsNullOrEmpty(actionType))
                throw MCPException.InvalidParams("'action_type' is required for add_action.");

            var (comp, fsm, go) = ResolveFsm(target, fsmName);

            // Find the action type
            Type actionClsType = FindType(actionType);
            if (actionClsType == null)
            {
                // Try with prefix
                actionClsType = FindType("HutongGames.PlayMaker.Actions." + actionType);
            }
            if (actionClsType == null)
                throw MCPException.InvalidParams($"Action type '{actionType}' not found. Use list_actions to find available types.");

            // Find the target state
            var statesArr = FsmType.GetProperty("States")?.GetValue(fsm) as Array;
            object targetState = null;
            if (statesArr != null)
            {
                foreach (var state in statesArr)
                {
                    var sName = FsmStateType.GetProperty("Name")?.GetValue(state) as string;
                    if (sName == stateName) { targetState = state; break; }
                }
            }
            if (targetState == null)
                throw MCPException.InvalidParams($"State '{stateName}' not found in FSM.");

            // Create the action instance
            var actionInstance = Activator.CreateInstance(actionClsType);

            // Set action parameters if provided
            if (!string.IsNullOrEmpty(actionParams))
            {
                try
                {
                    var paramsDict = JsonUtility.FromJson<Dictionary<string, string>>(actionParams);
                    // JsonUtility doesn't support Dictionary, so parse manually
                    SetActionParameters(actionInstance, actionClsType, actionParams);
                }
                catch { /* Best effort parameter setting */ }
            }

            // Add to state's Actions array
            var actionsProp = FsmStateType.GetProperty("Actions");
            var baseActionType = FindType("HutongGames.PlayMaker.FsmStateAction");
            var actionsArr = actionsProp?.GetValue(targetState) as Array;
            int oldLen = actionsArr?.Length ?? 0;
            var newActionsArr = Array.CreateInstance(baseActionType, oldLen + 1);
            if (actionsArr != null && oldLen > 0)
                Array.Copy(actionsArr, newActionsArr, oldLen);
            newActionsArr.SetValue(actionInstance, oldLen);
            actionsProp?.SetValue(targetState, newActionsArr);

            Undo.RecordObject((UnityEngine.Object)comp, "Add FSM Action");
            EditorUtility.SetDirty((UnityEngine.Object)comp);

            return new
            {
                success = true,
                message = $"Added action '{actionClsType.Name}' to state '{stateName}'",
                gameObject = go.name,
                actionType = actionClsType.FullName
            };
        }

        #endregion

        #region Helpers

        private static void EnsurePlayMakerAvailable()
        {
            if (PlayMakerFSMType == null)
                throw new MCPException("PlayMaker is not installed or not loaded. Ensure PlayMaker is in the project and UNITY_MCP_PLAYMAKER define is set.");
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

        private static (object comp, object fsm, GameObject go) ResolveFsm(string target, string fsmName)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            var fsmComponents = go.GetComponents(PlayMakerFSMType);
            if (fsmComponents.Length == 0)
                throw MCPException.InvalidParams($"No PlayMakerFSM found on '{go.name}'.");

            Component comp;
            if (!string.IsNullOrEmpty(fsmName))
            {
                comp = fsmComponents.FirstOrDefault(c =>
                {
                    var n = PlayMakerFSMType.GetProperty("FsmName")?.GetValue(c) as string;
                    return n == fsmName;
                });
                if (comp == null)
                    throw MCPException.InvalidParams($"FSM named '{fsmName}' not found on '{go.name}'. Available: {string.Join(", ", fsmComponents.Select(c => PlayMakerFSMType.GetProperty("FsmName")?.GetValue(c) as string))}");
            }
            else
            {
                comp = fsmComponents[0];
            }

            var fsm = GetFsmFromComponent(comp);
            return (comp, fsm, go);
        }

        private static object GetFsmFromComponent(object fsmComponent)
        {
            var fsmProp = PlayMakerFSMType.GetProperty("Fsm");
            return fsmProp?.GetValue(fsmComponent)
                ?? throw new MCPException("Cannot access Fsm property on PlayMakerFSM.");
        }

        private static string GetTransitionEventName(object transition)
        {
            var fsmEventField = FsmTransitionType.GetField("FsmEvent");
            var fsmEvent = fsmEventField?.GetValue(transition);
            if (fsmEvent == null) return "";
            return FsmEventType.GetProperty("Name")?.GetValue(fsmEvent) as string ?? "";
        }

        private static List<string> GetActionTypeNames(Array actions)
        {
            var names = new List<string>();
            if (actions == null) return names;
            foreach (var action in actions)
            {
                if (action != null) names.Add(action.GetType().Name);
            }
            return names;
        }

        private static List<object> GetVariablesSummary(object varsObj)
        {
            if (varsObj == null) return new List<object>();
            return GetAllVariables(varsObj);
        }

        private static List<object> GetAllVariables(object varsObj)
        {
            var result = new List<object>();
            if (varsObj == null) return result;

            // FsmVariables has arrays: FloatVariables, IntVariables, BoolVariables, StringVariables, Vector3Variables, etc.
            var varTypes = new[]
            {
                ("FloatVariables", "float"), ("IntVariables", "int"), ("BoolVariables", "bool"),
                ("StringVariables", "string"), ("Vector2Variables", "vector2"), ("Vector3Variables", "vector3"),
                ("ColorVariables", "color"), ("RectVariables", "rect"),
                ("GameObjectVariables", "gameobject"), ("ObjectVariables", "object"),
                ("MaterialVariables", "material"), ("TextureVariables", "texture"),
                ("QuaternionVariables", "quaternion"), ("EnumVariables", "enum")
            };

            foreach (var (propName, typeName) in varTypes)
            {
                var arr = FsmVariablesType.GetProperty(propName)?.GetValue(varsObj) as Array;
                if (arr == null) continue;

                foreach (var v in arr)
                {
                    if (v == null) continue;
                    var nameVal = v.GetType().GetProperty("Name")?.GetValue(v) as string;
                    string valueStr = "";
                    try
                    {
                        var valueProp = v.GetType().GetProperty("Value");
                        var val = valueProp?.GetValue(v);
                        valueStr = val?.ToString() ?? "null";
                    }
                    catch { valueStr = "<error>"; }

                    result.Add(new { name = nameVal, type = typeName, value = valueStr });
                }
            }

            return result;
        }

        private static string GetVariableNames(object varsObj)
        {
            var allVars = GetAllVariables(varsObj);
            return string.Join(", ", allVars.Select(v => ((dynamic)v).name));
        }

        private static bool TrySetExistingVariable(object varsObj, string variableName, string value)
        {
            // Try each variable type's getter
            var getterMethods = new[]
            {
                "GetFsmFloat", "GetFsmInt", "GetFsmBool", "GetFsmString",
                "GetFsmVector2", "GetFsmVector3", "GetFsmColor", "GetFsmRect",
                "GetFsmGameObject", "GetFsmObject", "GetFsmMaterial", "GetFsmTexture",
                "GetFsmQuaternion", "GetFsmEnum"
            };

            foreach (var methodName in getterMethods)
            {
                var method = FsmVariablesType.GetMethod(methodName, new[] { typeof(string) });
                if (method == null) continue;

                var fsmVar = method.Invoke(varsObj, new object[] { variableName });
                if (fsmVar == null) continue;

                // Check if it actually has this name (some return empty defaults)
                var nameVal = fsmVar.GetType().GetProperty("Name")?.GetValue(fsmVar) as string;
                if (string.IsNullOrEmpty(nameVal) || nameVal != variableName) continue;

                // Set the value
                var valueProp = fsmVar.GetType().GetProperty("Value");
                if (valueProp == null) continue;

                try
                {
                    var targetType = valueProp.PropertyType;
                    object converted;

                    if (targetType == typeof(float)) converted = float.Parse(value);
                    else if (targetType == typeof(int)) converted = int.Parse(value);
                    else if (targetType == typeof(bool)) converted = bool.Parse(value);
                    else if (targetType == typeof(string)) converted = value;
                    else if (targetType == typeof(Vector2))
                    {
                        var parts = value.Trim('(', ')').Split(',');
                        converted = new Vector2(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()));
                    }
                    else if (targetType == typeof(Vector3))
                    {
                        var parts = value.Trim('(', ')').Split(',');
                        converted = new Vector3(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()), float.Parse(parts[2].Trim()));
                    }
                    else
                    {
                        converted = Convert.ChangeType(value, targetType);
                    }

                    valueProp.SetValue(fsmVar, converted);
                    return true;
                }
                catch { continue; }
            }

            return false;
        }

        private static void SetActionParameters(object actionInstance, Type actionType, string paramsJson)
        {
            // Simple key:value parsing for action parameters
            // Expected format: {"fieldName": "value", ...}
            // Since JsonUtility doesn't handle Dictionary, parse manually
            var trimmed = paramsJson.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return;

            trimmed = trimmed.Substring(1, trimmed.Length - 2);
            var pairs = SplitJsonPairs(trimmed);

            foreach (var pair in pairs)
            {
                var colonIndex = pair.IndexOf(':');
                if (colonIndex < 0) continue;

                var key = pair.Substring(0, colonIndex).Trim().Trim('"');
                var val = pair.Substring(colonIndex + 1).Trim().Trim('"');

                // Try to set the field on the action
                var field = actionType.GetField(key, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) continue;

                try
                {
                    // Handle FsmFloat, FsmInt, FsmBool, FsmString wrappers
                    var fieldType = field.FieldType;
                    if (fieldType.Name.StartsWith("Fsm") && fieldType.Namespace == "HutongGames.PlayMaker")
                    {
                        var wrapper = field.GetValue(actionInstance);
                        if (wrapper == null)
                        {
                            wrapper = Activator.CreateInstance(fieldType);
                            field.SetValue(actionInstance, wrapper);
                        }
                        var valueProp = fieldType.GetProperty("Value");
                        if (valueProp != null)
                        {
                            var targetType = valueProp.PropertyType;
                            object converted = ConvertSimpleValue(val, targetType);
                            if (converted != null) valueProp.SetValue(wrapper, converted);
                        }
                    }
                    else
                    {
                        object converted = ConvertSimpleValue(val, fieldType);
                        if (converted != null) field.SetValue(actionInstance, converted);
                    }
                }
                catch { /* Best effort */ }
            }
        }

        private static object ConvertSimpleValue(string val, Type targetType)
        {
            if (targetType == typeof(float)) return float.Parse(val);
            if (targetType == typeof(int)) return int.Parse(val);
            if (targetType == typeof(bool)) return bool.Parse(val);
            if (targetType == typeof(string)) return val;
            if (targetType == typeof(double)) return double.Parse(val);
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
