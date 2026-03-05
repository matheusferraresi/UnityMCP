using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnixxtyMCP.Editor.Utilities
{
    /// <summary>
    /// Helper for loading assets with diagnostic hints when they fail to load.
    /// Returns actionable messages when an asset exists on disk but isn't imported.
    /// </summary>
    public static class AssetLoadHelper
    {
        /// <summary>
        /// Loads an asset, returning a helpful error message if it fails.
        /// When the file exists on disk but AssetDatabase doesn't know about it,
        /// suggests running unity_refresh first.
        /// </summary>
        public static T LoadWithHint<T>(string assetPath, out string error) where T : Object
        {
            error = null;

            if (string.IsNullOrEmpty(assetPath))
            {
                error = "Asset path is null or empty.";
                return null;
            }

            string normalizedPath = PathUtilities.NormalizePath(assetPath);
            var asset = AssetDatabase.LoadAssetAtPath<T>(normalizedPath);

            if (asset != null)
                return asset;

            // Asset not found in AssetDatabase — check if the file exists on disk
            string fullPath = ResolveFullPath(normalizedPath);

            if (File.Exists(fullPath))
            {
                error = $"Asset at '{normalizedPath}' exists on disk but is not imported into Unity. " +
                        "Run unity_refresh(mode: 'force') first, then retry.";
            }
            else
            {
                error = $"Asset not found at '{normalizedPath}'. The file does not exist on disk.";
            }

            return null;
        }

        /// <summary>
        /// Resolves a Unity asset path to a full filesystem path.
        /// Handles both Assets/ and Packages/ paths.
        /// </summary>
        private static string ResolveFullPath(string assetPath)
        {
            string basePath = Path.Combine(Application.dataPath, "..", assetPath).Replace('\\', '/');

            if (assetPath.StartsWith("Packages/") && !File.Exists(basePath))
            {
                // Package paths may resolve via symlink or cache — try AssetDatabase resolution
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid))
                {
                    string resolvedPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        string fullResolved = Path.GetFullPath(
                            Path.Combine(Application.dataPath, "..", resolvedPath)).Replace('\\', '/');
                        if (File.Exists(fullResolved))
                            return fullResolved;
                    }
                }
            }

            return basePath;
        }
    }
}
