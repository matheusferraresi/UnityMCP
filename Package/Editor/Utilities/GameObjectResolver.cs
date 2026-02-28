using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnixxtyMCP.Editor.Core
{
    /// <summary>
    /// Shared utility for resolving GameObjects by instance ID, path, or name.
    /// Extracted from ManageGameObject for reuse across tools.
    /// </summary>
    public static class GameObjectResolver
    {
        /// <summary>
        /// Resolves a GameObject by instance ID (int), hierarchy path (contains '/'), or name.
        /// Returns null if not found.
        /// </summary>
        public static GameObject Resolve(string target, bool searchInactive = true)
        {
            if (string.IsNullOrEmpty(target))
                return null;

            // Try instance ID first
            if (int.TryParse(target, out int instanceId))
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go) return go;
                if (obj is Component comp) return comp.gameObject;
            }

            Scene activeScene = SceneManager.GetActiveScene();

            // Try path-based lookup
            if (target.Contains("/"))
            {
                var roots = activeScene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root == null) continue;

                    string rootPath = root.name;
                    if (target.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
                        return root;

                    if (target.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        var found = root.transform.Find(target.Substring(rootPath.Length + 1));
                        if (found != null) return found.gameObject;
                    }
                }
            }

            // Try name-based lookup across all scene objects
            var rootObjects = activeScene.GetRootGameObjects();
            foreach (var root in rootObjects)
            {
                if (root == null) continue;
                var result = FindByName(root.transform, target, searchInactive);
                if (result != null) return result;
            }

            return null;
        }

        private static GameObject FindByName(Transform parent, string name, bool searchInactive)
        {
            if (!searchInactive && !parent.gameObject.activeInHierarchy)
                return null;

            if (parent.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return parent.gameObject;

            for (int i = 0; i < parent.childCount; i++)
            {
                var result = FindByName(parent.GetChild(i), name, searchInactive);
                if (result != null) return result;
            }

            return null;
        }
    }
}
