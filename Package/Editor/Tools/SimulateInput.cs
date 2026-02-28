using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Simulate keyboard and mouse input during Play Mode.
    /// Like Cypress for Unity games — supports single actions and timed sequences.
    /// Uses InputState.Change to inject into Unity's Input System 1.17+.
    /// </summary>
    public static class SimulateInput
    {
        private static readonly HashSet<Key> _heldKeys = new HashSet<Key>();
        private static readonly HashSet<int> _heldMouseButtons = new HashSet<int>();

        [MCPTool("simulate_input",
            "Simulate keyboard and mouse input during Play Mode. Like Cypress for Unity games. " +
            "Supports key_down/up/tap, mouse_move/click, and timed sequences with delay_ms.",
            Category = "Input", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "key_down", "key_up", "key_tap",
                               "mouse_move", "mouse_down", "mouse_up", "mouse_click",
                               "sequence", "get_job", "cancel", "release_all", "status" })]
            string action,
            [MCPParam("key", "Key name: space, w, a, s, d, leftshift, return, escape, up, down, left, right, etc.")]
            string key = null,
            [MCPParam("mouse_button", "Mouse button: left, right, middle (default: left)")]
            string mouseButton = null,
            [MCPParam("x", "Screen X coordinate for mouse actions")]
            double x = double.NaN,
            [MCPParam("y", "Screen Y coordinate for mouse actions")]
            double y = double.NaN,
            [MCPParam("target_object", "GameObject name — auto-calculates screen position via Camera.main")]
            string targetObject = null,
            [MCPParam("hold_frames", "Frames to hold tap/click before releasing (default: 2, min: 1)")]
            int holdFrames = 2,
            [MCPParam("steps", "Sequence steps array: [{\"action\":\"key_tap\",\"key\":\"space\",\"delay_ms\":500}, ...]")]
            object steps = null,
            [MCPParam("job_id", "Job ID for get_job/cancel actions")]
            string jobId = null)
        {
            switch (action.ToLower())
            {
                case "key_down": return DoKeyDown(key);
                case "key_up": return DoKeyUp(key);
                case "key_tap": return DoKeyTap(key, Math.Max(1, holdFrames));
                case "mouse_move": return DoMouseMove(x, y, targetObject);
                case "mouse_down": return DoMouseDown(mouseButton, x, y, targetObject);
                case "mouse_up": return DoMouseUp(mouseButton);
                case "mouse_click": return DoMouseClick(mouseButton, x, y, targetObject, Math.Max(1, holdFrames));
                case "sequence": return StartSequence(steps);
                case "get_job": return GetJob(jobId);
                case "cancel": return CancelJob(jobId);
                case "release_all": return DoReleaseAll();
                case "status": return GetStatus();
                default:
                    throw MCPException.InvalidParams($"Unknown action '{action}'.");
            }
        }

        #region Play Mode Guard

        private static void RequirePlayMode()
        {
            if (!EditorApplication.isPlaying)
                throw MCPException.InvalidParams(
                    "simulate_input requires Play Mode. Enter play mode first with playmode_enter.");
        }

        #endregion

        #region Key Actions

        private static object DoKeyDown(string keyName)
        {
            RequirePlayMode();
            var k = ParseKey(keyName);
            InjectKeyDown(k);
            return new { success = true, action = "key_down", key = keyName, held_keys = GetHeldKeyNames() };
        }

        private static object DoKeyUp(string keyName)
        {
            RequirePlayMode();
            var k = ParseKey(keyName);
            InjectKeyUp(k);
            return new { success = true, action = "key_up", key = keyName, held_keys = GetHeldKeyNames() };
        }

        private static object DoKeyTap(string keyName, int holdFrames)
        {
            RequirePlayMode();
            var k = ParseKey(keyName);
            InjectKeyDown(k);
            ScheduleRelease(() => { if (EditorApplication.isPlaying) InjectKeyUp(k); }, holdFrames);
            return new { success = true, action = "key_tap", key = keyName, hold_frames = holdFrames };
        }

        #endregion

        #region Mouse Actions

        private static object DoMouseMove(double x, double y, string targetObject)
        {
            RequirePlayMode();
            var pos = ResolveMousePosition(x, y, targetObject);
            InjectMouseMove(pos);
            return new { success = true, action = "mouse_move", screen_x = pos.x, screen_y = pos.y };
        }

        private static object DoMouseDown(string mouseButton, double x, double y, string targetObject)
        {
            RequirePlayMode();
            int btn = ParseMouseButton(mouseButton);

            if (!double.IsNaN(x) || !double.IsNaN(y) || !string.IsNullOrEmpty(targetObject))
            {
                var pos = ResolveMousePosition(x, y, targetObject);
                InjectMouseMove(pos);
            }

            InjectMouseDown(btn);
            return new { success = true, action = "mouse_down", button = MouseButtonName(btn),
                         held_buttons = GetHeldButtonNames() };
        }

        private static object DoMouseUp(string mouseButton)
        {
            RequirePlayMode();
            int btn = ParseMouseButton(mouseButton);
            InjectMouseUp(btn);
            return new { success = true, action = "mouse_up", button = MouseButtonName(btn),
                         held_buttons = GetHeldButtonNames() };
        }

        private static object DoMouseClick(string mouseButton, double x, double y, string targetObject, int holdFrames)
        {
            RequirePlayMode();
            int btn = ParseMouseButton(mouseButton);

            if (!double.IsNaN(x) || !double.IsNaN(y) || !string.IsNullOrEmpty(targetObject))
            {
                var pos = ResolveMousePosition(x, y, targetObject);
                InjectMouseMove(pos);
            }

            InjectMouseDown(btn);
            int capturedBtn = btn;
            ScheduleRelease(() => { if (EditorApplication.isPlaying) InjectMouseUp(capturedBtn); }, holdFrames);
            return new { success = true, action = "mouse_click", button = MouseButtonName(btn),
                         hold_frames = holdFrames };
        }

        #endregion

        #region Release All / Status

        private static object DoReleaseAll()
        {
            int keysReleased = _heldKeys.Count;
            int buttonsReleased = _heldMouseButtons.Count;
            ReleaseAllHeld();
            return new { success = true, keys_released = keysReleased, buttons_released = buttonsReleased };
        }

        private static object GetStatus()
        {
            var currentJob = InputSimJobManager.CurrentJob;
            return new
            {
                success = true,
                is_playing = EditorApplication.isPlaying,
                held_keys = GetHeldKeyNames(),
                held_mouse_buttons = GetHeldButtonNames(),
                active_sequence = currentJob != null && currentJob.status == InputSimJobStatus.Running
                    ? (object)new { job_id = currentJob.jobId, completed_steps = currentJob.completedSteps,
                                   total_steps = currentJob.totalSteps }
                    : null
            };
        }

        #endregion

        #region Input Injection

        private static void InjectKeyDown(Key key)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                throw new MCPException("No keyboard device available. Is a Keyboard connected?");
            QueueKeyEvent(keyboard, key, true);
            _heldKeys.Add(key);
        }

        private static void InjectKeyUp(Key key)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                throw new MCPException("No keyboard device available.");
            QueueKeyEvent(keyboard, key, false);
            _heldKeys.Remove(key);
        }

        private static void QueueKeyEvent(Keyboard keyboard, Key key, bool pressed)
        {
            using (StateEvent.From(keyboard, out var eventPtr))
            {
                keyboard[key].WriteValueIntoEvent(pressed ? 1f : 0f, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        private static void InjectMouseMove(Vector2 screenPos)
        {
            var mouse = Mouse.current;
            if (mouse == null)
                throw new MCPException("No mouse device available.");
            using (StateEvent.From(mouse, out var eventPtr))
            {
                mouse.position.WriteValueIntoEvent(screenPos, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        private static void InjectMouseDown(int button)
        {
            var mouse = Mouse.current;
            if (mouse == null)
                throw new MCPException("No mouse device available.");
            var control = GetMouseButtonControl(mouse, button);
            using (StateEvent.From(mouse, out var eventPtr))
            {
                control.WriteValueIntoEvent(1f, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
            _heldMouseButtons.Add(button);
        }

        private static void InjectMouseUp(int button)
        {
            var mouse = Mouse.current;
            if (mouse == null)
                throw new MCPException("No mouse device available.");
            var control = GetMouseButtonControl(mouse, button);
            using (StateEvent.From(mouse, out var eventPtr))
            {
                control.WriteValueIntoEvent(0f, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
            _heldMouseButtons.Remove(button);
        }

        private static UnityEngine.InputSystem.Controls.ButtonControl GetMouseButtonControl(Mouse mouse, int button)
        {
            switch (button)
            {
                case 0: return mouse.leftButton;
                case 1: return mouse.rightButton;
                case 2: return mouse.middleButton;
                default: throw MCPException.InvalidParams($"Invalid mouse button index: {button}");
            }
        }

        private static void ReleaseAllHeld()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                foreach (var key in _heldKeys.ToList())
                {
                    try { QueueKeyEvent(keyboard, key, false); } catch { }
                }
            }
            _heldKeys.Clear();

            var mouse = Mouse.current;
            if (mouse != null)
            {
                foreach (var btn in _heldMouseButtons.ToList())
                {
                    try
                    {
                        var control = GetMouseButtonControl(mouse, btn);
                        using (StateEvent.From(mouse, out var eventPtr))
                        {
                            control.WriteValueIntoEvent(0f, eventPtr);
                            InputSystem.QueueEvent(eventPtr);
                        }
                    }
                    catch { }
                }
            }
            _heldMouseButtons.Clear();
        }

        private static void ScheduleRelease(Action releaseAction, int frames)
        {
            int remaining = frames;
            void Tick()
            {
                if (--remaining <= 0)
                {
                    EditorApplication.update -= Tick;
                    try { releaseAction(); } catch { }
                }
            }
            EditorApplication.update += Tick;
        }

        #endregion

        #region Key / Button Parsing

        private static readonly Dictionary<string, Key> _keyAliases = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            { "space", Key.Space }, { "spacebar", Key.Space },
            { "enter", Key.Enter }, { "return", Key.Enter },
            { "esc", Key.Escape }, { "escape", Key.Escape },
            { "tab", Key.Tab },
            { "backspace", Key.Backspace },
            { "delete", Key.Delete }, { "del", Key.Delete },
            { "up", Key.UpArrow }, { "down", Key.DownArrow },
            { "left", Key.LeftArrow }, { "right", Key.RightArrow },
            { "lshift", Key.LeftShift }, { "rshift", Key.RightShift },
            { "leftshift", Key.LeftShift }, { "rightshift", Key.RightShift },
            { "lctrl", Key.LeftCtrl }, { "rctrl", Key.RightCtrl },
            { "leftctrl", Key.LeftCtrl }, { "rightctrl", Key.RightCtrl },
            { "lalt", Key.LeftAlt }, { "ralt", Key.RightAlt },
            { "leftalt", Key.LeftAlt }, { "rightalt", Key.RightAlt },
            { "0", Key.Digit0 }, { "1", Key.Digit1 }, { "2", Key.Digit2 },
            { "3", Key.Digit3 }, { "4", Key.Digit4 }, { "5", Key.Digit5 },
            { "6", Key.Digit6 }, { "7", Key.Digit7 }, { "8", Key.Digit8 },
            { "9", Key.Digit9 },
            { "f1", Key.F1 }, { "f2", Key.F2 }, { "f3", Key.F3 },
            { "f4", Key.F4 }, { "f5", Key.F5 }, { "f6", Key.F6 },
            { "f7", Key.F7 }, { "f8", Key.F8 }, { "f9", Key.F9 },
            { "f10", Key.F10 }, { "f11", Key.F11 }, { "f12", Key.F12 },
        };

        private static Key ParseKey(string keyName)
        {
            if (string.IsNullOrEmpty(keyName))
                throw MCPException.InvalidParams("'key' parameter is required for key actions.");

            keyName = keyName.Trim();

            // Check aliases first
            if (_keyAliases.TryGetValue(keyName, out var aliasKey))
                return aliasKey;

            // Try direct enum parse (handles W, A, S, D, Space, LeftShift, etc.)
            if (Enum.TryParse<Key>(keyName, ignoreCase: true, out var parsedKey) && parsedKey != Key.None)
                return parsedKey;

            // Single character → letter key
            if (keyName.Length == 1 && char.IsLetter(keyName[0]))
            {
                string enumName = keyName.ToUpper();
                if (Enum.TryParse<Key>(enumName, out var letterKey) && letterKey != Key.None)
                    return letterKey;
            }

            throw MCPException.InvalidParams(
                $"Unknown key '{keyName}'. Use key names like: space, w, a, s, d, up, down, left, right, " +
                "enter, escape, tab, lshift, lctrl, lalt, f1-f12, 0-9, or any Key enum value.");
        }

        private static int ParseMouseButton(string buttonName)
        {
            if (string.IsNullOrEmpty(buttonName) || buttonName.Equals("left", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (buttonName.Equals("right", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (buttonName.Equals("middle", StringComparison.OrdinalIgnoreCase))
                return 2;
            throw MCPException.InvalidParams($"Unknown mouse button '{buttonName}'. Use: left, right, middle.");
        }

        private static string MouseButtonName(int btn)
        {
            switch (btn)
            {
                case 0: return "left";
                case 1: return "right";
                case 2: return "middle";
                default: return btn.ToString();
            }
        }

        private static List<string> GetHeldKeyNames()
        {
            return _heldKeys.Select(k => k.ToString()).ToList();
        }

        private static List<string> GetHeldButtonNames()
        {
            return _heldMouseButtons.Select(b => MouseButtonName(b)).ToList();
        }

        #endregion

        #region Mouse Position Resolution

        private static Vector2 ResolveMousePosition(double x, double y, string targetObject)
        {
            if (!string.IsNullOrEmpty(targetObject))
            {
                var go = GameObject.Find(targetObject);
                if (go == null)
                    throw MCPException.InvalidParams($"GameObject '{targetObject}' not found.");

                var cam = Camera.main;
                if (cam == null)
                    throw new MCPException("No main camera found for screen position calculation.");

                Vector3 screenPos = cam.WorldToScreenPoint(go.transform.position);
                if (screenPos.z < 0)
                    throw new MCPException($"GameObject '{targetObject}' is behind the camera.");

                return new Vector2(screenPos.x, screenPos.y);
            }

            if (double.IsNaN(x) || double.IsNaN(y))
                throw MCPException.InvalidParams("Provide x+y screen coordinates or target_object for mouse actions.");

            return new Vector2((float)x, (float)y);
        }

        #endregion

        #region Sequence Execution

        private static object StartSequence(object stepsRaw)
        {
            RequirePlayMode();

            if (stepsRaw == null)
                throw MCPException.InvalidParams("'steps' parameter is required for sequence action.");

            var parsedSteps = ParseSteps(stepsRaw);
            if (parsedSteps.Count == 0)
                throw MCPException.InvalidParams("'steps' array is empty.");

            var existingJob = InputSimJobManager.CurrentJob;
            if (existingJob != null && existingJob.status == InputSimJobStatus.Running)
                return new { success = false, error = "A sequence is already running. Cancel it first or wait for completion.",
                             job_id = existingJob.jobId };

            var job = InputSimJobManager.StartJob(parsedSteps.Count);
            if (job == null)
                return new { success = false, error = "Failed to create sequence job." };

            ExecuteSequence(job, parsedSteps);

            return new
            {
                success = true,
                message = $"Sequence started with {parsedSteps.Count} steps. Poll with get_job.",
                job_id = job.jobId,
                total_steps = parsedSteps.Count,
                status = "running"
            };
        }

        private static void ExecuteSequence(InputSimJob job, List<SequenceStep> steps)
        {
            int stepIndex = 0;
            double nextStepTime = EditorApplication.timeSinceStartup + steps[0].delayMs / 1000.0;

            void OnUpdate()
            {
                // Abort if left play mode
                if (!EditorApplication.isPlaying)
                {
                    ReleaseAllHeld();
                    job.status = InputSimJobStatus.Failed;
                    job.error = "Exited play mode during sequence.";
                    job.finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    EditorApplication.update -= OnUpdate;
                    InputSimJobManager.Save();
                    return;
                }

                // Check cancellation
                if (job.status == InputSimJobStatus.Cancelled)
                {
                    ReleaseAllHeld();
                    EditorApplication.update -= OnUpdate;
                    return;
                }

                // Wait for delay
                if (EditorApplication.timeSinceStartup < nextStepTime)
                    return;

                // Execute current step
                try
                {
                    var step = steps[stepIndex];
                    ExecuteSingleStep(step);
                    job.completedSteps = stepIndex + 1;
                    job.currentAction = $"{step.action} {step.key ?? step.mouseButton ?? ""}".Trim();
                }
                catch (Exception ex)
                {
                    ReleaseAllHeld();
                    job.status = InputSimJobStatus.Failed;
                    job.error = $"Step {stepIndex} failed: {ex.Message}";
                    job.finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    EditorApplication.update -= OnUpdate;
                    InputSimJobManager.Save();
                    return;
                }

                stepIndex++;
                if (stepIndex >= steps.Count)
                {
                    job.status = InputSimJobStatus.Completed;
                    job.finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    EditorApplication.update -= OnUpdate;
                    InputSimJobManager.Save();
                    return;
                }

                nextStepTime = EditorApplication.timeSinceStartup + steps[stepIndex].delayMs / 1000.0;
                InputSimJobManager.Save();
            }

            EditorApplication.update += OnUpdate;
        }

        private static void ExecuteSingleStep(SequenceStep step)
        {
            switch (step.action.ToLower())
            {
                case "key_down":
                    InjectKeyDown(ParseKey(step.key));
                    break;
                case "key_up":
                    InjectKeyUp(ParseKey(step.key));
                    break;
                case "key_tap":
                    var k = ParseKey(step.key);
                    InjectKeyDown(k);
                    ScheduleRelease(() => { if (EditorApplication.isPlaying) InjectKeyUp(k); },
                        step.holdFrames > 0 ? step.holdFrames : 2);
                    break;
                case "mouse_move":
                    InjectMouseMove(ResolveMousePosition(step.x, step.y, step.targetObject));
                    break;
                case "mouse_down":
                {
                    int btn = ParseMouseButton(step.mouseButton);
                    if (!double.IsNaN(step.x) || !double.IsNaN(step.y) || !string.IsNullOrEmpty(step.targetObject))
                        InjectMouseMove(ResolveMousePosition(step.x, step.y, step.targetObject));
                    InjectMouseDown(btn);
                    break;
                }
                case "mouse_up":
                    InjectMouseUp(ParseMouseButton(step.mouseButton));
                    break;
                case "mouse_click":
                {
                    int btn = ParseMouseButton(step.mouseButton);
                    if (!double.IsNaN(step.x) || !double.IsNaN(step.y) || !string.IsNullOrEmpty(step.targetObject))
                        InjectMouseMove(ResolveMousePosition(step.x, step.y, step.targetObject));
                    InjectMouseDown(btn);
                    int capturedBtn = btn;
                    ScheduleRelease(() => { if (EditorApplication.isPlaying) InjectMouseUp(capturedBtn); },
                        step.holdFrames > 0 ? step.holdFrames : 2);
                    break;
                }
                default:
                    throw new MCPException($"Unknown sequence step action: '{step.action}'");
            }
        }

        #endregion

        #region Step Parsing

        private class SequenceStep
        {
            public string action;
            public string key;
            public string mouseButton;
            public double x = double.NaN;
            public double y = double.NaN;
            public string targetObject;
            public int holdFrames;
            public int delayMs;
        }

        private static List<SequenceStep> ParseSteps(object stepsRaw)
        {
            List<object> stepList;

            // Handle string input (JSON arrays may arrive as strings via MCP)
            if (stepsRaw is string stepsStr)
            {
                stepsStr = stepsStr.Trim();
                if (!stepsStr.StartsWith("["))
                    throw MCPException.InvalidParams("'steps' must be a JSON array.");

                try
                {
                    var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(stepsStr);
                    stepList = parsed.Cast<object>().ToList();
                }
                catch (Exception ex)
                {
                    throw MCPException.InvalidParams($"Failed to parse 'steps' JSON: {ex.Message}");
                }
            }
            else if (stepsRaw is List<object> list)
            {
                stepList = list;
            }
            else
            {
                throw MCPException.InvalidParams($"'steps' must be an array. Got: {stepsRaw.GetType().Name}");
            }

            var result = new List<SequenceStep>();
            for (int i = 0; i < stepList.Count; i++)
            {
                var item = stepList[i];
                if (!(item is Dictionary<string, object> dict))
                    throw MCPException.InvalidParams($"Step {i} must be an object, got {item?.GetType().Name ?? "null"}.");

                var step = new SequenceStep();

                if (!dict.TryGetValue("action", out var actionVal) || !(actionVal is string actionStr))
                    throw MCPException.InvalidParams($"Step {i}: 'action' is required and must be a string.");
                step.action = actionStr;

                if (dict.TryGetValue("key", out var keyVal))
                    step.key = keyVal?.ToString();
                if (dict.TryGetValue("mouse_button", out var mbVal))
                    step.mouseButton = mbVal?.ToString();
                if (dict.TryGetValue("target_object", out var toVal))
                    step.targetObject = toVal?.ToString();

                if (dict.TryGetValue("x", out var xVal))
                    step.x = ConvertToDouble(xVal);
                if (dict.TryGetValue("y", out var yVal))
                    step.y = ConvertToDouble(yVal);
                if (dict.TryGetValue("hold_frames", out var hfVal))
                    step.holdFrames = (int)ConvertToDouble(hfVal);
                if (dict.TryGetValue("delay_ms", out var delayVal))
                    step.delayMs = (int)ConvertToDouble(delayVal);

                result.Add(step);
            }

            return result;
        }

        private static double ConvertToDouble(object val)
        {
            if (val is double d) return d;
            if (val is long l) return l;
            if (val is int i) return i;
            if (val is float f) return f;
            if (val is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return double.NaN;
        }

        #endregion

        #region Job Management

        private static object GetJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                throw MCPException.InvalidParams("'job_id' is required for get_job action.");

            var job = InputSimJobManager.GetJob(jobId);
            if (job == null)
                return new { success = false, error = $"No sequence job found with ID '{jobId}'." };

            return new
            {
                success = true,
                job_id = job.jobId,
                status = job.status.ToString().ToLowerInvariant(),
                total_steps = job.totalSteps,
                completed_steps = job.completedSteps,
                current_action = job.currentAction,
                error = job.error,
                duration_ms = job.finishedUnixMs > 0
                    ? job.finishedUnixMs - job.startedUnixMs
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - job.startedUnixMs
            };
        }

        private static object CancelJob(string jobId)
        {
            var job = string.IsNullOrEmpty(jobId)
                ? InputSimJobManager.CurrentJob
                : InputSimJobManager.GetJob(jobId);

            if (job == null)
                return new { success = false, error = "No sequence job found to cancel." };

            if (job.status == InputSimJobStatus.Running)
            {
                job.status = InputSimJobStatus.Cancelled;
                job.error = "Cancelled by user.";
                job.finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                ReleaseAllHeld();
                InputSimJobManager.Save();
            }

            return new { success = true, job_id = job.jobId, status = job.status.ToString().ToLowerInvariant() };
        }

        #endregion
    }

    #region InputSim Job Manager

    public enum InputSimJobStatus
    {
        Running,
        Completed,
        Cancelled,
        Failed
    }

    [Serializable]
    public class InputSimJob
    {
        public string jobId;
        public InputSimJobStatus status;
        public long startedUnixMs;
        public long finishedUnixMs;
        public int totalSteps;
        public int completedSteps;
        public string currentAction;
        public string error;
    }

    [Serializable]
    internal class InputSimJobStorage
    {
        public List<InputSimJob> jobs = new List<InputSimJob>();
    }

    [InitializeOnLoad]
    public static class InputSimJobManager
    {
        private const string SessionStateKey = "UnixxtyMCP.InputSimJobManager.Jobs";
        private const int MaxJobsToKeep = 5;

        private static InputSimJob _currentJob;
        private static InputSimJobStorage _storage;

        static InputSimJobManager()
        {
            LoadFromSessionState();

            // Clean up on play mode exit
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    // Release all held inputs
                    try { SimulateInput.Execute("release_all"); } catch { }

                    // Cancel running sequence
                    if (_currentJob != null && _currentJob.status == InputSimJobStatus.Running)
                    {
                        _currentJob.status = InputSimJobStatus.Cancelled;
                        _currentJob.error = "Play mode exited.";
                        _currentJob.finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        Save();
                    }
                }
            };
        }

        public static InputSimJob CurrentJob
        {
            get
            {
                EnsureInitialized();
                return _currentJob;
            }
        }

        public static InputSimJob StartJob(int totalSteps)
        {
            EnsureInitialized();

            if (_currentJob != null && _currentJob.status == InputSimJobStatus.Running)
            {
                // Check for orphaned job (>5 min)
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _currentJob.startedUnixMs > 300000)
                {
                    _currentJob.status = InputSimJobStatus.Failed;
                    _currentJob.error = "Job timed out (orphaned).";
                    _currentJob.finishedUnixMs = now;
                }
                else
                {
                    return null;
                }
            }

            _currentJob = new InputSimJob
            {
                jobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                status = InputSimJobStatus.Running,
                startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                totalSteps = totalSteps
            };

            _storage.jobs.Add(_currentJob);
            Save();
            return _currentJob;
        }

        public static InputSimJob GetJob(string jobId)
        {
            EnsureInitialized();
            return _storage.jobs.FirstOrDefault(j => j.jobId == jobId);
        }

        public static void Save()
        {
            if (_storage == null) _storage = new InputSimJobStorage();

            while (_storage.jobs.Count > MaxJobsToKeep)
            {
                var oldest = _storage.jobs
                    .Where(j => j.status != InputSimJobStatus.Running)
                    .OrderBy(j => j.startedUnixMs)
                    .FirstOrDefault();
                if (oldest != null) _storage.jobs.Remove(oldest);
                else break;
            }

            string json = JsonUtility.ToJson(_storage);
            SessionState.SetString(SessionStateKey, json);
        }

        private static void EnsureInitialized()
        {
            if (_storage == null)
                LoadFromSessionState();
        }

        private static void LoadFromSessionState()
        {
            string json = SessionState.GetString(SessionStateKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { _storage = JsonUtility.FromJson<InputSimJobStorage>(json); }
                catch { _storage = new InputSimJobStorage(); }
            }
            else
            {
                _storage = new InputSimJobStorage();
            }

            _currentJob = _storage.jobs.FirstOrDefault(j => j.status == InputSimJobStatus.Running);
        }
    }

    #endregion
}
