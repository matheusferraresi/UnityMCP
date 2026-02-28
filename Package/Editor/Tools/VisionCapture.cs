using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Captures Game View / Scene View screenshots and returns them as base64
    /// for multimodal AI vision models. The AI can "see" the game.
    /// </summary>
    public static class VisionCapture
    {
        [MCPTool("vision_capture",
            "Capture Game View or Scene View screenshot as base64 PNG for AI vision analysis. " +
            "Use output_path to save to disk instead of returning base64 (recommended for large captures). " +
            "For file-based capture with ScreenCapture API, see scene_screenshot.",
            Category = "Scene", ReadOnlyHint = true)]
        public static object Capture(
            [MCPParam("view", "Which view to capture: game, scene, or both (default: game)",
                Enum = new[] { "game", "scene", "both" })] string view = "game",
            [MCPParam("width", "Target width in pixels (default: 640)", Minimum = 64, Maximum = 1920)] int width = 640,
            [MCPParam("height", "Target height in pixels (default: 480, or 0 for auto aspect ratio)", Minimum = 0, Maximum = 1080)] int height = 0,
            [MCPParam("output_path", "Save PNG to this path instead of returning base64. Relative paths resolve from project root. Recommended for large captures.")] string outputPath = null)
        {
            var images = new List<object>();
            bool saveToFile = !string.IsNullOrEmpty(outputPath);

            try
            {
                if (view == "game" || view == "both")
                {
                    var gameCapture = CaptureView("game", width, height, saveToFile, outputPath, view == "both" ? "_game" : "");
                    if (gameCapture != null)
                        images.Add(gameCapture);
                }

                if (view == "scene" || view == "both")
                {
                    var sceneCapture = CaptureView("scene", width, height, saveToFile, outputPath, view == "both" ? "_scene" : "");
                    if (sceneCapture != null)
                        images.Add(sceneCapture);
                }

                if (images.Count == 0)
                {
                    return new
                    {
                        success = false,
                        error = "No views could be captured. Ensure Game View or Scene View is open."
                    };
                }

                return new
                {
                    success = true,
                    image_count = images.Count,
                    saved_to_file = saveToFile,
                    images
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    error = $"Vision capture failed: {ex.Message}"
                };
            }
        }

        private static object CaptureView(string viewType, int width, int height, bool saveToFile, string outputPath, string suffix)
        {
            // Capture the raw PNG bytes using existing methods
            var captureResult = viewType == "game" ? CaptureGameViewRaw(width, height) : CaptureSceneViewRaw(width, height);
            if (captureResult == null) return null;

            if (captureResult.Error != null)
            {
                return new { view = viewType, error = captureResult.Error };
            }

            if (saveToFile)
            {
                string resolvedPath = ResolveSavePath(outputPath, suffix);
                string directory = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(resolvedPath, captureResult.PngBytes);

                return new
                {
                    view = viewType,
                    width = captureResult.Width,
                    height = captureResult.Height,
                    size_bytes = captureResult.PngBytes.Length,
                    path = resolvedPath
                };
            }
            else
            {
                string base64 = Convert.ToBase64String(captureResult.PngBytes);
                return new
                {
                    view = viewType,
                    width = captureResult.Width,
                    height = captureResult.Height,
                    size_bytes = captureResult.PngBytes.Length,
                    base64
                };
            }
        }

        private static string ResolveSavePath(string outputPath, string suffix)
        {
            // Insert suffix before extension for "both" mode
            string path = outputPath;
            if (!string.IsNullOrEmpty(suffix))
            {
                string ext = Path.GetExtension(path);
                string withoutExt = Path.ChangeExtension(path, null);
                path = withoutExt + suffix + (string.IsNullOrEmpty(ext) ? ".png" : ext);
            }

            // Ensure .png extension
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";

            // Resolve relative paths from project root
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Application.dataPath, "..", path);

            return Path.GetFullPath(path);
        }

        private class CaptureData
        {
            public byte[] PngBytes;
            public int Width;
            public int Height;
            public string Error;
        }

        private static CaptureData CaptureGameViewRaw(int targetWidth, int targetHeight)
        {
            try
            {
                var gameViewType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType == null) return null;

                var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                if (gameView == null) return null;

                gameView.Repaint();

                var cameras = Camera.allCameras;
                if (cameras.Length == 0 && Camera.main == null)
                    return new CaptureData { Error = "No cameras found in scene." };

                var camera = Camera.main ?? cameras[0];
                int captureHeight = targetHeight > 0 ? targetHeight : Mathf.RoundToInt(targetWidth * 0.75f);

                var rt = new RenderTexture(targetWidth, captureHeight, 24, RenderTextureFormat.ARGB32);
                var prevRT = camera.targetTexture;

                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = prevRT;

                var tex = new Texture2D(targetWidth, captureHeight, TextureFormat.RGB24, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, targetWidth, captureHeight), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;

                byte[] png = tex.EncodeToPNG();

                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);

                return new CaptureData { PngBytes = png, Width = targetWidth, Height = captureHeight };
            }
            catch (Exception ex)
            {
                return new CaptureData { Error = ex.Message };
            }
        }

        private static CaptureData CaptureSceneViewRaw(int targetWidth, int targetHeight)
        {
            try
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                    return new CaptureData { Error = "No active Scene View found." };

                var camera = sceneView.camera;
                if (camera == null)
                    return new CaptureData { Error = "Scene View camera not available." };

                int captureHeight = targetHeight > 0 ? targetHeight : Mathf.RoundToInt(targetWidth * 0.75f);

                var rt = new RenderTexture(targetWidth, captureHeight, 24, RenderTextureFormat.ARGB32);
                var prevRT = camera.targetTexture;

                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = prevRT;

                var tex = new Texture2D(targetWidth, captureHeight, TextureFormat.RGB24, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, targetWidth, captureHeight), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;

                byte[] png = tex.EncodeToPNG();

                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);

                return new CaptureData { PngBytes = png, Width = targetWidth, Height = captureHeight };
            }
            catch (Exception ex)
            {
                return new CaptureData { Error = ex.Message };
            }
        }
    }
}
