using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Comprehensive animation tools for creating and managing AnimatorControllers and AnimationClips.
    /// Inspired by CoplayDev/unity-mcp's 27-action animation system.
    /// </summary>
    public static class AnimationTools
    {
        #region AnimatorController Tools

        [MCPTool("animation_controller", "Manage AnimatorControllers: create, add_state, add_transition, add_parameter, get_info, assign, add_layer",
            Category = "Animation", DestructiveHint = true)]
        public static object ManageController(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "create", "add_state", "add_transition", "add_parameter", "get_info", "assign", "add_layer", "remove_layer", "set_layer_weight" })] string action,
            [MCPParam("path", "Asset path for the controller (relative to Assets/)")] string path = null,
            [MCPParam("name", "Name for the state, parameter, or layer")] string name = null,
            [MCPParam("layer", "Layer index or name (default: 0)")] string layer = "0",
            [MCPParam("motion", "Path to AnimationClip asset to assign as motion")] string motion = null,
            [MCPParam("from_state", "Source state name for transitions")] string fromState = null,
            [MCPParam("to_state", "Destination state name for transitions (use 'exit' for exit state)")] string toState = null,
            [MCPParam("has_exit_time", "Whether transition uses exit time (default: true)")] bool hasExitTime = true,
            [MCPParam("duration", "Transition duration in seconds")] float duration = 0.25f,
            [MCPParam("parameter_type", "Parameter type: float, int, bool, trigger")] string parameterType = "bool",
            [MCPParam("default_value", "Default value for the parameter")] string defaultValue = null,
            [MCPParam("condition_param", "Parameter name for transition condition")] string conditionParam = null,
            [MCPParam("condition_mode", "Condition mode: if, ifnot, greater, less, equals, notequal")] string conditionMode = null,
            [MCPParam("condition_threshold", "Threshold value for condition")] float conditionThreshold = 0f,
            [MCPParam("target", "GameObject instance ID or path to assign controller to")] string target = null,
            [MCPParam("is_default", "Set as default state (default: false)")] bool isDefault = false,
            [MCPParam("weight", "Layer weight (0-1)")] float weight = 1f,
            [MCPParam("blending", "Layer blending mode: override, additive")] string blending = "override")
        {
            if (string.IsNullOrEmpty(action))
                throw MCPException.InvalidParams("'action' parameter is required.");

            return action.ToLowerInvariant() switch
            {
                "create" => CreateController(path),
                "add_state" => AddState(path, name, layer, motion, isDefault),
                "add_transition" => AddTransition(path, fromState, toState, layer, hasExitTime, duration, conditionParam, conditionMode, conditionThreshold),
                "add_parameter" => AddParameter(path, name, parameterType, defaultValue),
                "get_info" => GetControllerInfo(path),
                "assign" => AssignController(path, target),
                "add_layer" => AddLayer(path, name, weight, blending),
                "remove_layer" => RemoveLayer(path, layer),
                "set_layer_weight" => SetLayerWeight(path, layer, weight),
                _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
            };
        }

        private static object CreateController(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw MCPException.InvalidParams("'path' is required for create.");

            string assetPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
            if (!assetPath.EndsWith(".controller"))
                assetPath += ".controller";

            var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);

            return new
            {
                success = true,
                message = $"AnimatorController created at '{assetPath}'.",
                path = assetPath,
                layers = controller.layers.Length,
                parameters = controller.parameters.Length
            };
        }

        private static object AddState(string path, string name, string layerStr, string motionPath, bool isDefault)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
                throw MCPException.InvalidParams("'path' and 'name' are required for add_state.");

            var controller = LoadController(path);
            int layerIndex = ResolveLayerIndex(controller, layerStr);
            var stateMachine = controller.layers[layerIndex].stateMachine;

            var state = stateMachine.AddState(name);

            if (!string.IsNullOrEmpty(motionPath))
            {
                string motionAssetPath = motionPath.StartsWith("Assets/") ? motionPath : $"Assets/{motionPath}";
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(motionAssetPath);
                if (clip != null) state.motion = clip;
            }

            if (isDefault)
                stateMachine.defaultState = state;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"State '{name}' added to layer {layerIndex}.",
                state = name,
                layer = layerIndex,
                isDefault
            };
        }

        private static object AddTransition(string path, string fromState, string toState, string layerStr,
            bool hasExitTime, float duration, string conditionParam, string conditionMode, float conditionThreshold)
        {
            if (string.IsNullOrEmpty(path))
                throw MCPException.InvalidParams("'path' is required.");

            var controller = LoadController(path);
            int layerIndex = ResolveLayerIndex(controller, layerStr);
            var stateMachine = controller.layers[layerIndex].stateMachine;

            // Find source state (null = AnyState)
            AnimatorState source = null;
            if (!string.IsNullOrEmpty(fromState) && fromState.ToLower() != "any")
            {
                source = FindState(stateMachine, fromState);
                if (source == null)
                    throw MCPException.InvalidParams($"Source state '{fromState}' not found.");
            }

            AnimatorStateTransition transition;

            if (!string.IsNullOrEmpty(toState) && toState.ToLower() == "exit")
            {
                transition = source != null ? source.AddExitTransition() : throw MCPException.InvalidParams("Cannot add exit transition from AnyState.");
            }
            else if (string.IsNullOrEmpty(toState))
            {
                throw MCPException.InvalidParams("'to_state' is required.");
            }
            else
            {
                var dest = FindState(stateMachine, toState);
                if (dest == null)
                    throw MCPException.InvalidParams($"Destination state '{toState}' not found.");

                transition = source != null
                    ? source.AddTransition(dest)
                    : stateMachine.AddAnyStateTransition(dest);
            }

            transition.hasExitTime = hasExitTime;
            transition.duration = duration;

            // Add condition if specified
            if (!string.IsNullOrEmpty(conditionParam) && !string.IsNullOrEmpty(conditionMode))
            {
                var mode = conditionMode.ToLower() switch
                {
                    "if" => AnimatorConditionMode.If,
                    "ifnot" => AnimatorConditionMode.IfNot,
                    "greater" => AnimatorConditionMode.Greater,
                    "less" => AnimatorConditionMode.Less,
                    "equals" => AnimatorConditionMode.Equals,
                    "notequal" => AnimatorConditionMode.NotEqual,
                    _ => AnimatorConditionMode.If
                };
                transition.AddCondition(mode, conditionThreshold, conditionParam);
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Transition added: {fromState ?? "AnyState"} → {toState}.",
                from = fromState ?? "AnyState",
                to = toState,
                hasExitTime,
                duration
            };
        }

        private static object AddParameter(string path, string name, string parameterType, string defaultValue)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
                throw MCPException.InvalidParams("'path' and 'name' are required.");

            var controller = LoadController(path);

            var type = parameterType?.ToLower() switch
            {
                "float" => AnimatorControllerParameterType.Float,
                "int" => AnimatorControllerParameterType.Int,
                "bool" => AnimatorControllerParameterType.Bool,
                "trigger" => AnimatorControllerParameterType.Trigger,
                _ => AnimatorControllerParameterType.Bool
            };

            controller.AddParameter(name, type);

            // Set default value if provided
            if (!string.IsNullOrEmpty(defaultValue))
            {
                var param = controller.parameters.Last();
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        if (float.TryParse(defaultValue, out float fVal)) param.defaultFloat = fVal;
                        break;
                    case AnimatorControllerParameterType.Int:
                        if (int.TryParse(defaultValue, out int iVal)) param.defaultInt = iVal;
                        break;
                    case AnimatorControllerParameterType.Bool:
                        param.defaultBool = defaultValue.ToLower() == "true";
                        break;
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Parameter '{name}' ({type}) added.",
                name,
                type = type.ToString(),
                defaultValue
            };
        }

        private static object GetControllerInfo(string path)
        {
            var controller = LoadController(path);

            var layers = new List<object>();
            for (int i = 0; i < controller.layers.Length; i++)
            {
                var layer = controller.layers[i];
                var states = layer.stateMachine.states.Select(s => new
                {
                    name = s.state.name,
                    motion = s.state.motion?.name,
                    speed = s.state.speed,
                    isDefault = layer.stateMachine.defaultState == s.state,
                    transitions = s.state.transitions.Length
                }).ToArray();

                layers.Add(new
                {
                    index = i,
                    name = layer.name,
                    weight = layer.defaultWeight,
                    blendingMode = layer.blendingMode.ToString(),
                    stateCount = states.Length,
                    states,
                    anyStateTransitions = layer.stateMachine.anyStateTransitions.Length
                });
            }

            var parameters = controller.parameters.Select(p => new
            {
                name = p.name,
                type = p.type.ToString(),
                defaultValue = p.type switch
                {
                    AnimatorControllerParameterType.Float => p.defaultFloat.ToString(),
                    AnimatorControllerParameterType.Int => p.defaultInt.ToString(),
                    AnimatorControllerParameterType.Bool => p.defaultBool.ToString(),
                    _ => ""
                }
            }).ToArray();

            return new
            {
                success = true,
                path = AssetDatabase.GetAssetPath(controller),
                layers,
                parameters,
                layerCount = controller.layers.Length,
                parameterCount = controller.parameters.Length
            };
        }

        private static object AssignController(string path, string target)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for assign.");

            var controller = LoadController(path);
            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                animator = go.AddComponent<Animator>();

            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(go);

            return new
            {
                success = true,
                message = $"Controller assigned to '{go.name}'.",
                gameObject = go.name,
                controller = AssetDatabase.GetAssetPath(controller)
            };
        }

        private static object AddLayer(string path, string name, float weight, string blending)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
                throw MCPException.InvalidParams("'path' and 'name' are required.");

            var controller = LoadController(path);
            controller.AddLayer(name);

            // Set layer properties
            var layers = controller.layers;
            var newLayer = layers[layers.Length - 1];
            newLayer.defaultWeight = weight;
            newLayer.blendingMode = blending?.ToLower() == "additive"
                ? AnimatorLayerBlendingMode.Additive
                : AnimatorLayerBlendingMode.Override;
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Layer '{name}' added.",
                layerIndex = layers.Length - 1,
                weight,
                blending = newLayer.blendingMode.ToString()
            };
        }

        private static object RemoveLayer(string path, string layerStr)
        {
            var controller = LoadController(path);
            int layerIndex = ResolveLayerIndex(controller, layerStr);

            if (layerIndex == 0)
                throw MCPException.InvalidParams("Cannot remove the base layer (index 0).");

            controller.RemoveLayer(layerIndex);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, message = $"Layer {layerIndex} removed." };
        }

        private static object SetLayerWeight(string path, string layerStr, float weight)
        {
            var controller = LoadController(path);
            int layerIndex = ResolveLayerIndex(controller, layerStr);

            var layers = controller.layers;
            layers[layerIndex].defaultWeight = Mathf.Clamp01(weight);
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, message = $"Layer {layerIndex} weight set to {weight}." };
        }

        #endregion

        #region AnimationClip Tools

        [MCPTool("animation_clip", "Manage AnimationClips: create, get_info, add_curve, create_preset, assign",
            Category = "Animation", DestructiveHint = true)]
        public static object ManageClip(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "create", "get_info", "add_curve", "create_preset", "assign" })] string action,
            [MCPParam("path", "Asset path for the clip")] string path = null,
            [MCPParam("name", "Clip name")] string name = null,
            [MCPParam("loop", "Whether clip should loop")] bool loop = false,
            [MCPParam("property_path", "Property path for curve (e.g., 'localPosition.x')")] string propertyPath = null,
            [MCPParam("component_type", "Component type for curve binding (e.g., 'Transform')")] string componentType = "Transform",
            [MCPParam("keys", "Keyframe array: [{\"time\": 0, \"value\": 0}, ...]")] List<object> keys = null,
            [MCPParam("preset", "Preset type: idle_bob, spin, pulse, fade_in, fade_out")] string preset = null,
            [MCPParam("duration", "Clip duration in seconds")] float duration = 1f,
            [MCPParam("target", "GameObject to assign clip to")] string target = null)
        {
            return action?.ToLowerInvariant() switch
            {
                "create" => CreateClip(path, name, loop, duration),
                "get_info" => GetClipInfo(path),
                "add_curve" => AddCurve(path, propertyPath, componentType, keys),
                "create_preset" => CreatePresetClip(path, name, preset, duration, loop),
                "assign" => AssignClip(path, target),
                _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
            };
        }

        private static object CreateClip(string path, string name, bool loop, float duration)
        {
            if (string.IsNullOrEmpty(path))
                throw MCPException.InvalidParams("'path' is required.");

            string assetPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
            if (!assetPath.EndsWith(".anim"))
                assetPath += ".anim";

            var clip = new AnimationClip();
            if (!string.IsNullOrEmpty(name))
                clip.name = name;

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"AnimationClip created at '{assetPath}'.",
                path = assetPath,
                loop,
                duration
            };
        }

        private static object GetClipInfo(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw MCPException.InvalidParams("'path' is required.");

            string assetPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
                throw MCPException.InvalidParams($"AnimationClip not found at '{assetPath}'.");

            var bindings = AnimationUtility.GetCurveBindings(clip);
            var curves = bindings.Select(b => new
            {
                path = b.path,
                property = b.propertyName,
                type = b.type.Name,
                keyCount = AnimationUtility.GetEditorCurve(clip, b)?.keys.Length ?? 0
            }).ToArray();

            var settings = AnimationUtility.GetAnimationClipSettings(clip);

            return new
            {
                success = true,
                name = clip.name,
                path = assetPath,
                length = clip.length,
                frameRate = clip.frameRate,
                loop = settings.loopTime,
                curves,
                curveCount = curves.Length,
                hasEvents = clip.events.Length > 0,
                eventCount = clip.events.Length
            };
        }

        private static object AddCurve(string path, string propertyPath, string componentType, List<object> keys)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(propertyPath))
                throw MCPException.InvalidParams("'path' and 'property_path' are required.");

            string assetPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
                throw MCPException.InvalidParams($"AnimationClip not found at '{assetPath}'.");

            var type = ResolveType(componentType);
            var curve = new AnimationCurve();

            if (keys != null)
            {
                foreach (var key in keys)
                {
                    var keyObj = key is Newtonsoft.Json.Linq.JObject jo ? jo : Newtonsoft.Json.Linq.JObject.FromObject(key);
                    float time = keyObj["time"]?.Value<float>() ?? 0f;
                    float value = keyObj["value"]?.Value<float>() ?? 0f;
                    curve.AddKey(time, value);
                }
            }

            var binding = EditorCurveBinding.FloatCurve("", type, propertyPath);
            AnimationUtility.SetEditorCurve(clip, binding, curve);

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Curve added to '{propertyPath}' with {curve.keys.Length} keyframes.",
                property = propertyPath,
                componentType,
                keyCount = curve.keys.Length
            };
        }

        private static object CreatePresetClip(string path, string name, string preset, float duration, bool loop)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(preset))
                throw MCPException.InvalidParams("'path' and 'preset' are required.");

            string assetPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
            if (!assetPath.EndsWith(".anim"))
                assetPath += ".anim";

            var clip = new AnimationClip();
            if (!string.IsNullOrEmpty(name))
                clip.name = name;

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            switch (preset.ToLower())
            {
                case "idle_bob":
                    var bobCurve = new AnimationCurve(
                        new Keyframe(0, 0), new Keyframe(duration / 2, 0.1f), new Keyframe(duration, 0));
                    clip.SetCurve("", typeof(Transform), "localPosition.y", bobCurve);
                    break;

                case "spin":
                    var spinCurve = new AnimationCurve(
                        new Keyframe(0, 0), new Keyframe(duration, 360));
                    clip.SetCurve("", typeof(Transform), "localEulerAngles.y", spinCurve);
                    break;

                case "pulse":
                    var pulseCurve = new AnimationCurve(
                        new Keyframe(0, 1), new Keyframe(duration / 2, 1.2f), new Keyframe(duration, 1));
                    clip.SetCurve("", typeof(Transform), "localScale.x", pulseCurve);
                    clip.SetCurve("", typeof(Transform), "localScale.y", pulseCurve);
                    clip.SetCurve("", typeof(Transform), "localScale.z", pulseCurve);
                    break;

                case "fade_in":
                    var fadeInCurve = new AnimationCurve(
                        new Keyframe(0, 0), new Keyframe(duration, 1));
                    clip.SetCurve("", typeof(CanvasGroup), "m_Alpha", fadeInCurve);
                    break;

                case "fade_out":
                    var fadeOutCurve = new AnimationCurve(
                        new Keyframe(0, 1), new Keyframe(duration, 0));
                    clip.SetCurve("", typeof(CanvasGroup), "m_Alpha", fadeOutCurve);
                    break;

                default:
                    throw MCPException.InvalidParams($"Unknown preset: '{preset}'. Valid: idle_bob, spin, pulse, fade_in, fade_out");
            }

            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Preset clip '{preset}' created at '{assetPath}'.",
                path = assetPath,
                preset,
                duration,
                loop
            };
        }

        private static object AssignClip(string path, string target)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'path' and 'target' are required.");

            string assetPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
                throw MCPException.InvalidParams($"AnimationClip not found at '{assetPath}'.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            var animation = go.GetComponent<Animation>();
            if (animation == null)
                animation = go.AddComponent<Animation>();

            animation.clip = clip;
            animation.AddClip(clip, clip.name);
            EditorUtility.SetDirty(go);

            return new
            {
                success = true,
                message = $"Clip '{clip.name}' assigned to '{go.name}'.",
                gameObject = go.name,
                clip = clip.name
            };
        }

        #endregion

        #region Helpers

        private static AnimatorController LoadController(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw MCPException.InvalidParams("'path' is required.");

            string assetPath = path.StartsWith("Assets/") ? path : $"Assets/{path}";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (controller == null)
                throw MCPException.InvalidParams($"AnimatorController not found at '{assetPath}'.");

            return controller;
        }

        private static int ResolveLayerIndex(AnimatorController controller, string layerStr)
        {
            if (int.TryParse(layerStr, out int index))
            {
                if (index < 0 || index >= controller.layers.Length)
                    throw MCPException.InvalidParams($"Layer index {index} out of range (0-{controller.layers.Length - 1}).");
                return index;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                if (controller.layers[i].name == layerStr)
                    return i;
            }

            throw MCPException.InvalidParams($"Layer '{layerStr}' not found.");
        }

        private static AnimatorState FindState(AnimatorStateMachine stateMachine, string stateName)
        {
            foreach (var cs in stateMachine.states)
            {
                if (cs.state.name == stateName)
                    return cs.state;
            }
            return null;
        }

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return typeof(Transform);

            return typeName.ToLower() switch
            {
                "transform" => typeof(Transform),
                "renderer" => typeof(Renderer),
                "meshrenderer" => typeof(MeshRenderer),
                "spriterenderer" => typeof(SpriteRenderer),
                "light" => typeof(Light),
                "camera" => typeof(Camera),
                "canvasgroup" => typeof(CanvasGroup),
                "recttransform" => typeof(RectTransform),
                _ => Type.GetType(typeName) ?? typeof(Transform)
            };
        }

        #endregion
    }
}
