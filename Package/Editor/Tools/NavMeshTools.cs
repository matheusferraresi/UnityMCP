#if UNITY_MCP_AI_NAVIGATION
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnixxtyMCP.Editor.Core;
using EditorNavMeshBuilder = UnityEditor.AI.NavMeshBuilder;

namespace UnixxtyMCP.Editor.Tools
{
    public static class NavMeshTools
    {
        [MCPTool("navmesh_manage", "Manage NavMesh: bake, clear, query paths, configure agents and areas",
            Category = "Navigation", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "bake", "clear", "status", "query_path", "get_settings", "set_settings",
                               "get_areas", "get_agents", "add_surface", "get_surfaces" })] string action,
            [MCPParam("start_x", "Start position X (for query_path)")] float startX = 0,
            [MCPParam("start_y", "Start position Y (for query_path)")] float startY = 0,
            [MCPParam("start_z", "Start position Z (for query_path)")] float startZ = 0,
            [MCPParam("end_x", "End position X (for query_path)")] float endX = 0,
            [MCPParam("end_y", "End position Y (for query_path)")] float endY = 0,
            [MCPParam("end_z", "End position Z (for query_path)")] float endZ = 0,
            [MCPParam("agent_radius", "Agent radius (for set_settings)")] float agentRadius = -1,
            [MCPParam("agent_height", "Agent height (for set_settings)")] float agentHeight = -1,
            [MCPParam("step_height", "Max step height (for set_settings)")] float stepHeight = -1,
            [MCPParam("slope_angle", "Max slope angle in degrees (for set_settings)")] float slopeAngle = -1,
            [MCPParam("target", "GameObject name/path/ID (for add_surface)")] string target = null,
            [MCPParam("area_name", "NavMesh area name (for add_surface)")] string areaName = null)
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "bake" => BakeNavMesh(),
                    "clear" => ClearNavMesh(),
                    "status" => GetStatus(),
                    "query_path" => QueryPath(startX, startY, startZ, endX, endY, endZ),
                    "get_settings" => GetSettings(),
                    "set_settings" => SetSettings(agentRadius, agentHeight, stepHeight, slopeAngle),
                    "get_areas" => GetAreas(),
                    "get_agents" => GetAgents(),
                    "add_surface" => AddSurface(target, areaName),
                    "get_surfaces" => GetSurfaces(),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"NavMesh operation failed: {ex.Message}");
            }
        }

        private static object BakeNavMesh()
        {
            var startTime = DateTime.UtcNow;
            EditorNavMeshBuilder.BuildNavMesh();
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

            var triangulation = NavMesh.CalculateTriangulation();
            return new
            {
                success = true,
                message = "NavMesh baked successfully.",
                bake_time_ms = elapsed,
                vertices = triangulation.vertices.Length,
                triangles = triangulation.indices.Length / 3
            };
        }

        private static object ClearNavMesh()
        {
            EditorNavMeshBuilder.ClearAllNavMeshes();
            return new { success = true, message = "All NavMesh data cleared." };
        }

        private static object GetStatus()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            bool hasNavMesh = triangulation.vertices.Length > 0;

            var surfaces = UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None);

            return new
            {
                success = true,
                has_navmesh = hasNavMesh,
                vertices = triangulation.vertices.Length,
                triangles = triangulation.indices.Length / 3,
                surface_count = surfaces.Length,
                settings = GetSettingsData()
            };
        }

        private static object QueryPath(float sx, float sy, float sz, float ex, float ey, float ez)
        {
            var start = new Vector3(sx, sy, sz);
            var end = new Vector3(ex, ey, ez);

            NavMeshHit startHit, endHit;
            bool startValid = NavMesh.SamplePosition(start, out startHit, 10f, NavMesh.AllAreas);
            bool endValid = NavMesh.SamplePosition(end, out endHit, 10f, NavMesh.AllAreas);

            if (!startValid)
                return new { success = false, error = "Start position is not on the NavMesh.", start = new { x = sx, y = sy, z = sz } };
            if (!endValid)
                return new { success = false, error = "End position is not on the NavMesh.", end = new { x = ex, y = ey, z = ez } };

            var path = new NavMeshPath();
            bool found = NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path);

            float totalDistance = 0;
            for (int i = 1; i < path.corners.Length; i++)
                totalDistance += Vector3.Distance(path.corners[i - 1], path.corners[i]);

            return new
            {
                success = true,
                path_found = found,
                status = path.status.ToString(),
                corner_count = path.corners.Length,
                total_distance = totalDistance,
                corners = path.corners.Select(c => new { x = c.x, y = c.y, z = c.z }).ToArray(),
                start_snapped = new { x = startHit.position.x, y = startHit.position.y, z = startHit.position.z },
                end_snapped = new { x = endHit.position.x, y = endHit.position.y, z = endHit.position.z }
            };
        }

        private static object GetSettings()
        {
            return new { success = true, settings = GetSettingsData() };
        }

        private static object GetSettingsData()
        {
            var settings = NavMesh.GetSettingsByID(0);
            return new
            {
                agent_radius = settings.agentRadius,
                agent_height = settings.agentHeight,
                agent_climb = settings.agentClimb,
                agent_slope = settings.agentSlope
            };
        }

        private static object SetSettings(float radius, float height, float stepH, float slope)
        {
            var serialized = new SerializedObject(EditorNavMeshBuilder.navMeshSettingsObject);

            if (radius >= 0)
            {
                var prop = serialized.FindProperty("m_BuildSettings.agentRadius");
                if (prop != null) prop.floatValue = radius;
            }
            if (height >= 0)
            {
                var prop = serialized.FindProperty("m_BuildSettings.agentHeight");
                if (prop != null) prop.floatValue = height;
            }
            if (stepH >= 0)
            {
                var prop = serialized.FindProperty("m_BuildSettings.agentClimb");
                if (prop != null) prop.floatValue = stepH;
            }
            if (slope >= 0)
            {
                var prop = serialized.FindProperty("m_BuildSettings.agentSlope");
                if (prop != null) prop.floatValue = slope;
            }

            serialized.ApplyModifiedProperties();
            return new { success = true, message = "NavMesh settings updated.", settings = GetSettingsData() };
        }

        private static object GetAreas()
        {
            var areas = new List<object>();
            for (int i = 0; i < 32; i++)
            {
                string name = GameObjectUtility.GetNavMeshAreaNames().FirstOrDefault(n =>
                    NavMesh.GetAreaFromName(n) == i);
                if (!string.IsNullOrEmpty(name))
                {
                    areas.Add(new { index = i, name, cost = NavMesh.GetAreaCost(i) });
                }
            }
            return new { success = true, areas, count = areas.Count };
        }

        private static object GetAgents()
        {
            var agents = new List<object>();
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var settings = NavMesh.GetSettingsByIndex(i);
                string name = NavMesh.GetSettingsNameFromID(settings.agentTypeID);
                agents.Add(new
                {
                    id = settings.agentTypeID,
                    name,
                    radius = settings.agentRadius,
                    height = settings.agentHeight,
                    climb = settings.agentClimb,
                    slope = settings.agentSlope
                });
            }
            return new { success = true, agents, count = agents.Count };
        }

        private static object AddSurface(string target, string areaName)
        {
            if (string.IsNullOrEmpty(target))
                throw MCPException.InvalidParams("'target' is required for add_surface.");

            var go = GameObjectResolver.Resolve(target);
            if (go == null)
                throw MCPException.InvalidParams($"GameObject '{target}' not found.");

            var surface = go.GetComponent<NavMeshSurface>();
            if (surface == null)
                surface = go.AddComponent<NavMeshSurface>();

            if (!string.IsNullOrEmpty(areaName))
            {
                int areaIndex = NavMesh.GetAreaFromName(areaName);
                if (areaIndex >= 0)
                    surface.defaultArea = areaIndex;
            }

            EditorUtility.SetDirty(go);
            return new
            {
                success = true,
                message = $"NavMeshSurface added to '{go.name}'.",
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                default_area = surface.defaultArea
            };
        }

        private static object GetSurfaces()
        {
            var surfaces = UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None);
            return new
            {
                success = true,
                surfaces = surfaces.Select(s => new
                {
                    gameObject = s.gameObject.name,
                    instanceId = s.gameObject.GetInstanceID(),
                    default_area = s.defaultArea,
                    collect_objects = s.collectObjects.ToString(),
                    use_geometry = s.useGeometry.ToString()
                }).ToArray(),
                count = surfaces.Length
            };
        }
    }
}
#endif
