using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnixxtyMCP.Editor;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Controls lightmap baking: start/stop bakes, check progress, configure settings,
    /// and manage light probes.
    /// </summary>
    public static class LightingBake
    {
        [MCPTool("lighting_bake", "Control lightmap baking: start/stop/status, configure settings, manage light probes and reflection probes", Category = "Lighting")]
        public static object Execute(
            [MCPParam("action", "Action: bake, bake_async, cancel, status, get_settings, set_settings, clear_baked_data, get_lights", required: true,
                Enum = new[] { "bake", "bake_async", "cancel", "status", "get_settings", "set_settings", "clear_baked_data", "get_lights" })] string action,
            [MCPParam("lightmapper", "Lightmapper: ProgressiveGPU, ProgressiveCPU",
                Enum = new[] { "ProgressiveGPU", "ProgressiveCPU" })] string lightmapper = null,
            [MCPParam("lightmap_resolution", "Texels per unit (default: 40)")] int? lightmapResolution = null,
            [MCPParam("lightmap_padding", "Padding between UV charts (default: 2)")] int? lightmapPadding = null,
            [MCPParam("max_lightmap_size", "Max lightmap texture size: 32-4096 (default: 1024)")] int? maxLightmapSize = null,
            [MCPParam("ambient_occlusion", "Enable ambient occlusion")] bool? ambientOcclusion = null,
            [MCPParam("ao_max_distance", "AO max distance (default: 1)")] float? aoMaxDistance = null,
            [MCPParam("directional_mode", "Directional mode: NonDirectional, Directional",
                Enum = new[] { "NonDirectional", "Directional" })] string directionalMode = null,
            [MCPParam("compress_lightmaps", "Compress lightmap textures")] bool? compressLightmaps = null,
            [MCPParam("bounces", "Number of indirect bounces (0-100)")] int? bounces = null)
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "bake" => BakeSync(),
                    "bake_async" => BakeAsync(),
                    "cancel" => CancelBake(),
                    "status" => GetStatus(),
                    "get_settings" => GetSettings(),
                    "set_settings" => SetSettings(lightmapper, lightmapResolution, lightmapPadding, maxLightmapSize,
                        ambientOcclusion, aoMaxDistance, directionalMode, compressLightmaps, bounces),
                    "clear_baked_data" => ClearBakedData(),
                    "get_lights" => GetLights(),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"Lighting operation failed: {ex.Message}");
            }
        }

        private static object BakeSync()
        {
            if (Lightmapping.isRunning)
                throw new MCPException("A bake is already in progress. Use 'cancel' to stop it first.");

            bool success = Lightmapping.Bake();
            return new
            {
                success,
                message = success ? "Lightmap bake completed successfully." : "Lightmap bake failed.",
                lightmapCount = LightmapSettings.lightmaps.Length
            };
        }

        private static object BakeAsync()
        {
            if (Lightmapping.isRunning)
                throw new MCPException("A bake is already in progress. Use 'status' to check progress or 'cancel' to stop.");

            bool started = Lightmapping.BakeAsync();
            return new
            {
                success = started,
                message = started
                    ? "Async lightmap bake started. Use action 'status' to check progress."
                    : "Failed to start async bake."
            };
        }

        private static object CancelBake()
        {
            if (!Lightmapping.isRunning)
            {
                return new { success = true, message = "No bake in progress." };
            }

            Lightmapping.Cancel();
            return new { success = true, message = "Lightmap bake cancelled." };
        }

        private static object GetStatus()
        {
            float progress = Lightmapping.buildProgress;

            return new
            {
                success = true,
                isRunning = Lightmapping.isRunning,
                progress = Math.Round(progress * 100, 1),
                lightmapCount = LightmapSettings.lightmaps.Length,
                lightProbeCount = LightmapSettings.lightProbes?.positions?.Length ?? 0,
                message = Lightmapping.isRunning
                    ? $"Baking in progress: {progress * 100:F1}%"
                    : $"Not baking. {LightmapSettings.lightmaps.Length} lightmaps baked."
            };
        }

        private static object GetSettings()
        {
            var settings = LightmapEditorSettings.lightmapper;

            return new
            {
                success = true,
                lightmapper = settings.ToString(),
                lightmapResolution = LightmapEditorSettings.bakeResolution,
                lightmapPadding = LightmapEditorSettings.padding,
                maxLightmapSize = LightmapEditorSettings.maxAtlasSize,
                ambientOcclusion = LightmapEditorSettings.enableAmbientOcclusion,
                aoMaxDistance = LightmapEditorSettings.aoMaxDistance,
                directionalMode = LightmapSettings.lightmapsMode.ToString(),
                compressLightmaps = LightmapEditorSettings.textureCompression,
                bounces = Lightmapping.bounceBoost > 0 ? (int?)null : null,
                environmentLighting = new
                {
                    source = RenderSettings.ambientMode.ToString(),
                    ambientColor = RenderSettings.ambientMode == AmbientMode.Flat
                        ? ColorUtility.ToHtmlStringRGBA(RenderSettings.ambientLight)
                        : null,
                    skyColor = RenderSettings.ambientMode == AmbientMode.Trilight
                        ? ColorUtility.ToHtmlStringRGBA(RenderSettings.ambientSkyColor)
                        : null,
                    reflectionIntensity = RenderSettings.reflectionIntensity,
                    reflectionBounces = RenderSettings.reflectionBounces
                },
                bakedLightmaps = LightmapSettings.lightmaps.Length,
                lightProbePositions = LightmapSettings.lightProbes?.positions?.Length ?? 0
            };
        }

        private static object SetSettings(string lightmapper, int? resolution, int? padding, int? maxSize,
            bool? ao, float? aoMaxDist, string directionalMode, bool? compress, int? bounceCount)
        {
            var changes = new List<string>();

            if (!string.IsNullOrEmpty(lightmapper))
            {
                var lm = lightmapper switch
                {
                    "ProgressiveGPU" => LightmapEditorSettings.Lightmapper.ProgressiveGPU,
                    "ProgressiveCPU" => LightmapEditorSettings.Lightmapper.ProgressiveCPU,
                    _ => throw MCPException.InvalidParams($"Unknown lightmapper: '{lightmapper}'")
                };
                LightmapEditorSettings.lightmapper = lm;
                changes.Add($"lightmapper = {lightmapper}");
            }

            if (resolution.HasValue)
            {
                LightmapEditorSettings.bakeResolution = resolution.Value;
                changes.Add($"resolution = {resolution.Value}");
            }

            if (padding.HasValue)
            {
                LightmapEditorSettings.padding = padding.Value;
                changes.Add($"padding = {padding.Value}");
            }

            if (maxSize.HasValue)
            {
                LightmapEditorSettings.maxAtlasSize = maxSize.Value;
                changes.Add($"maxAtlasSize = {maxSize.Value}");
            }

            if (ao.HasValue)
            {
                LightmapEditorSettings.enableAmbientOcclusion = ao.Value;
                changes.Add($"ambientOcclusion = {ao.Value}");
            }

            if (aoMaxDist.HasValue)
            {
                LightmapEditorSettings.aoMaxDistance = aoMaxDist.Value;
                changes.Add($"aoMaxDistance = {aoMaxDist.Value}");
            }

            if (!string.IsNullOrEmpty(directionalMode))
            {
                var dm = directionalMode switch
                {
                    "NonDirectional" => LightmapsMode.NonDirectional,
                    "Directional" => LightmapsMode.CombinedDirectional,
                    _ => throw MCPException.InvalidParams($"Unknown directional mode: '{directionalMode}'")
                };
                LightmapSettings.lightmapsMode = dm;
                changes.Add($"directionalMode = {directionalMode}");
            }

            if (compress.HasValue)
            {
                LightmapEditorSettings.textureCompression = compress.Value;
                changes.Add($"compressLightmaps = {compress.Value}");
            }

            return new
            {
                success = true,
                changesApplied = changes,
                message = changes.Count > 0
                    ? $"Updated {changes.Count} lighting settings. Rebake to apply."
                    : "No settings changed."
            };
        }

        private static object ClearBakedData()
        {
            Lightmapping.Clear();
            Lightmapping.ClearDiskCache();
            Lightmapping.ClearLightingDataAsset();

            return new
            {
                success = true,
                message = "Cleared all baked lighting data, disk cache, and lighting data asset."
            };
        }

        private static object GetLights()
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var reflectionProbes = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
            var lightProbeGroups = UnityEngine.Object.FindObjectsByType<LightProbeGroup>(FindObjectsSortMode.None);

            return new
            {
                success = true,
                lights = lights.Select(l => new
                {
                    name = l.gameObject.name,
                    instanceId = l.gameObject.GetInstanceID(),
                    type = l.type.ToString(),
                    mode = l.lightmapBakeType.ToString(),
                    color = ColorUtility.ToHtmlStringRGBA(l.color),
                    intensity = l.intensity,
                    range = l.range,
                    shadows = l.shadows.ToString(),
                    enabled = l.enabled
                }).ToList(),
                reflectionProbes = reflectionProbes.Select(rp => new
                {
                    name = rp.gameObject.name,
                    instanceId = rp.gameObject.GetInstanceID(),
                    mode = rp.mode.ToString(),
                    boxSize = new { x = rp.size.x, y = rp.size.y, z = rp.size.z },
                    resolution = rp.resolution,
                    enabled = rp.enabled
                }).ToList(),
                lightProbeGroups = lightProbeGroups.Select(lpg => new
                {
                    name = lpg.gameObject.name,
                    instanceId = lpg.gameObject.GetInstanceID(),
                    probeCount = lpg.probePositions.Length,
                    enabled = lpg.enabled
                }).ToList(),
                summary = $"{lights.Length} lights, {reflectionProbes.Length} reflection probes, {lightProbeGroups.Length} light probe groups"
            };
        }
    }
}
