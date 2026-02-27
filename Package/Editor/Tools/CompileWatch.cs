using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Services;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Async compilation pipeline with structured error feedback.
    /// Triggers compilation, watches for errors, reports structured results.
    /// Agent polls for status like tests_get_job.
    /// </summary>
    public static class CompileWatch
    {
        [MCPTool("compile_and_watch", "Trigger script compilation and watch for results. Returns structured errors/warnings with file, line, column.",
            Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action: start (trigger compilation), get_job (poll status)",
                required: true, Enum = new[] { "start", "get_job" })] string action,
            [MCPParam("job_id", "Job ID to poll (required for get_job)")] string jobId = null)
        {
            switch (action.ToLower())
            {
                case "start":
                    return StartCompilation();
                case "get_job":
                    return GetJob(jobId);
                default:
                    throw MCPException.InvalidParams($"Unknown action '{action}'.");
            }
        }

        private static object StartCompilation()
        {
            if (EditorApplication.isCompiling)
            {
                // Already compiling - check if we have an active job
                var existing = CompileJobManager.CurrentJob;
                if (existing != null)
                {
                    return new
                    {
                        success = true,
                        message = "Compilation already in progress.",
                        job_id = existing.jobId,
                        status = "compiling"
                    };
                }
            }

            if (TestJobManager.IsRunning)
            {
                return new
                {
                    success = false,
                    error = "Cannot compile while tests are running."
                };
            }

            var job = CompileJobManager.StartJob();
            if (job == null)
            {
                return new
                {
                    success = false,
                    error = "A compilation job is already running."
                };
            }

            CompilationPipeline.RequestScriptCompilation();

            return new
            {
                success = true,
                message = "Compilation started. Poll with action='get_job' to check status.",
                job_id = job.jobId,
                status = "compiling"
            };
        }

        private static object GetJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                throw MCPException.InvalidParams("'job_id' is required for get_job action.");

            var job = CompileJobManager.GetJob(jobId);
            if (job == null)
            {
                return new
                {
                    success = false,
                    error = $"No compilation job found with ID '{jobId}'."
                };
            }

            return new
            {
                success = true,
                job_id = job.jobId,
                status = job.status.ToString().ToLowerInvariant(),
                duration_ms = job.finishedUnixMs > 0
                    ? job.finishedUnixMs - job.startedUnixMs
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - job.startedUnixMs,
                errors = job.errors.Count > 0 ? (object)job.errors.Select(e => new
                {
                    file = e.file,
                    line = e.line,
                    column = e.column,
                    code = e.code,
                    message = e.message,
                    severity = e.severity
                }).ToList() : null,
                warning_count = job.warningCount,
                assembly_count = job.assemblyCount
            };
        }
    }

    #region CompileJobManager

    public enum CompileJobStatus
    {
        Compiling,
        Succeeded,
        Failed
    }

    [Serializable]
    public class CompileError
    {
        public string file;
        public int line;
        public int column;
        public string code;
        public string message;
        public string severity;
    }

    [Serializable]
    public class CompileJob
    {
        public string jobId;
        public CompileJobStatus status;
        public long startedUnixMs;
        public long finishedUnixMs;
        public List<CompileError> errors = new List<CompileError>();
        public int warningCount;
        public int assemblyCount;
    }

    [Serializable]
    internal class CompileJobStorage
    {
        public List<CompileJob> jobs = new List<CompileJob>();
    }

    [InitializeOnLoad]
    public static class CompileJobManager
    {
        private const string SessionStateKey = "UnityMCP.CompileJobManager.Jobs";
        private const int MaxJobsToKeep = 5;

        private static CompileJob _currentJob;
        private static CompileJobStorage _storage;

        static CompileJobManager()
        {
            LoadFromSessionState();

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
        }

        public static CompileJob CurrentJob
        {
            get
            {
                EnsureInitialized();
                return _currentJob;
            }
        }

        public static CompileJob StartJob()
        {
            EnsureInitialized();

            if (_currentJob != null && _currentJob.status == CompileJobStatus.Compiling)
            {
                // Check for orphaned job (>2 min)
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _currentJob.startedUnixMs > 120000)
                {
                    _currentJob.status = CompileJobStatus.Failed;
                    _currentJob.finishedUnixMs = now;
                    _currentJob.errors.Add(new CompileError
                    {
                        message = "Compilation job timed out (orphaned)",
                        severity = "error"
                    });
                }
                else
                {
                    return null;
                }
            }

            _currentJob = new CompileJob
            {
                jobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                status = CompileJobStatus.Compiling,
                startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _storage.jobs.Add(_currentJob);
            SaveToSessionState();
            return _currentJob;
        }

        public static CompileJob GetJob(string jobId)
        {
            EnsureInitialized();
            return _storage.jobs.FirstOrDefault(j => j.jobId == jobId);
        }

        private static void OnCompilationStarted(object context)
        {
            // If no current job exists (compilation triggered outside of MCP), create one
            if (_currentJob == null || _currentJob.status != CompileJobStatus.Compiling)
            {
                // Don't auto-create - only track MCP-initiated compilations
            }
        }

        private static void OnAssemblyFinished(string assemblyPath, CompilerMessage[] messages)
        {
            EnsureInitialized();
            if (_currentJob == null || _currentJob.status != CompileJobStatus.Compiling)
                return;

            _currentJob.assemblyCount++;

            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Error)
                {
                    _currentJob.errors.Add(ParseCompilerMessage(msg, "error"));
                }
                else if (msg.type == CompilerMessageType.Warning)
                {
                    _currentJob.warningCount++;
                }
            }

            SaveToSessionState();
        }

        private static void OnCompilationFinished(object context)
        {
            EnsureInitialized();
            if (_currentJob == null || _currentJob.status != CompileJobStatus.Compiling)
                return;

            _currentJob.finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _currentJob.status = _currentJob.errors.Count > 0
                ? CompileJobStatus.Failed
                : CompileJobStatus.Succeeded;

            SaveToSessionState();
            _currentJob = null;
        }

        private static CompileError ParseCompilerMessage(CompilerMessage msg, string severity)
        {
            var error = new CompileError
            {
                message = msg.message,
                file = msg.file,
                line = msg.line,
                column = msg.column,
                severity = severity
            };

            // Try to extract error code (e.g. CS1061)
            var match = System.Text.RegularExpressions.Regex.Match(msg.message, @"\b(CS\d{4})\b");
            if (match.Success)
                error.code = match.Groups[1].Value;

            return error;
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
                try
                {
                    _storage = JsonUtility.FromJson<CompileJobStorage>(json);
                }
                catch
                {
                    _storage = new CompileJobStorage();
                }
            }
            else
            {
                _storage = new CompileJobStorage();
            }

            _currentJob = _storage.jobs.FirstOrDefault(j => j.status == CompileJobStatus.Compiling);
        }

        private static void SaveToSessionState()
        {
            if (_storage == null) _storage = new CompileJobStorage();

            while (_storage.jobs.Count > MaxJobsToKeep)
            {
                var oldest = _storage.jobs
                    .Where(j => j.status != CompileJobStatus.Compiling)
                    .OrderBy(j => j.startedUnixMs)
                    .FirstOrDefault();
                if (oldest != null) _storage.jobs.Remove(oldest);
                else break;
            }

            string json = JsonUtility.ToJson(_storage);
            SessionState.SetString(SessionStateKey, json);
        }
    }

    #endregion
}
