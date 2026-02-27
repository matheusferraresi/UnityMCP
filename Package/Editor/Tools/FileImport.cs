using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Imports external files into the Unity project with optional import settings configuration.
    /// </summary>
    public static class FileImport
    {
        [MCPTool("file_import", "Import external files into Unity project. Copies files from anywhere on disk into Assets/ and configures import settings.", Category = "Asset", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("source_path", "Absolute path to the source file on disk", required: true)] string sourcePath,
            [MCPParam("destination", "Destination path relative to Assets/ (e.g., 'Textures/player.png'). If a folder, filename is preserved.", required: true)] string destination,
            [MCPParam("overwrite", "Overwrite if destination exists (default: false)")] bool overwrite = false,
            [MCPParam("texture_type", "For textures: Default, NormalMap, Sprite, Editor GUI, Cursor, Cookie, Lightmap, SingleChannel")] string textureType = null,
            [MCPParam("texture_max_size", "For textures: max size (32-16384)")] int? textureMaxSize = null,
            [MCPParam("texture_compression", "For textures: None, LowQuality, Normal, HighQuality")] string textureCompression = null,
            [MCPParam("sprite_pixels_per_unit", "For sprites: pixels per unit (default: 100)")] float? spritePixelsPerUnit = null,
            [MCPParam("model_scale_factor", "For models: scale factor (default: 1)")] float? modelScaleFactor = null,
            [MCPParam("model_import_materials", "For models: import materials")] bool? modelImportMaterials = null,
            [MCPParam("audio_force_mono", "For audio: force to mono")] bool? audioForceMono = null,
            [MCPParam("audio_load_in_background", "For audio: load in background")] bool? audioLoadInBackground = null)
        {
            // Validate source
            if (string.IsNullOrEmpty(sourcePath))
                throw MCPException.InvalidParams("source_path is required.");
            if (!File.Exists(sourcePath))
                throw MCPException.InvalidParams($"Source file not found: '{sourcePath}'");

            // Build destination path
            if (string.IsNullOrEmpty(destination))
                throw MCPException.InvalidParams("destination is required.");

            string assetPath = destination.StartsWith("Assets/") || destination.StartsWith("Assets\\")
                ? destination
                : "Assets/" + destination;

            // If destination is a directory path (ends with / or existing dir), append filename
            if (assetPath.EndsWith("/") || assetPath.EndsWith("\\"))
                assetPath += Path.GetFileName(sourcePath);

            // Normalize path separators
            assetPath = assetPath.Replace('\\', '/');

            string fullDestPath = Path.Combine(Application.dataPath, "..", assetPath).Replace('\\', '/');
            fullDestPath = Path.GetFullPath(fullDestPath);

            // Check existing
            if (File.Exists(fullDestPath) && !overwrite)
                throw MCPException.InvalidParams($"Destination already exists: '{assetPath}'. Set overwrite=true to replace.");

            // Ensure directory exists
            string destDir = Path.GetDirectoryName(fullDestPath);
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            try
            {
                // Copy file
                File.Copy(sourcePath, fullDestPath, overwrite);

                // Import into Unity
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

                // Get file info
                var fileInfo = new FileInfo(fullDestPath);
                string extension = fileInfo.Extension.ToLowerInvariant();

                // Configure import settings based on file type
                var importerSettings = new Dictionary<string, object>();

                if (IsTextureExtension(extension))
                    importerSettings = ConfigureTextureImporter(assetPath, textureType, textureMaxSize, textureCompression, spritePixelsPerUnit);
                else if (IsModelExtension(extension))
                    importerSettings = ConfigureModelImporter(assetPath, modelScaleFactor, modelImportMaterials);
                else if (IsAudioExtension(extension))
                    importerSettings = ConfigureAudioImporter(assetPath, audioForceMono, audioLoadInBackground);

                return new
                {
                    success = true,
                    assetPath,
                    fileSize = fileInfo.Length,
                    fileType = GetFileCategory(extension),
                    importSettings = importerSettings.Count > 0 ? importerSettings : null,
                    message = $"Imported '{Path.GetFileName(sourcePath)}' to '{assetPath}'"
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                // Clean up on failure
                if (File.Exists(fullDestPath))
                {
                    try { File.Delete(fullDestPath); } catch { }
                }
                throw new MCPException(-32603, $"Import failed: {ex.Message}");
            }
        }

        #region Import Settings

        private static Dictionary<string, object> ConfigureTextureImporter(string assetPath, string textureType, int? maxSize, string compression, float? spritePixelsPerUnit)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new Dictionary<string, object>();

            var settings = new Dictionary<string, object>();
            bool changed = false;

            if (!string.IsNullOrEmpty(textureType))
            {
                var type = textureType.ToLowerInvariant() switch
                {
                    "default" => TextureImporterType.Default,
                    "normalmap" => TextureImporterType.NormalMap,
                    "sprite" => TextureImporterType.Sprite,
                    "editor gui" => TextureImporterType.GUI,
                    "cursor" => TextureImporterType.Cursor,
                    "cookie" => TextureImporterType.Cookie,
                    "lightmap" => TextureImporterType.Lightmap,
                    "singlechannel" => TextureImporterType.SingleChannel,
                    _ => TextureImporterType.Default
                };
                importer.textureType = type;
                settings["textureType"] = type.ToString();
                changed = true;
            }

            if (maxSize.HasValue)
            {
                importer.maxTextureSize = maxSize.Value;
                settings["maxTextureSize"] = maxSize.Value;
                changed = true;
            }

            if (!string.IsNullOrEmpty(compression))
            {
                var comp = compression.ToLowerInvariant() switch
                {
                    "none" => TextureImporterCompression.Uncompressed,
                    "lowquality" => TextureImporterCompression.CompressedLQ,
                    "normal" => TextureImporterCompression.Compressed,
                    "highquality" => TextureImporterCompression.CompressedHQ,
                    _ => TextureImporterCompression.Compressed
                };
                importer.textureCompression = comp;
                settings["compression"] = comp.ToString();
                changed = true;
            }

            if (spritePixelsPerUnit.HasValue && importer.textureType == TextureImporterType.Sprite)
            {
                importer.spritePixelsPerUnit = spritePixelsPerUnit.Value;
                settings["spritePixelsPerUnit"] = spritePixelsPerUnit.Value;
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }

            return settings;
        }

        private static Dictionary<string, object> ConfigureModelImporter(string assetPath, float? scaleFactor, bool? importMaterials)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new Dictionary<string, object>();

            var settings = new Dictionary<string, object>();
            bool changed = false;

            if (scaleFactor.HasValue)
            {
                importer.globalScale = scaleFactor.Value;
                settings["globalScale"] = scaleFactor.Value;
                changed = true;
            }

            if (importMaterials.HasValue)
            {
                importer.materialImportMode = importMaterials.Value
                    ? ModelImporterMaterialImportMode.ImportViaMaterialDescription
                    : ModelImporterMaterialImportMode.None;
                settings["materialImportMode"] = importer.materialImportMode.ToString();
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }

            return settings;
        }

        private static Dictionary<string, object> ConfigureAudioImporter(string assetPath, bool? forceMono, bool? loadInBackground)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer == null) return new Dictionary<string, object>();

            var settings = new Dictionary<string, object>();
            bool changed = false;

            if (forceMono.HasValue)
            {
                importer.forceToMono = forceMono.Value;
                settings["forceToMono"] = forceMono.Value;
                changed = true;
            }

            if (loadInBackground.HasValue)
            {
                importer.loadInBackground = loadInBackground.Value;
                settings["loadInBackground"] = loadInBackground.Value;
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }

            return settings;
        }

        #endregion

        #region File Type Detection

        private static bool IsTextureExtension(string ext) =>
            ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".psd" or ".gif" or ".hdr" or ".exr" or ".tiff" or ".tif" or ".webp";

        private static bool IsModelExtension(string ext) =>
            ext is ".fbx" or ".obj" or ".blend" or ".dae" or ".3ds" or ".max" or ".ma" or ".mb" or ".gltf" or ".glb";

        private static bool IsAudioExtension(string ext) =>
            ext is ".wav" or ".mp3" or ".ogg" or ".aiff" or ".aif" or ".flac" or ".mod" or ".it" or ".s3m" or ".xm";

        private static string GetFileCategory(string ext)
        {
            if (IsTextureExtension(ext)) return "Texture";
            if (IsModelExtension(ext)) return "Model";
            if (IsAudioExtension(ext)) return "Audio";
            if (ext is ".cs") return "Script";
            if (ext is ".shader" or ".shadergraph" or ".hlsl" or ".cginc") return "Shader";
            if (ext is ".mat") return "Material";
            if (ext is ".prefab") return "Prefab";
            if (ext is ".unity") return "Scene";
            if (ext is ".asset") return "ScriptableObject";
            if (ext is ".anim") return "Animation";
            if (ext is ".controller") return "AnimatorController";
            if (ext is ".json" or ".xml" or ".txt" or ".csv" or ".yaml" or ".yml") return "TextAsset";
            if (ext is ".ttf" or ".otf") return "Font";
            if (ext is ".mp4" or ".avi" or ".mov" or ".webm") return "Video";
            return "Other";
        }

        #endregion
    }
}
