using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor.Services
{
    /// <summary>
    /// Metadata for a scene checkpoint, stored alongside the saved scene file.
    /// </summary>
    [Serializable]
    public class CheckpointMetadata
    {
        public string id;
        public string name;
        public DateTime timestamp;
        public string sceneName;
        public string scenePath;
        public int rootObjectCount;
        public int totalObjectCount;
        public List<string> rootObjectNames = new List<string>();

        /// <summary>
        /// Converts metadata to a serializable format for JSON output.
        /// </summary>
        public object ToSerializable()
        {
            return new Dictionary<string, object>
            {
                { "id", id },
                { "name", name },
                { "timestamp", timestamp.ToString("o") },
                { "scene_name", sceneName },
                { "scene_path", scenePath },
                { "root_object_count", rootObjectCount },
                { "total_object_count", totalObjectCount }
            };
        }
    }

    /// <summary>
    /// Result of comparing two checkpoint states, reporting added and removed root objects.
    /// </summary>
    public class CheckpointDiff
    {
        public List<string> addedObjects = new List<string>();
        public List<string> removedObjects = new List<string>();
        public int rootCountA;
        public int rootCountB;
        public int totalCountA;
        public int totalCountB;

        /// <summary>
        /// Converts the diff result to a serializable format for JSON output.
        /// </summary>
        public object ToSerializable()
        {
            var result = new Dictionary<string, object>
            {
                { "root_count_a", rootCountA },
                { "root_count_b", rootCountB },
                { "root_count_diff", rootCountB - rootCountA },
                { "total_count_a", totalCountA },
                { "total_count_b", totalCountB },
                { "total_count_diff", totalCountB - totalCountA }
            };

            if (addedObjects.Count > 0)
            {
                result["added"] = addedObjects;
            }

            if (removedObjects.Count > 0)
            {
                result["removed"] = removedObjects;
            }

            return result;
        }
    }

    /// <summary>
    /// Manages scene checkpoint storage for save/restore/diff operations.
    /// Checkpoints are stored as scene file copies with metadata in a temp directory.
    /// File-based storage survives domain reloads.
    /// </summary>
    public static class CheckpointManager
    {
        #region Constants

        /// <summary>
        /// Maximum number of checkpoints to keep. Oldest are evicted when this limit is exceeded.
        /// </summary>
        private const int MaxCheckpoints = 20;

        private static readonly object _lock = new object();

        #endregion

        #region Properties

        /// <summary>
        /// Gets the directory where checkpoints are stored.
        /// </summary>
        private static string CheckpointDirectory
        {
            get
            {
                string directory = Path.Combine(Path.GetTempPath(), "UnityMCP", "Checkpoints");
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                return directory;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Saves a checkpoint of the current scene state.
        /// Copies the saved scene file and records metadata including root object names.
        /// </summary>
        /// <param name="checkpointName">Optional human-readable name for the checkpoint.</param>
        /// <returns>The metadata for the newly created checkpoint, or null on failure.</returns>
        public static CheckpointMetadata SaveCheckpoint(string checkpointName = null)
        {
            lock (_lock)
            {
                Scene activeScene = EditorSceneManager.GetActiveScene();
                if (!activeScene.IsValid())
                {
                    Debug.LogWarning("[CheckpointManager] Cannot save checkpoint: no valid active scene.");
                    return null;
                }

                // Save current scene state first
                if (!string.IsNullOrEmpty(activeScene.path))
                {
                    EditorSceneManager.SaveScene(activeScene);
                }
                else
                {
                    Debug.LogWarning("[CheckpointManager] Cannot save checkpoint: scene has no path. Save the scene first.");
                    return null;
                }

                // Generate checkpoint ID and paths
                string checkpointId = Guid.NewGuid().ToString("N").Substring(0, 12);
                string checkpointScenePath = Path.Combine(CheckpointDirectory, $"{checkpointId}.unity");
                string checkpointMetadataPath = Path.Combine(CheckpointDirectory, $"{checkpointId}.json");

                // Copy scene file to checkpoint location
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string fullScenePath = Path.Combine(projectRoot, activeScene.path);

                try
                {
                    File.Copy(fullScenePath, checkpointScenePath, overwrite: true);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[CheckpointManager] Failed to copy scene file: {exception.Message}");
                    return null;
                }

                // Gather root object information for diff support
                GameObject[] rootObjects = activeScene.GetRootGameObjects();
                List<string> rootObjectNames = rootObjects
                    .Where(go => go != null)
                    .Select(go => go.name)
                    .ToList();

                int totalObjectCount = 0;
                foreach (GameObject rootObject in rootObjects)
                {
                    if (rootObject != null)
                    {
                        totalObjectCount += rootObject.GetComponentsInChildren<Transform>(includeInactive: true).Length;
                    }
                }

                // Build metadata
                CheckpointMetadata metadata = new CheckpointMetadata
                {
                    id = checkpointId,
                    name = checkpointName ?? $"Checkpoint {DateTime.Now:HH:mm:ss}",
                    timestamp = DateTime.UtcNow,
                    sceneName = activeScene.name,
                    scenePath = activeScene.path,
                    rootObjectCount = rootObjects.Length,
                    totalObjectCount = totalObjectCount,
                    rootObjectNames = rootObjectNames
                };

                // Write metadata JSON
                try
                {
                    string metadataJson = JsonConvert.SerializeObject(metadata, Formatting.Indented);
                    File.WriteAllText(checkpointMetadataPath, metadataJson);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[CheckpointManager] Failed to write metadata: {exception.Message}");
                    // Clean up the scene file since metadata write failed
                    TryDeleteFile(checkpointScenePath);
                    return null;
                }

                // Enforce checkpoint limit (FIFO eviction)
                EnforceCheckpointLimit();

                return metadata;
            }
        }

        /// <summary>
        /// Lists all available checkpoints sorted by timestamp descending (newest first).
        /// </summary>
        /// <returns>List of checkpoint metadata objects.</returns>
        public static List<CheckpointMetadata> ListCheckpoints()
        {
            lock (_lock)
            {
                var checkpoints = new List<CheckpointMetadata>();
                string checkpointDirectory = CheckpointDirectory;

                string[] metadataFiles;
                try
                {
                    metadataFiles = Directory.GetFiles(checkpointDirectory, "*.json");
                }
                catch (Exception)
                {
                    return checkpoints;
                }

                foreach (string metadataFilePath in metadataFiles)
                {
                    try
                    {
                        string json = File.ReadAllText(metadataFilePath);
                        CheckpointMetadata metadata = JsonConvert.DeserializeObject<CheckpointMetadata>(json);
                        if (metadata != null)
                        {
                            // Verify the corresponding scene file still exists
                            string sceneFilePath = Path.Combine(checkpointDirectory, $"{metadata.id}.unity");
                            if (File.Exists(sceneFilePath))
                            {
                                checkpoints.Add(metadata);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"[CheckpointManager] Failed to read metadata file '{metadataFilePath}': {exception.Message}");
                    }
                }

                // Sort by timestamp descending (newest first)
                checkpoints.Sort((a, b) => b.timestamp.CompareTo(a.timestamp));

                return checkpoints;
            }
        }

        /// <summary>
        /// Restores a previously saved checkpoint by copying its scene file back to the original location.
        /// </summary>
        /// <param name="checkpointId">The ID of the checkpoint to restore.</param>
        /// <returns>The metadata of the restored checkpoint, or null on failure.</returns>
        public static CheckpointMetadata RestoreCheckpoint(string checkpointId)
        {
            lock (_lock)
            {
                CheckpointMetadata metadata = GetCheckpointMetadata(checkpointId);
                if (metadata == null)
                {
                    Debug.LogWarning($"[CheckpointManager] Checkpoint not found: {checkpointId}");
                    return null;
                }

                string checkpointScenePath = Path.Combine(CheckpointDirectory, $"{checkpointId}.unity");
                if (!File.Exists(checkpointScenePath))
                {
                    Debug.LogWarning($"[CheckpointManager] Checkpoint scene file not found: {checkpointScenePath}");
                    return null;
                }

                // Resolve the original scene path
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string originalFullPath = Path.Combine(projectRoot, metadata.scenePath);

                try
                {
                    // Copy checkpoint scene file back to the original location
                    File.Copy(checkpointScenePath, originalFullPath, overwrite: true);

                    // Refresh the asset database and reopen the scene
                    AssetDatabase.Refresh();
                    EditorSceneManager.OpenScene(metadata.scenePath, OpenSceneMode.Single);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[CheckpointManager] Failed to restore checkpoint: {exception.Message}");
                    return null;
                }

                return metadata;
            }
        }

        /// <summary>
        /// Compares two checkpoints by their root object names.
        /// If checkpointIdB is null, compares against the current scene state.
        /// </summary>
        /// <param name="checkpointIdA">First checkpoint ID, or "current" for the active scene.</param>
        /// <param name="checkpointIdB">Second checkpoint ID, or "current" for the active scene. Defaults to current scene.</param>
        /// <returns>A diff result showing added and removed root objects, or null on failure.</returns>
        public static CheckpointDiff GetDiff(string checkpointIdA, string checkpointIdB = null)
        {
            lock (_lock)
            {
                // Resolve checkpoint A root object names
                List<string> rootNamesA;
                int totalCountA;
                int rootCountA;

                if (string.Equals(checkpointIdA, "current", StringComparison.OrdinalIgnoreCase))
                {
                    var currentInfo = GetCurrentSceneRootInfo();
                    rootNamesA = currentInfo.rootNames;
                    rootCountA = currentInfo.rootCount;
                    totalCountA = currentInfo.totalCount;
                }
                else
                {
                    CheckpointMetadata metadataA = GetCheckpointMetadata(checkpointIdA);
                    if (metadataA == null)
                    {
                        Debug.LogWarning($"[CheckpointManager] Checkpoint A not found: {checkpointIdA}");
                        return null;
                    }
                    rootNamesA = metadataA.rootObjectNames ?? new List<string>();
                    rootCountA = metadataA.rootObjectCount;
                    totalCountA = metadataA.totalObjectCount;
                }

                // Resolve checkpoint B root object names (default to current scene)
                List<string> rootNamesB;
                int totalCountB;
                int rootCountB;

                bool useCurrentForB = string.IsNullOrEmpty(checkpointIdB) ||
                                      string.Equals(checkpointIdB, "current", StringComparison.OrdinalIgnoreCase);

                if (useCurrentForB)
                {
                    var currentInfo = GetCurrentSceneRootInfo();
                    rootNamesB = currentInfo.rootNames;
                    rootCountB = currentInfo.rootCount;
                    totalCountB = currentInfo.totalCount;
                }
                else
                {
                    CheckpointMetadata metadataB = GetCheckpointMetadata(checkpointIdB);
                    if (metadataB == null)
                    {
                        Debug.LogWarning($"[CheckpointManager] Checkpoint B not found: {checkpointIdB}");
                        return null;
                    }
                    rootNamesB = metadataB.rootObjectNames ?? new List<string>();
                    rootCountB = metadataB.rootObjectCount;
                    totalCountB = metadataB.totalObjectCount;
                }

                // Compute diff using name lists
                HashSet<string> setA = new HashSet<string>(rootNamesA);
                HashSet<string> setB = new HashSet<string>(rootNamesB);

                List<string> addedObjects = rootNamesB.Where(objectName => !setA.Contains(objectName)).ToList();
                List<string> removedObjects = rootNamesA.Where(objectName => !setB.Contains(objectName)).ToList();

                return new CheckpointDiff
                {
                    addedObjects = addedObjects,
                    removedObjects = removedObjects,
                    rootCountA = rootCountA,
                    rootCountB = rootCountB,
                    totalCountA = totalCountA,
                    totalCountB = totalCountB
                };
            }
        }

        /// <summary>
        /// Deletes a checkpoint and its associated files.
        /// </summary>
        /// <param name="checkpointId">The ID of the checkpoint to delete.</param>
        /// <returns>True if the checkpoint was deleted, false if not found or failed.</returns>
        public static bool DeleteCheckpoint(string checkpointId)
        {
            lock (_lock)
            {
                string checkpointScenePath = Path.Combine(CheckpointDirectory, $"{checkpointId}.unity");
                string checkpointMetadataPath = Path.Combine(CheckpointDirectory, $"{checkpointId}.json");

                bool sceneDeleted = TryDeleteFile(checkpointScenePath);
                bool metadataDeleted = TryDeleteFile(checkpointMetadataPath);

                return sceneDeleted || metadataDeleted;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Reads a checkpoint metadata file by ID.
        /// </summary>
        private static CheckpointMetadata GetCheckpointMetadata(string checkpointId)
        {
            string metadataPath = Path.Combine(CheckpointDirectory, $"{checkpointId}.json");
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(metadataPath);
                return JsonConvert.DeserializeObject<CheckpointMetadata>(json);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[CheckpointManager] Failed to read metadata for '{checkpointId}': {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets root object information from the current active scene.
        /// </summary>
        private static (List<string> rootNames, int rootCount, int totalCount) GetCurrentSceneRootInfo()
        {
            Scene activeScene = EditorSceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                return (new List<string>(), 0, 0);
            }

            GameObject[] rootObjects = activeScene.GetRootGameObjects();
            List<string> rootNames = rootObjects
                .Where(go => go != null)
                .Select(go => go.name)
                .ToList();

            int totalCount = 0;
            foreach (GameObject rootObject in rootObjects)
            {
                if (rootObject != null)
                {
                    totalCount += rootObject.GetComponentsInChildren<Transform>(includeInactive: true).Length;
                }
            }

            return (rootNames, rootObjects.Length, totalCount);
        }

        /// <summary>
        /// Enforces the maximum checkpoint limit by deleting the oldest checkpoints.
        /// </summary>
        private static void EnforceCheckpointLimit()
        {
            List<CheckpointMetadata> checkpoints = ListCheckpointsInternal();

            while (checkpoints.Count > MaxCheckpoints)
            {
                // Remove the oldest checkpoint (last in the desc-sorted list)
                CheckpointMetadata oldestCheckpoint = checkpoints[checkpoints.Count - 1];
                DeleteCheckpointFiles(oldestCheckpoint.id);
                checkpoints.RemoveAt(checkpoints.Count - 1);
            }
        }

        /// <summary>
        /// Internal version of ListCheckpoints that does not acquire the lock (caller must hold it).
        /// </summary>
        private static List<CheckpointMetadata> ListCheckpointsInternal()
        {
            var checkpoints = new List<CheckpointMetadata>();
            string checkpointDirectory = CheckpointDirectory;

            string[] metadataFiles;
            try
            {
                metadataFiles = Directory.GetFiles(checkpointDirectory, "*.json");
            }
            catch (Exception)
            {
                return checkpoints;
            }

            foreach (string metadataFilePath in metadataFiles)
            {
                try
                {
                    string json = File.ReadAllText(metadataFilePath);
                    CheckpointMetadata metadata = JsonConvert.DeserializeObject<CheckpointMetadata>(json);
                    if (metadata != null)
                    {
                        string sceneFilePath = Path.Combine(checkpointDirectory, $"{metadata.id}.unity");
                        if (File.Exists(sceneFilePath))
                        {
                            checkpoints.Add(metadata);
                        }
                    }
                }
                catch (Exception)
                {
                    // Skip unreadable metadata files
                }
            }

            // Sort by timestamp descending (newest first)
            checkpoints.Sort((a, b) => b.timestamp.CompareTo(a.timestamp));

            return checkpoints;
        }

        /// <summary>
        /// Deletes checkpoint files without acquiring the lock (caller must hold it).
        /// </summary>
        private static void DeleteCheckpointFiles(string checkpointId)
        {
            string checkpointScenePath = Path.Combine(CheckpointDirectory, $"{checkpointId}.unity");
            string checkpointMetadataPath = Path.Combine(CheckpointDirectory, $"{checkpointId}.json");

            TryDeleteFile(checkpointScenePath);
            TryDeleteFile(checkpointMetadataPath);
        }

        /// <summary>
        /// Attempts to delete a file, returning true if successful or if the file does not exist.
        /// </summary>
        private static bool TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[CheckpointManager] Failed to delete file '{filePath}': {exception.Message}");
                return false;
            }
        }

        #endregion
    }
}
