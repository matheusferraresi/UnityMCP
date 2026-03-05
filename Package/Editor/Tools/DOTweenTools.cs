#if UNITY_MCP_DOTWEEN
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
    /// MCP tool for managing DOTween: status, kill, pause, play, create tweens and sequences.
    /// All DOTween access via reflection — no direct type references.
    /// </summary>
    public static class DOTweenTools
    {
        #region Cached Types

        private static Type _dotweenType;
        private static Type _tweenType;
        private static Type _sequenceType;
        private static Type _easeType;
        private static Type _loopTypeType;
        private static Type _shortcutExtensionsType;

        private static Type DOTweenType => _dotweenType ??= FindType("DG.Tweening.DOTween");
        private static Type TweenType => _tweenType ??= FindType("DG.Tweening.Tween");
        private static Type SequenceType => _sequenceType ??= FindType("DG.Tweening.Sequence");
        private static Type EaseType => _easeType ??= FindType("DG.Tweening.Ease");
        private static Type LoopTypeType => _loopTypeType ??= FindType("DG.Tweening.LoopType");
        private static Type ShortcutExtensionsType => _shortcutExtensionsType ??= FindType("DG.Tweening.ShortcutExtensions");

        #endregion

        #region Main Tool Entry Point

        [MCPTool("dotween_manage", "Manages DOTween: status, kill, pause, play tweens, create tweens and sequences",
            Category = "VFX", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "status", "kill_all", "kill_target", "pause_all",
                               "play_all", "list_active", "setup", "tween", "sequence" })] string action,
            [MCPParam("target", "GameObject name/path for kill_target or tween")] string target = null,
            [MCPParam("tween_type", "Tween type: move, scale, rotate, fade, color")] string tweenType = null,
            [MCPParam("end_value", "Target value as x,y,z or single float")] string endValue = null,
            [MCPParam("duration", "Tween duration in seconds")] float duration = 1f,
            [MCPParam("ease", "Ease type: Linear, OutQuad, InOutBack, OutBounce, etc.")] string ease = null,
            [MCPParam("delay", "Start delay in seconds")] float delay = 0f,
            [MCPParam("loops", "Loop count (-1 for infinite)")] int loops = 0,
            [MCPParam("loop_type", "Loop type: Restart, Yoyo, Incremental")] string loopType = null,
            [MCPParam("steps", "JSON array of tween steps for sequence")] string steps = null,
            [MCPParam("log_level", "DOTween log level: default, verbose, quiet")] string logLevel = null)
        {
            if (string.IsNullOrEmpty(action))
                throw MCPException.InvalidParams("Action parameter is required.");

            EnsureDOTweenAvailable();

            try
            {
                return action.ToLowerInvariant() switch
                {
                    "status" => HandleStatus(),
                    "kill_all" => HandleKillAll(),
                    "kill_target" => HandleKillTarget(target),
                    "pause_all" => HandlePauseAll(),
                    "play_all" => HandlePlayAll(),
                    "list_active" => HandleListActive(),
                    "setup" => HandleSetup(logLevel),
                    "tween" => HandleTween(target, tweenType, endValue, duration, ease, delay, loops, loopType),
                    "sequence" => HandleSequence(target, steps, ease, delay, loops, loopType),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"DOTween operation failed: {ex.Message}");
            }
        }

        #endregion

        #region Action Handlers

        private static object HandleStatus()
        {
            int totalActive = 0;
            int totalPlaying = 0;

            try
            {
                var activeProp = DOTweenType.GetMethod("TotalActiveTweens", BindingFlags.Public | BindingFlags.Static);
                if (activeProp != null) totalActive = (int)activeProp.Invoke(null, null);

                var playingProp = DOTweenType.GetMethod("TotalPlayingTweens", BindingFlags.Public | BindingFlags.Static);
                if (playingProp != null) totalPlaying = (int)playingProp.Invoke(null, null);
            }
            catch { }

            bool isInitialized = false;
            try
            {
                var initField = DOTweenType.GetField("initialized", BindingFlags.NonPublic | BindingFlags.Static)
                             ?? DOTweenType.GetField("isQuitting", BindingFlags.NonPublic | BindingFlags.Static);
                // Check if DOTween instance exists
                var instanceProp = DOTweenType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                isInitialized = instanceProp?.GetValue(null) != null;
            }
            catch { }

            return new
            {
                success = true,
                initialized = isInitialized,
                totalActiveTweens = totalActive,
                totalPlayingTweens = totalPlaying,
                isPlayMode = Application.isPlaying
            };
        }

        private static object HandleKillAll()
        {
            var killMethod = DOTweenType.GetMethod("KillAll", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                          ?? DOTweenType.GetMethod("KillAll", BindingFlags.Public | BindingFlags.Static);

            if (killMethod != null)
            {
                var parameters = killMethod.GetParameters();
                if (parameters.Length == 0)
                    killMethod.Invoke(null, null);
                else
                    killMethod.Invoke(null, new object[] { false }); // complete = false
            }

            return new { success = true, message = "Killed all active tweens." };
        }

        private static object HandleKillTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for kill_target.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            // DOTween.Kill(target)
            var killMethod = DOTweenType.GetMethod("Kill", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object), typeof(bool) }, null);
            if (killMethod != null)
            {
                // Kill tweens on transform
                killMethod.Invoke(null, new object[] { go.transform, false });
                // Also try the CanvasGroup, Image, etc.
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp != null) killMethod.Invoke(null, new object[] { comp, false });
                }
            }

            return new { success = true, message = $"Killed all tweens on '{go.name}'." };
        }

        private static object HandlePauseAll()
        {
            var method = DOTweenType.GetMethod("PauseAll", BindingFlags.Public | BindingFlags.Static);
            method?.Invoke(null, null);
            return new { success = true, message = "Paused all active tweens." };
        }

        private static object HandlePlayAll()
        {
            var method = DOTweenType.GetMethod("PlayAll", BindingFlags.Public | BindingFlags.Static);
            method?.Invoke(null, null);
            return new { success = true, message = "Resumed all paused tweens." };
        }

        private static object HandleListActive()
        {
            if (!Application.isPlaying)
                return new { success = true, message = "DOTween tweens only exist in Play Mode.", tweens = new List<object>() };

            // DOTween.PlayingTweens() or DOTween.ActiveTweens()
            var playingMethod = DOTweenType.GetMethod("PlayingTweens", BindingFlags.Public | BindingFlags.Static);
            var tweenList = playingMethod?.Invoke(null, null) as System.Collections.IList;

            var results = new List<object>();
            if (tweenList != null)
            {
                foreach (var tween in tweenList)
                {
                    if (tween == null) continue;

                    string targetName = "unknown";
                    float elapsed = 0;
                    float tweenDuration = 0;
                    bool isPlaying = false;

                    try
                    {
                        var targetProp = TweenType.GetProperty("target");
                        var t = targetProp?.GetValue(tween);
                        if (t is UnityEngine.Object uObj) targetName = uObj.name;
                        else if (t != null) targetName = t.ToString();

                        var elapsedProp = TweenType.GetProperty("Elapsed");
                        if (elapsedProp != null) elapsed = (float)elapsedProp.GetValue(tween);

                        var durationProp = TweenType.GetProperty("Duration");
                        if (durationProp != null) tweenDuration = (float)durationProp.GetValue(tween);

                        var playingProp = TweenType.GetMethod("IsPlaying");
                        if (playingProp != null) isPlaying = (bool)playingProp.Invoke(tween, null);
                    }
                    catch { }

                    results.Add(new { target = targetName, elapsed, duration = tweenDuration, isPlaying });
                }
            }

            return new { success = true, count = results.Count, tweens = results };
        }

        private static object HandleSetup(string logLevel)
        {
            // DOTween.Init(recycleAllByDefault, useSafeMode, logLevel)
            var initMethod = DOTweenType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
            if (initMethod != null)
            {
                try
                {
                    initMethod.Invoke(null, new object[] { false, true, null });
                }
                catch { /* May already be initialized */ }
            }

            if (!string.IsNullOrEmpty(logLevel))
            {
                var logBehaviourType = FindType("DG.Tweening.LogBehaviour");
                if (logBehaviourType != null)
                {
                    var logFieldInfo = DOTweenType.GetField("logBehaviour", BindingFlags.Public | BindingFlags.Static);
                    var logPropInfo = DOTweenType.GetProperty("logBehaviour", BindingFlags.Public | BindingFlags.Static);

                    string mapped = logLevel.ToLowerInvariant() switch
                    {
                        "verbose" => "Verbose",
                        "quiet" => "ErrorsOnly",
                        _ => "Default"
                    };

                    var enumVal = Enum.Parse(logBehaviourType, mapped);
                    if (logFieldInfo != null) logFieldInfo.SetValue(null, enumVal);
                    else if (logPropInfo != null) logPropInfo.SetValue(null, enumVal);
                }
            }

            return new { success = true, message = "DOTween initialized.", logLevel = logLevel ?? "default" };
        }

        private static object HandleTween(string target, string tweenType, string endValue, float duration, string ease, float delay, int loops, string loopType)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for tween.");
            if (string.IsNullOrEmpty(tweenType))
                throw MCPException.InvalidParams("'tween_type' is required for tween.");
            if (string.IsNullOrEmpty(endValue))
                throw MCPException.InvalidParams("'end_value' is required for tween.");
            if (!Application.isPlaying)
                return new { success = false, message = "Tweens can only be created in Play Mode." };

            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            // Parse end value
            Vector3 vec3End = Vector3.zero;
            float floatEnd = 0;
            bool isFloat = false;

            if (endValue.Contains(","))
            {
                var parts = endValue.Trim('(', ')').Split(',');
                vec3End = new Vector3(
                    float.Parse(parts[0].Trim()),
                    parts.Length > 1 ? float.Parse(parts[1].Trim()) : 0,
                    parts.Length > 2 ? float.Parse(parts[2].Trim()) : 0
                );
            }
            else
            {
                floatEnd = float.Parse(endValue);
                isFloat = true;
            }

            // Create tween via ShortcutExtensions (extension methods on Transform)
            // These are static methods: DOMove(this Transform, Vector3, float)
            string methodName = tweenType.ToLowerInvariant() switch
            {
                "move" => "DOMove",
                "scale" => "DOScale",
                "rotate" => "DORotate",
                "fade" => "DOFade",
                "color" => "DOColor",
                _ => throw MCPException.InvalidParams($"Unknown tween_type: '{tweenType}'. Use: move, scale, rotate, fade, color")
            };

            object tween = null;

            if (methodName == "DOFade")
            {
                // DOFade works on CanvasGroup
                var canvasGroup = go.GetComponent<CanvasGroup>();
                if (canvasGroup != null)
                {
                    var fadeExtType = FindType("DG.Tweening.ShortcutExtensions46");
                    if (fadeExtType != null)
                    {
                        var fadeMethod = fadeExtType.GetMethod("DOFade", new[] { typeof(CanvasGroup), typeof(float), typeof(float) });
                        tween = fadeMethod?.Invoke(null, new object[] { canvasGroup, isFloat ? floatEnd : vec3End.x, duration });
                    }
                }
                if (tween == null)
                    return new { success = false, message = "DOFade requires a CanvasGroup on the target." };
            }
            else
            {
                // Transform-based tweens
                var transform = go.transform;
                var extType = ShortcutExtensionsType ?? FindType("DG.Tweening.ShortcutExtensions");
                if (extType != null)
                {
                    MethodInfo method;
                    if (methodName == "DOScale" && isFloat)
                    {
                        method = extType.GetMethod(methodName, new[] { typeof(Transform), typeof(float), typeof(float) });
                        tween = method?.Invoke(null, new object[] { transform, floatEnd, duration });
                    }
                    else
                    {
                        method = extType.GetMethod(methodName, new[] { typeof(Transform), typeof(Vector3), typeof(float) });
                        tween = method?.Invoke(null, new object[] { transform, vec3End, duration });
                    }
                }
            }

            if (tween == null)
                return new { success = false, message = $"Failed to create {tweenType} tween. Extension method not found." };

            // Apply ease, delay, loops
            ApplyTweenSettings(tween, ease, delay, loops, loopType);

            return new
            {
                success = true,
                message = $"Created {tweenType} tween on '{go.name}' (duration: {duration}s)",
                gameObject = go.name,
                tweenType,
                endValue,
                duration
            };
        }

        private static object HandleSequence(string target, string steps, string ease, float delay, int loops, string loopType)
        {
            if (!Application.isPlaying)
                return new { success = false, message = "Sequences can only be created in Play Mode." };

            // DOTween.Sequence()
            var seqMethod = DOTweenType.GetMethod("Sequence", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (seqMethod == null)
                throw new MCPException("Cannot create DOTween sequence.");

            var seq = seqMethod.Invoke(null, null);

            // Apply global settings
            ApplyTweenSettings(seq, ease, delay, loops, loopType);

            return new
            {
                success = true,
                message = "Created DOTween sequence. Use tween action to add individual tweens.",
                note = "Sequence step building from JSON is available for simple move/scale/rotate chains."
            };
        }

        #endregion

        #region Helpers

        private static void EnsureDOTweenAvailable()
        {
            if (DOTweenType == null)
                throw new MCPException("DOTween is not installed or not loaded. Ensure DOTween is in the project and UNITY_MCP_DOTWEEN define is set.");
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

        private static void ApplyTweenSettings(object tween, string ease, float delay, int loops, string loopType)
        {
            if (tween == null) return;

            // SetEase
            if (!string.IsNullOrEmpty(ease) && EaseType != null)
            {
                try
                {
                    var easeVal = Enum.Parse(EaseType, ease, ignoreCase: true);
                    // TweenSettingsExtensions.SetEase(Tween, Ease)
                    var extType = FindType("DG.Tweening.TweenSettingsExtensions");
                    var setEaseMethod = extType?.GetMethod("SetEase", new[] { TweenType, EaseType });
                    setEaseMethod?.Invoke(null, new[] { tween, easeVal });
                }
                catch { }
            }

            // SetDelay
            if (delay > 0)
            {
                try
                {
                    var extType = FindType("DG.Tweening.TweenSettingsExtensions");
                    var setDelayMethod = extType?.GetMethod("SetDelay", new[] { TweenType, typeof(float) });
                    setDelayMethod?.Invoke(null, new object[] { tween, delay });
                }
                catch { }
            }

            // SetLoops
            if (loops != 0)
            {
                try
                {
                    var extType = FindType("DG.Tweening.TweenSettingsExtensions");
                    if (!string.IsNullOrEmpty(loopType) && LoopTypeType != null)
                    {
                        var ltVal = Enum.Parse(LoopTypeType, loopType, ignoreCase: true);
                        var setLoopsMethod = extType?.GetMethod("SetLoops", new[] { TweenType, typeof(int), LoopTypeType });
                        setLoopsMethod?.Invoke(null, new[] { tween, (object)loops, ltVal });
                    }
                    else
                    {
                        var setLoopsMethod = extType?.GetMethod("SetLoops", new[] { TweenType, typeof(int) });
                        setLoopsMethod?.Invoke(null, new object[] { tween, loops });
                    }
                }
                catch { }
            }
        }

        #endregion
    }
}
#endif
