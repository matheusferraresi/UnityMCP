using System;
using System.Collections.Concurrent;
using UnityEditor;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Dispatches actions to Unity's main thread using EditorApplication.update.
    /// Works even when Unity is not in focus (unlike delayCall).
    /// </summary>
    [InitializeOnLoad]
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();
        private static volatile bool _isRunning;

        static MainThreadDispatcher()
        {
            Start();
        }

        /// <summary>
        /// Starts the dispatcher. Called automatically on editor load.
        /// </summary>
        public static void Start()
        {
            if (_isRunning) return;

            _isRunning = true;
            EditorApplication.update += ProcessQueue;
        }

        /// <summary>
        /// Stops the dispatcher.
        /// </summary>
        public static void Stop()
        {
            _isRunning = false;
            EditorApplication.update -= ProcessQueue;
        }

        /// <summary>
        /// Enqueues an action to be executed on the main thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            _actionQueue.Enqueue(action);
        }

        private static void ProcessQueue()
        {
            // Process all pending actions
            while (_actionQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[MainThreadDispatcher] Error executing action: {ex}");
                }
            }
        }
    }
}
