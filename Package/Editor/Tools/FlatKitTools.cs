#if UNITY_MCP_FLATKIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// MCP tool for managing Flat Kit toon rendering: outline/fog settings, render features, materials, mesh smoothing.
    /// All Flat Kit access via reflection — no direct type references.
    /// </summary>
    public static class FlatKitTools
    {
        #region Cached Types

        private static Type _outlineSettingsType;
        private static Type _fogSettingsType;
        private static Type _flatKitOutlineType;
        private static Type _flatKitFogType;
        private static Type _meshSmootherType;

        private static Type OutlineSettingsType => _outlineSettingsType ??= FindType("FlatKit.OutlineSettings");
        private static Type FogSettingsType => _fogSettingsType ??= FindType("FlatKit.FogSettings");
        private static Type FlatKitOutlineType => _flatKitOutlineType ??= FindType("FlatKit.FlatKitOutline");
        private static Type FlatKitFogType => _flatKitFogType ??= FindType("FlatKit.FlatKitFog");
        private static Type MeshSmootherType => _meshSmootherType ??= FindType("FlatKit.MeshSmoother");

        #endregion

        #region Main Tool Entry Point

        [MCPTool("flatkit_manage", "Manages Flat Kit rendering: outline/fog settings, render features, materials, mesh smoothing",
            Category = "Asset", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "outline_settings", "fog_settings", "toggle_feature",
                               "inspect_renderer", "apply_preset", "smooth_normals",
                               "batch_materials", "list_materials" })] string action,
            [MCPParam("target", "Asset path or GameObject name")] string target = null,
            [MCPParam("renderer_path", "Path to URP Renderer asset")] string rendererPath = null,
            [MCPParam("feature_type", "Feature type: outline, fog")] string featureType = null,
            [MCPParam("enabled", "Enable or disable a feature")] bool? enabled = null,
            [MCPParam("settings_path", "Asset path for settings ScriptableObject")] string settingsPath = null,
            [MCPParam("edge_color", "Outline edge color as r,g,b,a")] string edgeColor = null,
            [MCPParam("thickness", "Outline thickness 0-5")] float thickness = -1,
            [MCPParam("use_depth", "Enable depth-based outlines")] bool? useDepth = null,
            [MCPParam("use_normals", "Enable normal-based outlines")] bool? useNormals = null,
            [MCPParam("use_distance_fog", "Enable distance fog")] bool? useDistanceFog = null,
            [MCPParam("fog_near", "Distance fog near plane")] float fogNear = -1,
            [MCPParam("fog_far", "Distance fog far plane")] float fogFar = -1,
            [MCPParam("fog_intensity", "Distance fog intensity 0-1")] float fogIntensity = -1,
            [MCPParam("preset", "Preset name: anime_mech, flat_ground, emissive")] string preset = null,
            [MCPParam("property_name", "Material property to batch-set")] string propertyName = null,
            [MCPParam("property_value", "Value for batch-set")] string propertyValue = null,
            [MCPParam("mesh_path", "Asset path for mesh to smooth")] string meshPath = null)
        {
            if (string.IsNullOrEmpty(action))
                throw MCPException.InvalidParams("Action parameter is required.");

            EnsureFlatKitAvailable();

            try
            {
                return action.ToLowerInvariant() switch
                {
                    "outline_settings" => HandleOutlineSettings(settingsPath, edgeColor, thickness, useDepth, useNormals),
                    "fog_settings" => HandleFogSettings(settingsPath, useDistanceFog, fogNear, fogFar, fogIntensity),
                    "toggle_feature" => HandleToggleFeature(rendererPath, featureType, enabled),
                    "inspect_renderer" => HandleInspectRenderer(rendererPath),
                    "apply_preset" => HandleApplyPreset(target, preset),
                    "smooth_normals" => HandleSmoothNormals(meshPath),
                    "batch_materials" => HandleBatchMaterials(propertyName, propertyValue),
                    "list_materials" => HandleListMaterials(),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"Flat Kit operation failed: {ex.Message}");
            }
        }

        #endregion

        #region Action Handlers

        private static object HandleOutlineSettings(string settingsPath, string edgeColor, float thickness, bool? useDepth, bool? useNormals)
        {
            if (string.IsNullOrEmpty(settingsPath))
                settingsPath = "Assets/_Project/Settings/FlatKit_OutlineSettings.asset";

            var settings = AssetDatabase.LoadAssetAtPath(settingsPath, OutlineSettingsType);
            bool created = false;

            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance(OutlineSettingsType);
                EnsureDirectoryExists(settingsPath);
                AssetDatabase.CreateAsset(settings, settingsPath);
                created = true;
            }

            var so = new SerializedObject(settings);
            int changes = 0;

            if (!string.IsNullOrEmpty(edgeColor))
            {
                var color = ParseColor(edgeColor);
                var prop = so.FindProperty("edgeColor");
                if (prop != null) { prop.colorValue = color; changes++; }
            }

            if (thickness >= 0)
            {
                var prop = so.FindProperty("thickness");
                if (prop != null) { prop.floatValue = thickness; changes++; }
            }

            if (useDepth.HasValue)
            {
                var prop = so.FindProperty("useDepth");
                if (prop != null) { prop.boolValue = useDepth.Value; changes++; }
            }

            if (useNormals.HasValue)
            {
                var prop = so.FindProperty("useNormals");
                if (prop != null) { prop.boolValue = useNormals.Value; changes++; }
            }

            if (changes > 0)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            return new
            {
                success = true,
                message = created ? $"Created outline settings at '{settingsPath}'" : $"Updated {changes} properties on outline settings",
                path = settingsPath,
                created,
                propertiesChanged = changes
            };
        }

        private static object HandleFogSettings(string settingsPath, bool? useDistanceFog, float fogNear, float fogFar, float fogIntensity)
        {
            if (string.IsNullOrEmpty(settingsPath))
                settingsPath = "Assets/_Project/Settings/FlatKit_FogSettings.asset";

            var settings = AssetDatabase.LoadAssetAtPath(settingsPath, FogSettingsType);
            bool created = false;

            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance(FogSettingsType);
                EnsureDirectoryExists(settingsPath);
                AssetDatabase.CreateAsset(settings, settingsPath);
                created = true;
            }

            var so = new SerializedObject(settings);
            int changes = 0;

            if (useDistanceFog.HasValue)
            {
                var prop = so.FindProperty("useDistance");
                if (prop != null) { prop.boolValue = useDistanceFog.Value; changes++; }
            }

            if (fogNear >= 0)
            {
                var prop = so.FindProperty("distanceNear");
                if (prop != null) { prop.floatValue = fogNear; changes++; }
            }

            if (fogFar >= 0)
            {
                var prop = so.FindProperty("distanceFar");
                if (prop != null) { prop.floatValue = fogFar; changes++; }
            }

            if (fogIntensity >= 0)
            {
                var prop = so.FindProperty("distanceIntensity");
                if (prop != null) { prop.floatValue = fogIntensity; changes++; }
            }

            if (changes > 0)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            return new
            {
                success = true,
                message = created ? $"Created fog settings at '{settingsPath}'" : $"Updated {changes} properties on fog settings",
                path = settingsPath,
                created,
                propertiesChanged = changes
            };
        }

        private static object HandleToggleFeature(string rendererPath, string featureType, bool? enabled)
        {
            if (string.IsNullOrEmpty(featureType))
                throw MCPException.InvalidParams("'feature_type' is required: outline or fog.");
            if (!enabled.HasValue)
                throw MCPException.InvalidParams("'enabled' is required for toggle_feature.");

            var renderer = LoadRenderer(rendererPath);
            if (renderer == null)
                throw new MCPException("Cannot load URP Renderer.");

            var so = new SerializedObject(renderer);
            var featuresProp = so.FindProperty("m_RendererFeatures");
            if (featuresProp == null)
                throw new MCPException("Cannot access renderer features.");

            Type targetType = featureType.ToLowerInvariant() switch
            {
                "outline" => FlatKitOutlineType,
                "fog" => FlatKitFogType,
                _ => throw MCPException.InvalidParams($"Unknown feature_type: '{featureType}'. Use 'outline' or 'fog'.")
            };

            bool found = false;
            for (int i = 0; i < featuresProp.arraySize; i++)
            {
                var element = featuresProp.GetArrayElementAtIndex(i);
                var feature = element.objectReferenceValue;
                if (feature != null && feature.GetType() == targetType)
                {
                    var featureSo = new SerializedObject(feature);
                    var activeProp = featureSo.FindProperty("m_Active");
                    if (activeProp != null)
                    {
                        activeProp.boolValue = enabled.Value;
                        featureSo.ApplyModifiedProperties();
                    }
                    else
                    {
                        // SetActive via reflection
                        var setActiveMethod = feature.GetType().GetMethod("SetActive", BindingFlags.Public | BindingFlags.Instance);
                        setActiveMethod?.Invoke(feature, new object[] { enabled.Value });
                    }
                    EditorUtility.SetDirty(feature);
                    found = true;
                    break;
                }
            }

            if (!found)
                return new { success = false, message = $"Flat Kit {featureType} feature not found on renderer. Add it first via the Renderer inspector." };

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();

            return new { success = true, message = $"Set Flat Kit {featureType} to {(enabled.Value ? "enabled" : "disabled")}" };
        }

        private static object HandleInspectRenderer(string rendererPath)
        {
            var renderer = LoadRenderer(rendererPath);
            if (renderer == null)
                throw new MCPException("Cannot load URP Renderer.");

            var so = new SerializedObject(renderer);
            var featuresProp = so.FindProperty("m_RendererFeatures");

            var features = new List<object>();
            if (featuresProp != null)
            {
                for (int i = 0; i < featuresProp.arraySize; i++)
                {
                    var element = featuresProp.GetArrayElementAtIndex(i);
                    var feature = element.objectReferenceValue;
                    if (feature != null)
                    {
                        bool active = true;
                        var featureSo = new SerializedObject(feature);
                        var activeProp = featureSo.FindProperty("m_Active");
                        if (activeProp != null) active = activeProp.boolValue;

                        // Get settings reference if it's a Flat Kit feature
                        string settingsInfo = "";
                        if (feature.GetType() == FlatKitOutlineType || feature.GetType() == FlatKitFogType)
                        {
                            var settingsField = feature.GetType().GetField("settings", BindingFlags.Public | BindingFlags.Instance)
                                             ?? feature.GetType().GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (settingsField != null)
                            {
                                var settingsObj = settingsField.GetValue(feature) as ScriptableObject;
                                if (settingsObj != null)
                                    settingsInfo = AssetDatabase.GetAssetPath(settingsObj);
                            }
                        }

                        features.Add(new
                        {
                            index = i,
                            type = feature.GetType().Name,
                            name = feature.name,
                            active,
                            isFlatKit = feature.GetType().Namespace?.Contains("FlatKit") == true || feature.GetType().Name.StartsWith("FlatKit"),
                            settingsAsset = settingsInfo
                        });
                    }
                    else
                    {
                        features.Add(new { index = i, type = "null", name = "(missing)", active = false, isFlatKit = false, settingsAsset = "" });
                    }
                }
            }

            return new
            {
                success = true,
                rendererPath = rendererPath ?? GetActiveRendererPath(),
                featureCount = features.Count,
                features
            };
        }

        private static object HandleApplyPreset(string target, string preset)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for apply_preset.");
            if (string.IsNullOrEmpty(preset))
                throw MCPException.InvalidParams("'preset' is required for apply_preset.");

            // Try as material asset path first
            var mat = AssetDatabase.LoadAssetAtPath<Material>(target);
            List<Material> materials;

            if (mat != null)
            {
                materials = new List<Material> { mat };
            }
            else
            {
                // Try as GameObject — get all materials
                var go = GameObjectResolver.Resolve(target);
                if (go == null) throw MCPException.InvalidParams($"Target '{target}' not found as asset or GameObject.");

                materials = new List<Material>();
                foreach (var renderer in go.GetComponentsInChildren<Renderer>())
                {
                    materials.AddRange(renderer.sharedMaterials.Where(m => m != null));
                }
            }

            if (materials.Count == 0)
                return new { success = false, message = "No materials found on target." };

            var presetValues = GetPresetValues(preset);
            int applied = 0;

            foreach (var m in materials)
            {
                foreach (var kvp in presetValues)
                {
                    if (m.HasProperty(kvp.Key))
                    {
                        if (kvp.Value is float f) m.SetFloat(kvp.Key, f);
                        else if (kvp.Value is Color c) m.SetColor(kvp.Key, c);
                        else if (kvp.Value is int i) m.SetInt(kvp.Key, i);
                        applied++;
                    }
                }
                EditorUtility.SetDirty(m);
            }

            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Applied preset '{preset}' to {materials.Count} materials ({applied} property changes)",
                materialsAffected = materials.Count,
                propertiesSet = applied
            };
        }

        private static object HandleSmoothNormals(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath))
                throw MCPException.InvalidParams("'mesh_path' is required for smooth_normals.");

            if (MeshSmootherType == null)
                throw new MCPException("FlatKit.MeshSmoother not found.");

            // Load mesh
            var meshAsset = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (meshAsset == null)
            {
                // Try loading model importer and getting mesh
                var obj = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
                if (obj != null)
                {
                    var meshFilter = obj.GetComponentInChildren<MeshFilter>();
                    meshAsset = meshFilter?.sharedMesh;
                }
            }

            if (meshAsset == null)
                throw MCPException.InvalidParams($"Mesh not found at '{meshPath}'.");

            // Call MeshSmoother.SmoothNormals
            var smoothMethod = MeshSmootherType.GetMethod("SmoothNormals", BindingFlags.Public | BindingFlags.Static);
            if (smoothMethod != null)
            {
                smoothMethod.Invoke(null, new object[] { meshAsset });
            }
            else
            {
                // Try instance method or alternative
                var altMethod = MeshSmootherType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name.Contains("Smooth"));
                if (altMethod != null)
                    altMethod.Invoke(null, new object[] { meshAsset });
                else
                    throw new MCPException("Cannot find SmoothNormals method on MeshSmoother.");
            }

            EditorUtility.SetDirty(meshAsset);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Smoothed normals on '{meshPath}'",
                meshPath,
                vertexCount = meshAsset.vertexCount
            };
        }

        private static object HandleBatchMaterials(string propertyName, string propertyValue)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw MCPException.InvalidParams("'property_name' is required for batch_materials.");
            if (string.IsNullOrEmpty(propertyValue))
                throw MCPException.InvalidParams("'property_value' is required for batch_materials.");

            var guids = AssetDatabase.FindAssets("t:Material");
            int modified = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                if (!mat.shader.name.Contains("FlatKit") && !mat.shader.name.Contains("Flat Kit")) continue;
                if (!mat.HasProperty(propertyName)) continue;

                // Determine type and set
                if (float.TryParse(propertyValue, out float fVal))
                {
                    mat.SetFloat(propertyName, fVal);
                    modified++;
                }
                else if (propertyValue.Contains(","))
                {
                    var color = ParseColor(propertyValue);
                    mat.SetColor(propertyName, color);
                    modified++;
                }
                else if (int.TryParse(propertyValue, out int iVal))
                {
                    mat.SetInt(propertyName, iVal);
                    modified++;
                }

                if (modified > 0) EditorUtility.SetDirty(mat);
            }

            if (modified > 0) AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Set '{propertyName}' = {propertyValue} on {modified} Flat Kit materials",
                materialsModified = modified
            };
        }

        private static object HandleListMaterials()
        {
            var guids = AssetDatabase.FindAssets("t:Material");
            var results = new List<object>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                if (!mat.shader.name.Contains("FlatKit") && !mat.shader.name.Contains("Flat Kit")) continue;

                results.Add(new
                {
                    name = mat.name,
                    path,
                    shader = mat.shader.name,
                    renderQueue = mat.renderQueue
                });
            }

            return new { success = true, count = results.Count, materials = results };
        }

        #endregion

        #region Helpers

        private static void EnsureFlatKitAvailable()
        {
            if (OutlineSettingsType == null && FogSettingsType == null)
                throw new MCPException("Flat Kit is not installed or not loaded. Ensure Flat Kit is in the project and UNITY_MCP_FLATKIT define is set.");
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private static UnityEngine.Object LoadRenderer(string rendererPath)
        {
            if (string.IsNullOrEmpty(rendererPath))
                rendererPath = GetActiveRendererPath();

            if (string.IsNullOrEmpty(rendererPath))
                throw new MCPException("No URP Renderer path provided and cannot detect active renderer.");

            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(rendererPath);
        }

        private static string GetActiveRendererPath()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return null;

            var so = new SerializedObject(pipeline);
            var rendererListProp = so.FindProperty("m_RendererDataList");
            if (rendererListProp != null && rendererListProp.arraySize > 0)
            {
                var firstRenderer = rendererListProp.GetArrayElementAtIndex(0).objectReferenceValue;
                if (firstRenderer != null)
                    return AssetDatabase.GetAssetPath(firstRenderer);
            }

            return null;
        }

        private static void EnsureDirectoryExists(string assetPath)
        {
            var dir = System.IO.Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                var parts = dir.Replace("\\", "/").Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }
        }

        private static Color ParseColor(string colorStr)
        {
            var parts = colorStr.Trim('(', ')').Split(',');
            float r = parts.Length > 0 ? float.Parse(parts[0].Trim()) : 0;
            float g = parts.Length > 1 ? float.Parse(parts[1].Trim()) : 0;
            float b = parts.Length > 2 ? float.Parse(parts[2].Trim()) : 0;
            float a = parts.Length > 3 ? float.Parse(parts[3].Trim()) : 1;
            return new Color(r, g, b, a);
        }

        private static Dictionary<string, object> GetPresetValues(string preset)
        {
            return preset.ToLowerInvariant() switch
            {
                "anime_mech" => new Dictionary<string, object>
                {
                    { "_CelPrimaryColor", new Color(1f, 1f, 1f, 1f) },
                    { "_CelShadingHardness", 0.85f },
                    { "_SelfShadingSize", 0.5f },
                    { "_ShadowEdgeSize", 0.1f },
                    { "_Flatness", 0.7f },
                    { "_RimSize", 0.3f },
                    { "_RimColor", new Color(1f, 1f, 1f, 0.5f) },
                    { "_OutlineWidth", 1.5f },
                    { "_OutlineColor", new Color(0.1f, 0.1f, 0.15f, 1f) }
                },
                "flat_ground" => new Dictionary<string, object>
                {
                    { "_CelPrimaryColor", new Color(0.8f, 0.85f, 0.75f, 1f) },
                    { "_CelShadingHardness", 0.5f },
                    { "_Flatness", 1f },
                    { "_RimSize", 0f },
                    { "_OutlineWidth", 0f }
                },
                "emissive" => new Dictionary<string, object>
                {
                    { "_EmissionColor", new Color(1f, 0.8f, 0.3f, 1f) },
                    { "_CelShadingHardness", 0.3f },
                    { "_RimSize", 0.5f },
                    { "_RimColor", new Color(1f, 0.9f, 0.6f, 1f) }
                },
                _ => throw MCPException.InvalidParams($"Unknown preset: '{preset}'. Available: anime_mech, flat_ground, emissive")
            };
        }

        #endregion
    }
}
#endif
