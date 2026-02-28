using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnixxtyMCP.Editor;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Gets asset preview thumbnails as base64 PNG images.
    /// Enables vision-capable AI models to see what assets look like without opening them.
    /// </summary>
    public static class AssetPreviewTool
    {
        [MCPTool("asset_preview", "Get asset preview thumbnails as base64 PNG. Works with prefabs, materials, textures, models, sprites, and more.", Category = "Asset")]
        public static object Execute(
            [MCPParam("action", "Action: get (single asset preview), batch (multiple asset previews)", required: true,
                Enum = new[] { "get", "batch" })] string action,
            [MCPParam("asset_path", "Asset path for 'get' action (e.g., 'Assets/Prefabs/Player.prefab')")] string assetPath = null,
            [MCPParam("asset_paths", "Array of asset paths for 'batch' action")] List<string> assetPaths = null,
            [MCPParam("width", "Preview image width (default: 128, max: 512)")] int width = 128,
            [MCPParam("height", "Preview image height (default: 128, max: 512)")] int height = 128)
        {
            width = Mathf.Clamp(width, 32, 512);
            height = Mathf.Clamp(height, 32, 512);

            try
            {
                return action.ToLowerInvariant() switch
                {
                    "get" => GetPreview(assetPath, width, height),
                    "batch" => GetBatchPreview(assetPaths, width, height),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'. Valid: get, batch")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"Asset preview failed: {ex.Message}");
            }
        }

        private static object GetPreview(string assetPath, int width, int height)
        {
            if (string.IsNullOrEmpty(assetPath))
                throw MCPException.InvalidParams("asset_path is required for 'get' action.");

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                throw MCPException.InvalidParams($"Asset not found: '{assetPath}'");

            string base64 = CapturePreview(asset, width, height);
            string assetType = asset.GetType().Name;

            return new
            {
                success = true,
                assetPath,
                assetType,
                hasPreview = base64 != null,
                width,
                height,
                base64_png = base64,
                message = base64 != null
                    ? $"Preview captured for {assetType} '{asset.name}'"
                    : $"No preview available for {assetType} '{asset.name}'. Asset may need to be loaded first."
            };
        }

        private static object GetBatchPreview(List<string> paths, int width, int height)
        {
            if (paths == null || paths.Count == 0)
                throw MCPException.InvalidParams("asset_paths is required for 'batch' action.");

            if (paths.Count > 20)
                throw MCPException.InvalidParams("Maximum 20 assets per batch request.");

            var results = new List<object>();
            int successCount = 0;

            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset == null)
                {
                    results.Add(new { assetPath = path, success = false, error = "Asset not found" });
                    continue;
                }

                string base64 = CapturePreview(asset, width, height);
                results.Add(new
                {
                    assetPath = path,
                    success = base64 != null,
                    assetType = asset.GetType().Name,
                    base64_png = base64
                });

                if (base64 != null) successCount++;
            }

            return new
            {
                success = true,
                total = paths.Count,
                captured = successCount,
                previews = results
            };
        }

        private static string CapturePreview(UnityEngine.Object asset, int width, int height)
        {
            // For Texture2D assets, we can directly encode
            if (asset is Texture2D tex2d)
            {
                return EncodeTexture(tex2d, width, height);
            }

            // For Sprite assets, use the texture
            if (asset is Sprite sprite)
            {
                return EncodeTexture(sprite.texture, width, height);
            }

            // Use Unity's AssetPreview system for everything else
            var preview = UnityEditor.AssetPreview.GetAssetPreview(asset);
            if (preview != null)
            {
                return EncodeTexture(preview, width, height);
            }

            // Try mini thumbnail as fallback
            var miniThumb = UnityEditor.AssetPreview.GetMiniThumbnail(asset);
            if (miniThumb != null)
            {
                return EncodeTexture(miniThumb, width, height);
            }

            return null;
        }

        private static string EncodeTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            if (source == null) return null;

            try
            {
                // Create readable copy at target resolution
                var rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, rt);

                var previous = RenderTexture.active;
                RenderTexture.active = rt;

                var readable = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                readable.Apply();

                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);

                byte[] pngData = readable.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(readable);

                return pngData != null ? Convert.ToBase64String(pngData) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
