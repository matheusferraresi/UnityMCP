using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Captures Game View / Scene View screenshots and returns them as base64
    /// for multimodal AI vision models. The AI can "see" the game.
    /// </summary>
    public static class VisionCapture
    {
        [MCPTool("vision_capture", "Capture Game View or Scene View screenshot as base64 PNG for AI vision. The AI can SEE your game.",
            Category = "Scene", ReadOnlyHint = true)]
        public static object Capture(
            [MCPParam("view", "Which view to capture: game, scene, or both (default: game)",
                Enum = new[] { "game", "scene", "both" })] string view = "game",
            [MCPParam("width", "Target width in pixels (default: 640)", Minimum = 64, Maximum = 1920)] int width = 640,
            [MCPParam("height", "Target height in pixels (default: 480, or 0 for auto aspect ratio)", Minimum = 0, Maximum = 1080)] int height = 0)
        {
            var images = new List<object>();

            try
            {
                if (view == "game" || view == "both")
                {
                    var gameCapture = CaptureGameView(width, height);
                    if (gameCapture != null)
                        images.Add(gameCapture);
                }

                if (view == "scene" || view == "both")
                {
                    var sceneCapture = CaptureSceneView(width, height);
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

        private static object CaptureGameView(int targetWidth, int targetHeight)
        {
            try
            {
                // Find the Game View window
                var gameViewType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType == null) return null;

                var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                if (gameView == null) return null;

                // Force repaint to get latest frame
                gameView.Repaint();

                // Use RenderTexture to capture at specific resolution
                var cameras = Camera.allCameras;
                if (cameras.Length == 0 && Camera.main == null)
                {
                    return new
                    {
                        view = "game",
                        error = "No cameras found in scene."
                    };
                }

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
                string base64 = Convert.ToBase64String(png);

                // Cleanup
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);

                return new
                {
                    view = "game",
                    width = targetWidth,
                    height = captureHeight,
                    size_bytes = png.Length,
                    base64
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    view = "game",
                    error = ex.Message
                };
            }
        }

        private static object CaptureSceneView(int targetWidth, int targetHeight)
        {
            try
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return new
                    {
                        view = "scene",
                        error = "No active Scene View found."
                    };
                }

                var camera = sceneView.camera;
                if (camera == null)
                {
                    return new
                    {
                        view = "scene",
                        error = "Scene View camera not available."
                    };
                }

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
                string base64 = Convert.ToBase64String(png);

                // Cleanup
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);

                return new
                {
                    view = "scene",
                    width = targetWidth,
                    height = captureHeight,
                    size_bytes = png.Length,
                    base64
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    view = "scene",
                    error = ex.Message
                };
            }
        }
    }
}
