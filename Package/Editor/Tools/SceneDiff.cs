using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityMCP.Editor;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Tracks scene changes by taking snapshots and computing diffs.
    /// Helps agents understand what changed between operations.
    /// </summary>
    public static class SceneDiff
    {
        private const string SnapshotKey = "UnityMCP_SceneDiffSnapshot";

        [MCPTool("scene_diff", "Track scene changes: take snapshots and diff against previous state to see what was added, modified, or removed", Category = "Scene")]
        public static object Execute(
            [MCPParam("action", "Action: snapshot (save current state), diff (compare against last snapshot), clear (remove stored snapshot)", required: true,
                Enum = new[] { "snapshot", "diff", "clear" })] string action,
            [MCPParam("scene_name", "Scene to snapshot/diff (default: active scene)")] string sceneName = null,
            [MCPParam("include_components", "Include component details in snapshot (default: false, reduces size)")] bool includeComponents = false)
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "snapshot" => TakeSnapshot(sceneName, includeComponents),
                    "diff" => ComputeDiff(sceneName, includeComponents),
                    "clear" => ClearSnapshot(sceneName),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'. Valid: snapshot, diff, clear")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException(-32603, $"Scene diff failed: {ex.Message}");
            }
        }

        private static object TakeSnapshot(string sceneName, bool includeComponents)
        {
            var scene = GetScene(sceneName);
            var snapshot = BuildSnapshot(scene, includeComponents);
            string json = JsonConvert.SerializeObject(snapshot);

            string key = SnapshotKey + "_" + scene.name;
            SessionState.SetString(key, json);

            return new
            {
                success = true,
                scene = scene.name,
                objectCount = snapshot.Count,
                message = $"Snapshot saved for '{scene.name}' ({snapshot.Count} objects). Use action 'diff' to compare after changes."
            };
        }

        private static object ComputeDiff(string sceneName, bool includeComponents)
        {
            var scene = GetScene(sceneName);
            string key = SnapshotKey + "_" + scene.name;
            string previousJson = SessionState.GetString(key, "");

            if (string.IsNullOrEmpty(previousJson))
            {
                return new
                {
                    success = false,
                    message = $"No previous snapshot for '{scene.name}'. Take a snapshot first with action 'snapshot'."
                };
            }

            var previous = JsonConvert.DeserializeObject<List<ObjectSnapshot>>(previousJson);
            var current = BuildSnapshot(scene, includeComponents);

            var prevById = previous.ToDictionary(o => o.instanceId);
            var currById = current.ToDictionary(o => o.instanceId);

            // Find additions
            var added = current.Where(c => !prevById.ContainsKey(c.instanceId))
                .Select(c => new { c.name, c.instanceId, c.path, c.components })
                .ToList();

            // Find removals
            var removed = previous.Where(p => !currById.ContainsKey(p.instanceId))
                .Select(p => new { p.name, p.instanceId, p.path })
                .ToList();

            // Find modifications
            var modified = new List<object>();
            foreach (var curr in current)
            {
                if (!prevById.TryGetValue(curr.instanceId, out var prev)) continue;

                var changes = new List<string>();

                if (prev.name != curr.name) changes.Add($"renamed: '{prev.name}' → '{curr.name}'");
                if (prev.active != curr.active) changes.Add($"active: {prev.active} → {curr.active}");
                if (prev.path != curr.path) changes.Add($"moved: '{prev.path}' → '{curr.path}'");
                if (prev.layer != curr.layer) changes.Add($"layer: {prev.layer} → {curr.layer}");
                if (prev.tag != curr.tag) changes.Add($"tag: '{prev.tag}' → '{curr.tag}'");

                if (!PositionsEqual(prev.position, curr.position))
                    changes.Add($"position: ({prev.position?.x:F2},{prev.position?.y:F2},{prev.position?.z:F2}) → ({curr.position?.x:F2},{curr.position?.y:F2},{curr.position?.z:F2})");
                if (!PositionsEqual(prev.rotation, curr.rotation))
                    changes.Add($"rotation changed");
                if (!PositionsEqual(prev.scale, curr.scale))
                    changes.Add($"scale changed");

                // Component changes
                var prevComps = new HashSet<string>(prev.components ?? new List<string>());
                var currComps = new HashSet<string>(curr.components ?? new List<string>());
                var addedComps = currComps.Except(prevComps).ToList();
                var removedComps = prevComps.Except(currComps).ToList();
                if (addedComps.Count > 0) changes.Add($"components added: {string.Join(", ", addedComps)}");
                if (removedComps.Count > 0) changes.Add($"components removed: {string.Join(", ", removedComps)}");

                if (changes.Count > 0)
                {
                    modified.Add(new { name = curr.name, instanceId = curr.instanceId, changes });
                }
            }

            // Auto-update snapshot
            string newJson = JsonConvert.SerializeObject(current);
            SessionState.SetString(key, newJson);

            return new
            {
                success = true,
                scene = scene.name,
                summary = new
                {
                    added = added.Count,
                    removed = removed.Count,
                    modified = modified.Count,
                    unchanged = current.Count - added.Count - modified.Count
                },
                addedObjects = added.Count > 0 ? added : null,
                removedObjects = removed.Count > 0 ? removed : null,
                modifiedObjects = modified.Count > 0 ? modified : null,
                message = added.Count == 0 && removed.Count == 0 && modified.Count == 0
                    ? "No changes detected."
                    : $"{added.Count} added, {removed.Count} removed, {modified.Count} modified"
            };
        }

        private static object ClearSnapshot(string sceneName)
        {
            var scene = GetScene(sceneName);
            string key = SnapshotKey + "_" + scene.name;
            SessionState.EraseString(key);
            return new { success = true, message = $"Snapshot cleared for '{scene.name}'." };
        }

        #region Helpers

        private static Scene GetScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
                return SceneManager.GetActiveScene();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.name.Equals(sceneName, StringComparison.OrdinalIgnoreCase))
                    return s;
            }

            throw MCPException.InvalidParams($"Scene '{sceneName}' not found or not loaded.");
        }

        private static List<ObjectSnapshot> BuildSnapshot(Scene scene, bool includeComponents)
        {
            var snapshots = new List<ObjectSnapshot>();
            var rootObjects = scene.GetRootGameObjects();

            foreach (var root in rootObjects)
            {
                CollectSnapshots(root, snapshots, includeComponents);
            }

            return snapshots;
        }

        private static void CollectSnapshots(GameObject go, List<ObjectSnapshot> list, bool includeComponents)
        {
            var t = go.transform;
            var snap = new ObjectSnapshot
            {
                instanceId = go.GetInstanceID(),
                name = go.name,
                active = go.activeSelf,
                path = GetPath(t),
                layer = go.layer,
                tag = go.tag,
                position = new Vec3(t.localPosition),
                rotation = new Vec3(t.localEulerAngles),
                scale = new Vec3(t.localScale),
                components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .Where(n => n != "Transform" && n != "RectTransform")
                    .ToList()
            };

            list.Add(snap);

            for (int i = 0; i < t.childCount; i++)
                CollectSnapshots(t.GetChild(i).gameObject, list, includeComponents);
        }

        private static string GetPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        private static bool PositionsEqual(Vec3 a, Vec3 b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return Mathf.Approximately(a.x, b.x) && Mathf.Approximately(a.y, b.y) && Mathf.Approximately(a.z, b.z);
        }

        #endregion

        #region Data Classes

        [Serializable]
        private class ObjectSnapshot
        {
            public int instanceId;
            public string name;
            public bool active;
            public string path;
            public int layer;
            public string tag;
            public Vec3 position;
            public Vec3 rotation;
            public Vec3 scale;
            public List<string> components;
        }

        [Serializable]
        private class Vec3
        {
            public float x, y, z;
            public Vec3() { }
            public Vec3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
        }

        #endregion
    }
}
