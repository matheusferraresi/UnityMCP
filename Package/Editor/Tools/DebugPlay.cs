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
    /// Automated play mode testing. Enter play mode, wait for a condition,
    /// capture state (screenshot, console, component values), return structured snapshot.
    /// Closes the agent's feedback loop: edit → test → verify → iterate.
    /// </summary>
    public static class DebugPlay
    {
        [MCPTool("debug_play", "Automated play mode testing: enter play, wait, capture state, return snapshot. Closes the AI feedback loop.",
            Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action: start (begin test), get_status (poll), stop (end test)",
                required: true, Enum = new[] { "start", "get_status", "stop" })] string action,
            [MCPParam("job_id", "Job ID (required for get_status/stop)")] string jobId = null,
            [MCPParam("wait_seconds", "How long to run before capturing (default: 3)", Minimum = 0, Maximum = 60)] float waitSeconds = 3f,
            [MCPParam("wait_for_log", "Stop when this message appears in console")] string waitForLog = null,
            [MCPParam("capture_screenshot", "Take screenshot on completion (default: true)")] bool captureScreenshot = true,
            [MCPParam("capture_console", "Return console output (default: true)")] bool captureConsole = true,
            [MCPParam("inspect_objects", "Comma-separated list of GameObject paths to inspect (e.g. 'Player,Main Camera')")] string inspectObjects = null,
            [MCPParam("auto_stop", "Exit play mode when done (default: false)")] bool autoStop = false)
        {
            switch (action.ToLower())
            {
                case "start":
                    return StartDebugSession(waitSeconds, waitForLog, captureScreenshot, captureConsole, inspectObjects, autoStop);
                case "get_status":
                    return GetStatus(jobId);
                case "stop":
                    return StopDebugSession(jobId);
                default:
                    throw MCPException.InvalidParams($"Unknown action '{action}'.");
            }
        }

        private static object StartDebugSession(float waitSeconds, string waitForLog,
            bool captureScreenshot, bool captureConsole, string inspectObjects, bool autoStop)
        {
            // If already in play mode, capture immediately
            if (EditorApplication.isPlaying)
            {
                var job = DebugPlayJobManager.StartJob(waitSeconds, waitForLog, captureScreenshot, captureConsole, inspectObjects, autoStop);
                if (job == null)
                    return new { success = false, error = "A debug session is already running." };

                // Already playing - schedule capture after wait
                ScheduleCapture(job);

                return new
                {
                    success = true,
                    message = $"Debug session started (already in play mode). Will capture in {waitSeconds}s.",
                    job_id = job.jobId,
                    status = "running"
                };
            }

            // Enter play mode first
            if (EditorApplication.isCompiling)
                return new { success = false, error = "Cannot enter play mode while compiling." };

            var newJob = DebugPlayJobManager.StartJob(waitSeconds, waitForLog, captureScreenshot, captureConsole, inspectObjects, autoStop);
            if (newJob == null)
                return new { success = false, error = "A debug session is already running." };

            // Enter play mode, then schedule capture
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.isPlaying = true;

            return new
            {
                success = true,
                message = $"Entering play mode. Will capture after {waitSeconds}s. Poll with get_status.",
                job_id = newJob.jobId,
                status = "entering_play_mode"
            };
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                var job = DebugPlayJobManager.CurrentJob;
                if (job != null && job.status == DebugPlayStatus.Running)
                {
                    ScheduleCapture(job);
                }
            }
        }

        private static void ScheduleCapture(DebugPlayJob job)
        {
            // Subscribe to console logs if waiting for specific message
            if (!string.IsNullOrEmpty(job.waitForLog))
            {
                Application.logMessageReceived += (message, stackTrace, type) =>
                {
                    if (job.status == DebugPlayStatus.Running && message.Contains(job.waitForLog))
                    {
                        CaptureAndFinish(job);
                    }
                };
            }

            // Schedule time-based capture
            double captureTime = EditorApplication.timeSinceStartup + job.waitSeconds;
            EditorApplication.update += CheckCapture;

            void CheckCapture()
            {
                if (job.status != DebugPlayStatus.Running)
                {
                    EditorApplication.update -= CheckCapture;
                    return;
                }

                if (EditorApplication.timeSinceStartup >= captureTime)
                {
                    EditorApplication.update -= CheckCapture;
                    CaptureAndFinish(job);
                }
            }
        }

        private static void CaptureAndFinish(DebugPlayJob job)
        {
            if (job.status != DebugPlayStatus.Running)
                return;

            // Capture screenshot
            if (job.captureScreenshot)
            {
                try
                {
                    var cameras = Camera.allCameras;
                    var camera = Camera.main ?? (cameras.Length > 0 ? cameras[0] : null);

                    if (camera != null)
                    {
                        int w = 640, h = 480;
                        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                        var prevRT = camera.targetTexture;
                        camera.targetTexture = rt;
                        camera.Render();
                        camera.targetTexture = prevRT;

                        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                        var prevActive = RenderTexture.active;
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prevActive;

                        byte[] png = tex.EncodeToPNG();
                        job.screenshotBase64 = Convert.ToBase64String(png);
                        job.screenshotWidth = w;
                        job.screenshotHeight = h;

                        UnityEngine.Object.DestroyImmediate(tex);
                        UnityEngine.Object.DestroyImmediate(rt);
                    }
                }
                catch (Exception ex)
                {
                    job.screenshotError = ex.Message;
                }
            }

            // Capture console
            if (job.captureConsole)
            {
                job.consoleLogs = GetRecentConsoleLogs(50);
            }

            // Inspect objects
            if (!string.IsNullOrEmpty(job.inspectObjectPaths))
            {
                job.inspectedObjects = InspectGameObjects(job.inspectObjectPaths);
            }

            job.status = DebugPlayStatus.Completed;
            job.finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            DebugPlayJobManager.Save();

            // Auto-stop play mode if requested
            if (job.autoStop && EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
            }
        }

        private static object GetStatus(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                throw MCPException.InvalidParams("'job_id' is required for get_status.");

            var job = DebugPlayJobManager.GetJob(jobId);
            if (job == null)
                return new { success = false, error = $"No debug session found with ID '{jobId}'." };

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["job_id"] = job.jobId,
                ["status"] = job.status.ToString().ToLowerInvariant(),
                ["is_playing"] = EditorApplication.isPlaying,
                ["duration_ms"] = job.finishedUnixMs > 0
                    ? job.finishedUnixMs - job.startedUnixMs
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - job.startedUnixMs
            };

            if (job.status == DebugPlayStatus.Completed)
            {
                if (job.screenshotBase64 != null)
                {
                    result["screenshot"] = new
                    {
                        width = job.screenshotWidth,
                        height = job.screenshotHeight,
                        base64 = job.screenshotBase64
                    };
                }
                if (job.screenshotError != null)
                    result["screenshot_error"] = job.screenshotError;

                if (job.consoleLogs != null)
                    result["console"] = job.consoleLogs;

                if (job.inspectedObjects != null)
                    result["inspected_objects"] = job.inspectedObjects;
            }

            return result;
        }

        private static object StopDebugSession(string jobId)
        {
            var job = string.IsNullOrEmpty(jobId)
                ? DebugPlayJobManager.CurrentJob
                : DebugPlayJobManager.GetJob(jobId);

            if (job != null && job.status == DebugPlayStatus.Running)
            {
                CaptureAndFinish(job);
            }

            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
            }

            return new
            {
                success = true,
                message = "Debug session stopped.",
                job_id = job?.jobId
            };
        }

        #region Console Log Capture

        private static List<object> GetRecentConsoleLogs(int limit)
        {
            var logs = new List<object>();
            try
            {
                var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntriesType == null || logEntryType == null) return logs;

                var startMethod = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var endMethod = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public);
                var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);

                if (startMethod == null || endMethod == null || getCountMethod == null || getEntryMethod == null)
                    return logs;

                var modeField = logEntryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public);
                var messageField = logEntryType.GetField("message", BindingFlags.Instance | BindingFlags.Public);

                startMethod.Invoke(null, null);
                try
                {
                    int count = (int)getCountMethod.Invoke(null, null);
                    var logEntry = Activator.CreateInstance(logEntryType);

                    int startIdx = Math.Max(0, count - limit);
                    for (int i = startIdx; i < count; i++)
                    {
                        getEntryMethod.Invoke(null, new object[] { i, logEntry });
                        int mode = (int)modeField.GetValue(logEntry);
                        string message = (string)messageField.GetValue(logEntry);

                        string firstLine = message;
                        int nlIdx = message?.IndexOf('\n') ?? -1;
                        if (nlIdx >= 0) firstLine = message.Substring(0, nlIdx);

                        // Determine log type from mode flags
                        string type = "log";
                        if ((mode & (1 << 0)) != 0) type = "error";      // Error
                        else if ((mode & (1 << 1)) != 0) type = "assert"; // Assert
                        else if ((mode & (1 << 2)) != 0) type = "log";    // Log
                        else if ((mode & (1 << 5)) != 0) type = "warning"; // Warning
                        else if ((mode & (1 << 9)) != 0) type = "exception"; // Exception
                        else if ((mode & (1 << 11)) != 0) type = "compile_error";
                        else if ((mode & (1 << 12)) != 0) type = "compile_warning";

                        logs.Add(new
                        {
                            type,
                            message = firstLine?.Length > 300 ? firstLine.Substring(0, 300) + "..." : firstLine
                        });
                    }
                }
                finally
                {
                    endMethod.Invoke(null, null);
                }
            }
            catch { }
            return logs;
        }

        #endregion

        #region Object Inspection

        private static Dictionary<string, object> InspectGameObjects(string paths)
        {
            var result = new Dictionary<string, object>();

            foreach (var path in paths.Split(','))
            {
                string trimmed = path.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var go = GameObject.Find(trimmed);
                if (go == null)
                {
                    result[trimmed] = new { error = "GameObject not found" };
                    continue;
                }

                var data = new Dictionary<string, object>
                {
                    ["active"] = go.activeSelf,
                    ["position"] = go.transform.position.ToString("F2"),
                    ["rotation"] = go.transform.eulerAngles.ToString("F1")
                };

                // Get serialized fields from all components
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    var compType = comp.GetType();
                    if (compType == typeof(Transform)) continue; // Already captured

                    var compData = new Dictionary<string, object>();
                    var fields = compType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var field in fields)
                    {
                        try
                        {
                            var val = field.GetValue(comp);
                            compData[field.Name] = val?.ToString() ?? "null";
                        }
                        catch { }
                    }

                    // Also get serialized private fields
                    var privateFields = compType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var field in privateFields)
                    {
                        if (field.IsDefined(typeof(SerializeField), false))
                        {
                            try
                            {
                                var val = field.GetValue(comp);
                                compData[field.Name] = val?.ToString() ?? "null";
                            }
                            catch { }
                        }
                    }

                    if (compData.Count > 0)
                        data[$"{compType.Name}"] = compData;
                }

                result[trimmed] = data;
            }

            return result;
        }

        #endregion
    }

    #region DebugPlay Job Manager

    public enum DebugPlayStatus
    {
        Running,
        Completed
    }

    [Serializable]
    public class DebugPlayJob
    {
        public string jobId;
        public DebugPlayStatus status;
        public long startedUnixMs;
        public long finishedUnixMs;
        public float waitSeconds;
        public string waitForLog;
        public bool captureScreenshot;
        public bool captureConsole;
        public string inspectObjectPaths;
        public bool autoStop;

        // Results (non-serialized to SessionState due to size - held in memory)
        [NonSerialized] public string screenshotBase64;
        [NonSerialized] public int screenshotWidth;
        [NonSerialized] public int screenshotHeight;
        [NonSerialized] public string screenshotError;
        [NonSerialized] public List<object> consoleLogs;
        [NonSerialized] public Dictionary<string, object> inspectedObjects;
    }

    [Serializable]
    internal class DebugPlayJobStorage
    {
        public List<DebugPlayJob> jobs = new List<DebugPlayJob>();
    }

    public static class DebugPlayJobManager
    {
        private const string SessionStateKey = "UnixxtyMCP.DebugPlayJobManager.Jobs";
        private static DebugPlayJob _currentJob;
        private static DebugPlayJobStorage _storage;

        // Keep results in memory (too large for SessionState)
        private static readonly Dictionary<string, DebugPlayJob> _jobResults = new Dictionary<string, DebugPlayJob>();

        public static DebugPlayJob CurrentJob => _currentJob;

        public static DebugPlayJob StartJob(float waitSeconds, string waitForLog,
            bool captureScreenshot, bool captureConsole, string inspectObjects, bool autoStop)
        {
            EnsureInitialized();

            if (_currentJob != null && _currentJob.status == DebugPlayStatus.Running)
            {
                // Check if orphaned (>2 min)
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _currentJob.startedUnixMs > 120000)
                    _currentJob.status = DebugPlayStatus.Completed;
                else
                    return null;
            }

            _currentJob = new DebugPlayJob
            {
                jobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                status = DebugPlayStatus.Running,
                startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                waitSeconds = waitSeconds,
                waitForLog = waitForLog,
                captureScreenshot = captureScreenshot,
                captureConsole = captureConsole,
                inspectObjectPaths = inspectObjects,
                autoStop = autoStop
            };

            _storage.jobs.Add(_currentJob);
            _jobResults[_currentJob.jobId] = _currentJob;
            Save();

            return _currentJob;
        }

        public static DebugPlayJob GetJob(string jobId)
        {
            // Try in-memory first (has results)
            if (_jobResults.TryGetValue(jobId, out var job))
                return job;

            // Fall back to storage (no results - they were lost on domain reload)
            EnsureInitialized();
            return _storage.jobs.FirstOrDefault(j => j.jobId == jobId);
        }

        public static void Save()
        {
            if (_storage == null) return;

            while (_storage.jobs.Count > 5)
            {
                var oldest = _storage.jobs
                    .Where(j => j.status != DebugPlayStatus.Running)
                    .OrderBy(j => j.startedUnixMs)
                    .FirstOrDefault();
                if (oldest != null)
                {
                    _storage.jobs.Remove(oldest);
                    _jobResults.Remove(oldest.jobId);
                }
                else break;
            }

            string json = JsonUtility.ToJson(_storage);
            SessionState.SetString(SessionStateKey, json);
        }

        private static void EnsureInitialized()
        {
            if (_storage != null) return;

            string json = SessionState.GetString(SessionStateKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { _storage = JsonUtility.FromJson<DebugPlayJobStorage>(json); }
                catch { _storage = new DebugPlayJobStorage(); }
            }
            else
            {
                _storage = new DebugPlayJobStorage();
            }

            _currentJob = _storage.jobs.FirstOrDefault(j => j.status == DebugPlayStatus.Running);
        }
    }

    #endregion
}
