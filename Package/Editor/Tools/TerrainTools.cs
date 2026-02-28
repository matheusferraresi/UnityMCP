using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    public static class TerrainTools
    {
        [MCPTool("terrain_manage", "Manage terrain: create, modify heightmaps, paint textures, place trees and details",
            Category = "Terrain", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "create", "get_info", "set_height", "flatten", "smooth",
                               "get_layers", "add_layer", "paint_texture",
                               "add_tree", "get_trees", "clear_trees",
                               "set_detail_density", "get_terrains" })] string action,
            [MCPParam("width", "Terrain width in world units (for create)", Minimum = 1)] int width = 500,
            [MCPParam("length", "Terrain length in world units (for create)", Minimum = 1)] int length = 500,
            [MCPParam("height", "Terrain max height (for create)", Minimum = 1)] int height = 100,
            [MCPParam("heightmap_resolution", "Heightmap resolution (power of 2 + 1)", Minimum = 33)] int heightmapRes = 513,
            [MCPParam("target", "Target terrain GameObject name/path/ID")] string target = null,
            [MCPParam("x", "X position (normalized 0-1 for heightmap, world for trees)")] float x = 0.5f,
            [MCPParam("y", "Y value (height 0-1, or world Y for trees)")] float y = 0,
            [MCPParam("z", "Z position (normalized 0-1 for heightmap, world for trees)")] float z = 0.5f,
            [MCPParam("radius", "Brush radius (normalized 0-1)", Minimum = 0)] float radius = 0.1f,
            [MCPParam("strength", "Operation strength (0-1)", Minimum = 0, Maximum = 1)] float strength = 1f,
            [MCPParam("layer_index", "Terrain layer index (for paint_texture)")] int layerIndex = 0,
            [MCPParam("texture_path", "Texture asset path (for add_layer)")] string texturePath = null,
            [MCPParam("prefab_path", "Tree prefab path (for add_tree)")] string prefabPath = null,
            [MCPParam("count", "Number of items to place (for add_tree)")] int count = 1,
            [MCPParam("name", "Name for the terrain (for create)")] string name = "Terrain",
            [MCPParam("save_path", "Asset save path (for create)")] string savePath = null)
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "create" => CreateTerrain(name, width, length, height, heightmapRes, savePath),
                    "get_info" => GetTerrainInfo(target),
                    "set_height" => SetHeight(target, x, z, radius, y),
                    "flatten" => Flatten(target, y),
                    "smooth" => SmoothTerrain(target, x, z, radius, strength),
                    "get_layers" => GetLayers(target),
                    "add_layer" => AddLayer(target, texturePath),
                    "paint_texture" => PaintTexture(target, x, z, radius, strength, layerIndex),
                    "add_tree" => AddTree(target, prefabPath, x, z, count),
                    "get_trees" => GetTrees(target),
                    "clear_trees" => ClearTrees(target),
                    "set_detail_density" => SetDetailDensity(target, x, z, radius, strength),
                    "get_terrains" => GetAllTerrains(),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"Terrain operation failed: {ex.Message}");
            }
        }

        private static Terrain ResolveTerrain(string target)
        {
            Terrain terrain;
            if (string.IsNullOrEmpty(target))
            {
                terrain = Terrain.activeTerrain;
                if (terrain == null)
                    throw MCPException.InvalidParams("No active terrain found. Specify 'target' or create a terrain first.");
            }
            else
            {
                var go = GameObjectResolver.Resolve(target);
                if (go == null)
                    throw MCPException.InvalidParams($"GameObject '{target}' not found.");
                terrain = go.GetComponent<Terrain>();
                if (terrain == null)
                    throw MCPException.InvalidParams($"'{target}' does not have a Terrain component.");
            }
            return terrain;
        }

        private static object CreateTerrain(string name, int w, int l, int h, int hmRes, string savePath)
        {
            var terrainData = new TerrainData();
            terrainData.heightmapResolution = hmRes;
            terrainData.size = new Vector3(w, h, l);

            string assetPath = savePath ?? $"Assets/{name}.asset";
            AssetDatabase.CreateAsset(terrainData, assetPath);

            var go = Terrain.CreateTerrainGameObject(terrainData);
            go.name = name;

            Undo.RegisterCreatedObjectUndo(go, $"Create Terrain '{name}'");
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Terrain '{name}' created.",
                instanceId = go.GetInstanceID(),
                asset_path = assetPath,
                size = new { x = w, y = h, z = l },
                heightmap_resolution = hmRes
            };
        }

        private static object GetTerrainInfo(string target)
        {
            var terrain = ResolveTerrain(target);
            var data = terrain.terrainData;

            return new
            {
                success = true,
                name = terrain.gameObject.name,
                instanceId = terrain.gameObject.GetInstanceID(),
                position = new { x = terrain.transform.position.x, y = terrain.transform.position.y, z = terrain.transform.position.z },
                size = new { x = data.size.x, y = data.size.y, z = data.size.z },
                heightmap_resolution = data.heightmapResolution,
                alphamap_resolution = data.alphamapResolution,
                detail_resolution = data.detailResolution,
                layer_count = data.terrainLayers?.Length ?? 0,
                tree_prototype_count = data.treePrototypes?.Length ?? 0,
                tree_instance_count = data.treeInstanceCount
            };
        }

        private static object SetHeight(string target, float nx, float nz, float r, float h)
        {
            var terrain = ResolveTerrain(target);
            var data = terrain.terrainData;
            int res = data.heightmapResolution;

            int cx = Mathf.RoundToInt(nx * res);
            int cz = Mathf.RoundToInt(nz * res);
            int pixelRadius = Mathf.Max(1, Mathf.RoundToInt(r * res));

            int xMin = Mathf.Max(0, cx - pixelRadius);
            int zMin = Mathf.Max(0, cz - pixelRadius);
            int xMax = Mathf.Min(res, cx + pixelRadius);
            int zMax = Mathf.Min(res, cz + pixelRadius);
            int w = xMax - xMin;
            int ht = zMax - zMin;

            if (w <= 0 || ht <= 0) return new { success = false, error = "Brush out of bounds." };

            var heights = data.GetHeights(xMin, zMin, w, ht);
            for (int iz = 0; iz < ht; iz++)
            {
                for (int ix = 0; ix < w; ix++)
                {
                    float dist = Vector2.Distance(new Vector2(ix + xMin, iz + zMin), new Vector2(cx, cz));
                    if (dist <= pixelRadius)
                    {
                        float falloff = 1f - (dist / pixelRadius);
                        heights[iz, ix] = Mathf.Lerp(heights[iz, ix], h, falloff);
                    }
                }
            }

            Undo.RecordObject(data, "Set Terrain Height");
            data.SetHeights(xMin, zMin, heights);

            return new { success = true, message = $"Height set at ({nx:F2}, {nz:F2}) with radius {r:F2}." };
        }

        private static object Flatten(string target, float h)
        {
            var terrain = ResolveTerrain(target);
            var data = terrain.terrainData;
            int res = data.heightmapResolution;

            var heights = new float[res, res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    heights[z, x] = h;

            Undo.RecordObject(data, "Flatten Terrain");
            data.SetHeights(0, 0, heights);

            return new { success = true, message = $"Terrain flattened to height {h:F3}." };
        }

        private static object SmoothTerrain(string target, float nx, float nz, float r, float str)
        {
            var terrain = ResolveTerrain(target);
            var data = terrain.terrainData;
            int res = data.heightmapResolution;

            int cx = Mathf.RoundToInt(nx * res);
            int cz = Mathf.RoundToInt(nz * res);
            int pixelRadius = Mathf.Max(2, Mathf.RoundToInt(r * res));

            int xMin = Mathf.Max(1, cx - pixelRadius);
            int zMin = Mathf.Max(1, cz - pixelRadius);
            int xMax = Mathf.Min(res - 1, cx + pixelRadius);
            int zMax = Mathf.Min(res - 1, cz + pixelRadius);
            int w = xMax - xMin;
            int h = zMax - zMin;

            if (w <= 0 || h <= 0) return new { success = false, error = "Brush out of bounds." };

            var heights = data.GetHeights(xMin - 1, zMin - 1, w + 2, h + 2);
            var smoothed = new float[h, w];

            for (int iz = 0; iz < h; iz++)
            {
                for (int ix = 0; ix < w; ix++)
                {
                    float avg = (heights[iz, ix] + heights[iz, ix + 1] + heights[iz, ix + 2] +
                                 heights[iz + 1, ix] + heights[iz + 1, ix + 1] + heights[iz + 1, ix + 2] +
                                 heights[iz + 2, ix] + heights[iz + 2, ix + 1] + heights[iz + 2, ix + 2]) / 9f;
                    smoothed[iz, ix] = Mathf.Lerp(heights[iz + 1, ix + 1], avg, str);
                }
            }

            Undo.RecordObject(data, "Smooth Terrain");
            data.SetHeights(xMin, zMin, smoothed);

            return new { success = true, message = $"Terrain smoothed at ({nx:F2}, {nz:F2}) with radius {r:F2}." };
        }

        private static object GetLayers(string target)
        {
            var terrain = ResolveTerrain(target);
            var layers = terrain.terrainData.terrainLayers ?? new TerrainLayer[0];

            return new
            {
                success = true,
                layers = layers.Select((l, i) => new
                {
                    index = i,
                    name = l?.name,
                    diffuse_texture = l?.diffuseTexture?.name,
                    tile_size = l != null ? new { x = l.tileSize.x, y = l.tileSize.y } : null
                }).ToArray(),
                count = layers.Length
            };
        }

        private static object AddLayer(string target, string texPath)
        {
            if (string.IsNullOrEmpty(texPath))
                throw MCPException.InvalidParams("'texture_path' is required for add_layer.");

            var terrain = ResolveTerrain(target);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
                throw MCPException.InvalidParams($"Texture not found at '{texPath}'.");

            var layer = new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(10, 10) };
            string layerPath = $"Assets/TerrainLayer_{tex.name}.asset";
            AssetDatabase.CreateAsset(layer, layerPath);

            var existing = terrain.terrainData.terrainLayers?.ToList() ?? new System.Collections.Generic.List<TerrainLayer>();
            existing.Add(layer);

            Undo.RecordObject(terrain.terrainData, "Add Terrain Layer");
            terrain.terrainData.terrainLayers = existing.ToArray();
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Terrain layer added with texture '{tex.name}'.",
                layer_index = existing.Count - 1,
                layer_asset = layerPath
            };
        }

        private static object PaintTexture(string target, float nx, float nz, float r, float str, int layerIdx)
        {
            var terrain = ResolveTerrain(target);
            var data = terrain.terrainData;
            int res = data.alphamapResolution;
            int layerCount = data.alphamapLayers;

            if (layerIdx < 0 || layerIdx >= layerCount)
                throw MCPException.InvalidParams($"Layer index {layerIdx} out of range (0-{layerCount - 1}).");

            int cx = Mathf.RoundToInt(nx * res);
            int cz = Mathf.RoundToInt(nz * res);
            int pixelRadius = Mathf.Max(1, Mathf.RoundToInt(r * res));

            int xMin = Mathf.Max(0, cx - pixelRadius);
            int zMin = Mathf.Max(0, cz - pixelRadius);
            int xMax = Mathf.Min(res, cx + pixelRadius);
            int zMax = Mathf.Min(res, cz + pixelRadius);
            int w = xMax - xMin;
            int h = zMax - zMin;

            if (w <= 0 || h <= 0) return new { success = false, error = "Brush out of bounds." };

            var alphamaps = data.GetAlphamaps(xMin, zMin, w, h);

            for (int iz = 0; iz < h; iz++)
            {
                for (int ix = 0; ix < w; ix++)
                {
                    float dist = Vector2.Distance(new Vector2(ix + xMin, iz + zMin), new Vector2(cx, cz));
                    if (dist <= pixelRadius)
                    {
                        float falloff = (1f - dist / pixelRadius) * str;
                        for (int l = 0; l < layerCount; l++)
                            alphamaps[iz, ix, l] *= (1f - falloff);
                        alphamaps[iz, ix, layerIdx] += falloff;

                        // Normalize
                        float total = 0;
                        for (int l = 0; l < layerCount; l++) total += alphamaps[iz, ix, l];
                        if (total > 0)
                            for (int l = 0; l < layerCount; l++) alphamaps[iz, ix, l] /= total;
                    }
                }
            }

            Undo.RecordObject(data, "Paint Terrain Texture");
            data.SetAlphamaps(xMin, zMin, alphamaps);

            return new { success = true, message = $"Painted layer {layerIdx} at ({nx:F2}, {nz:F2}) with radius {r:F2}." };
        }

        private static object AddTree(string target, string prefab, float nx, float nz, int count)
        {
            if (string.IsNullOrEmpty(prefab))
                throw MCPException.InvalidParams("'prefab_path' is required for add_tree.");

            var terrain = ResolveTerrain(target);
            var treePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            if (treePrefab == null)
                throw MCPException.InvalidParams($"Tree prefab not found at '{prefab}'.");

            var data = terrain.terrainData;
            var prototypes = data.treePrototypes?.ToList() ?? new System.Collections.Generic.List<TreePrototype>();
            int protoIndex = prototypes.FindIndex(p => p.prefab == treePrefab);
            if (protoIndex < 0)
            {
                prototypes.Add(new TreePrototype { prefab = treePrefab });
                data.treePrototypes = prototypes.ToArray();
                protoIndex = prototypes.Count - 1;
            }

            Undo.RecordObject(data, "Add Trees");
            for (int i = 0; i < count; i++)
            {
                float tx = nx + UnityEngine.Random.Range(-0.05f, 0.05f);
                float tz = nz + UnityEngine.Random.Range(-0.05f, 0.05f);
                var instance = new TreeInstance
                {
                    prototypeIndex = protoIndex,
                    position = new Vector3(Mathf.Clamp01(tx), 0, Mathf.Clamp01(tz)),
                    widthScale = 1f,
                    heightScale = 1f,
                    color = Color.white,
                    lightmapColor = Color.white
                };
                terrain.AddTreeInstance(instance);
            }

            terrain.Flush();
            return new
            {
                success = true,
                message = $"Added {count} tree(s) at ({nx:F2}, {nz:F2}).",
                prototype_index = protoIndex,
                total_trees = data.treeInstanceCount
            };
        }

        private static object GetTrees(string target)
        {
            var terrain = ResolveTerrain(target);
            var data = terrain.terrainData;

            return new
            {
                success = true,
                prototypes = data.treePrototypes?.Select((p, i) => new
                {
                    index = i,
                    prefab = p.prefab?.name
                }).ToArray(),
                tree_count = data.treeInstanceCount,
                prototype_count = data.treePrototypes?.Length ?? 0
            };
        }

        private static object ClearTrees(string target)
        {
            var terrain = ResolveTerrain(target);
            Undo.RecordObject(terrain.terrainData, "Clear Trees");
            terrain.terrainData.SetTreeInstances(new TreeInstance[0], true);
            terrain.Flush();
            return new { success = true, message = "All trees cleared." };
        }

        private static object SetDetailDensity(string target, float nx, float nz, float r, float str)
        {
            var terrain = ResolveTerrain(target);
            var data = terrain.terrainData;
            int res = data.detailResolution;

            if (data.detailPrototypes == null || data.detailPrototypes.Length == 0)
                return new { success = false, error = "No detail prototypes defined on this terrain." };

            int cx = Mathf.RoundToInt(nx * res);
            int cz = Mathf.RoundToInt(nz * res);
            int pixelRadius = Mathf.Max(1, Mathf.RoundToInt(r * res));
            int density = Mathf.RoundToInt(str * 16);

            int xMin = Mathf.Max(0, cx - pixelRadius);
            int zMin = Mathf.Max(0, cz - pixelRadius);
            int xMax = Mathf.Min(res, cx + pixelRadius);
            int zMax = Mathf.Min(res, cz + pixelRadius);
            int w = xMax - xMin;
            int h = zMax - zMin;

            if (w <= 0 || h <= 0) return new { success = false, error = "Brush out of bounds." };

            Undo.RecordObject(data, "Set Detail Density");
            var details = data.GetDetailLayer(xMin, zMin, w, h, 0);
            for (int iz = 0; iz < h; iz++)
                for (int ix = 0; ix < w; ix++)
                {
                    float dist = Vector2.Distance(new Vector2(ix + xMin, iz + zMin), new Vector2(cx, cz));
                    if (dist <= pixelRadius) details[iz, ix] = density;
                }

            data.SetDetailLayer(xMin, zMin, 0, details);
            return new { success = true, message = $"Detail density set at ({nx:F2}, {nz:F2})." };
        }

        private static object GetAllTerrains()
        {
            var terrains = Terrain.activeTerrains;
            return new
            {
                success = true,
                terrains = terrains.Select(t => new
                {
                    name = t.gameObject.name,
                    instanceId = t.gameObject.GetInstanceID(),
                    position = new { x = t.transform.position.x, y = t.transform.position.y, z = t.transform.position.z },
                    size = new { x = t.terrainData.size.x, y = t.terrainData.size.y, z = t.terrainData.size.z },
                    is_active = t == Terrain.activeTerrain
                }).ToArray(),
                count = terrains.Length
            };
        }
    }
}
