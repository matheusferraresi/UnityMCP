using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Steps physics simulation in edit mode and reports results.
    /// Allows agents to verify physics setups (collisions, raycasts, rigidbody placement)
    /// without entering play mode.
    /// </summary>
    public static class PhysicsSimulate
    {
        [MCPTool("physics_simulate", "Step physics simulation in edit mode: simulate physics, cast rays, check overlaps, and verify setups without entering play mode", Category = "Physics")]
        public static object Execute(
            [MCPParam("action", "Action: step (simulate N steps), raycast, sphere_cast, overlap_sphere, check_collision, get_rigidbodies", required: true,
                Enum = new[] { "step", "raycast", "sphere_cast", "overlap_sphere", "check_collision", "get_rigidbodies" })] string action,
            [MCPParam("steps", "Number of physics steps for 'step' action (default: 1, max: 300)")] int steps = 1,
            [MCPParam("delta_time", "Time delta per step in seconds (default: 0.02 = 50fps)")] float deltaTime = 0.02f,
            [MCPParam("origin", "Origin point as [x,y,z] for raycast/sphere_cast/overlap_sphere")] object origin = null,
            [MCPParam("direction", "Direction as [x,y,z] for raycast/sphere_cast")] object direction = null,
            [MCPParam("max_distance", "Max distance for raycast/sphere_cast (default: 100)")] float maxDistance = 100f,
            [MCPParam("radius", "Radius for sphere_cast/overlap_sphere (default: 1)")] float radius = 1f,
            [MCPParam("layer_mask", "Layer mask name or number (default: all layers)")] string layerMask = null,
            [MCPParam("track_objects", "Instance IDs or names of objects to track positions during 'step'")] List<string> trackObjects = null)
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "step" => StepSimulation(steps, deltaTime, trackObjects),
                    "raycast" => DoRaycast(origin, direction, maxDistance, layerMask),
                    "sphere_cast" => DoSphereCast(origin, direction, radius, maxDistance, layerMask),
                    "overlap_sphere" => DoOverlapSphere(origin, radius, layerMask),
                    "check_collision" => CheckCollision(),
                    "get_rigidbodies" => GetRigidbodies(),
                    _ => throw MCPException.InvalidParams($"Unknown action: '{action}'")
                };
            }
            catch (MCPException) { throw; }
            catch (Exception ex)
            {
                throw new MCPException($"Physics operation failed: {ex.Message}");
            }
        }

        private static object StepSimulation(int steps, float deltaTime, List<string> trackObjects)
        {
            steps = Mathf.Clamp(steps, 1, 300);
            deltaTime = Mathf.Clamp(deltaTime, 0.001f, 0.1f);

            // Auto-sync transforms before simulation
            Physics.SyncTransforms();

            // Resolve tracked objects
            var trackedBodies = new List<TrackedObject>();
            if (trackObjects != null)
            {
                foreach (var id in trackObjects)
                {
                    GameObject go = null;
                    if (int.TryParse(id, out int instanceId))
                        go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                    if (go == null)
                        go = GameObject.Find(id);

                    if (go != null)
                    {
                        trackedBodies.Add(new TrackedObject
                        {
                            name = go.name,
                            instanceId = go.GetInstanceID(),
                            startPosition = go.transform.position,
                            go = go
                        });
                    }
                }
            }

            // Record undo for all rigidbodies
            var rigidbodies = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            foreach (var rb in rigidbodies)
                Undo.RecordObject(rb.transform, "Physics Simulate");

            // Step simulation
            for (int i = 0; i < steps; i++)
            {
                Physics.Simulate(deltaTime);
            }

            // Collect results for tracked objects
            var trackResults = trackedBodies.Select(t => new
            {
                t.name,
                t.instanceId,
                startPosition = FormatVector3(t.startPosition),
                endPosition = FormatVector3(t.go.transform.position),
                displacement = FormatVector3(t.go.transform.position - t.startPosition),
                distance = Vector3.Distance(t.startPosition, t.go.transform.position)
            }).ToList();

            return new
            {
                success = true,
                steps,
                deltaTime,
                totalTime = steps * deltaTime,
                rigidbodyCount = rigidbodies.Length,
                tracked = trackResults.Count > 0 ? trackResults : null,
                message = $"Simulated {steps} steps ({steps * deltaTime:F3}s). {rigidbodies.Length} rigidbodies affected. Use undo to revert."
            };
        }

        private static object DoRaycast(object origin, object direction, float maxDistance, string layerMask)
        {
            var o = ParseVector3(origin) ?? throw MCPException.InvalidParams("origin is required for raycast.");
            var d = ParseVector3(direction) ?? throw MCPException.InvalidParams("direction is required for raycast.");
            int mask = ParseLayerMask(layerMask);

            Physics.SyncTransforms();
            var hits = Physics.RaycastAll(o, d.normalized, maxDistance, mask);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            return new
            {
                success = true,
                hitCount = hits.Length,
                origin = FormatVector3(o),
                direction = FormatVector3(d.normalized),
                maxDistance,
                hits = hits.Select(h => new
                {
                    objectName = h.collider.gameObject.name,
                    instanceId = h.collider.gameObject.GetInstanceID(),
                    point = FormatVector3(h.point),
                    normal = FormatVector3(h.normal),
                    distance = h.distance,
                    colliderType = h.collider.GetType().Name
                }).ToList()
            };
        }

        private static object DoSphereCast(object origin, object direction, float radius, float maxDistance, string layerMask)
        {
            var o = ParseVector3(origin) ?? throw MCPException.InvalidParams("origin is required for sphere_cast.");
            var d = ParseVector3(direction) ?? throw MCPException.InvalidParams("direction is required for sphere_cast.");
            int mask = ParseLayerMask(layerMask);

            Physics.SyncTransforms();
            var hits = Physics.SphereCastAll(o, radius, d.normalized, maxDistance, mask);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            return new
            {
                success = true,
                hitCount = hits.Length,
                radius,
                hits = hits.Select(h => new
                {
                    objectName = h.collider.gameObject.name,
                    instanceId = h.collider.gameObject.GetInstanceID(),
                    point = FormatVector3(h.point),
                    distance = h.distance
                }).ToList()
            };
        }

        private static object DoOverlapSphere(object origin, float radius, string layerMask)
        {
            var o = ParseVector3(origin) ?? throw MCPException.InvalidParams("origin is required for overlap_sphere.");
            int mask = ParseLayerMask(layerMask);

            Physics.SyncTransforms();
            var colliders = Physics.OverlapSphere(o, radius, mask);

            return new
            {
                success = true,
                overlapCount = colliders.Length,
                center = FormatVector3(o),
                radius,
                overlapping = colliders.Select(c => new
                {
                    objectName = c.gameObject.name,
                    instanceId = c.gameObject.GetInstanceID(),
                    colliderType = c.GetType().Name,
                    distance = Vector3.Distance(o, c.transform.position)
                }).OrderBy(x => x.distance).ToList()
            };
        }

        private static object CheckCollision()
        {
            Physics.SyncTransforms();

            var colliders = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
            var overlaps = new List<object>();

            for (int i = 0; i < colliders.Length; i++)
            {
                for (int j = i + 1; j < colliders.Length; j++)
                {
                    if (Physics.ComputePenetration(
                        colliders[i], colliders[i].transform.position, colliders[i].transform.rotation,
                        colliders[j], colliders[j].transform.position, colliders[j].transform.rotation,
                        out Vector3 dir, out float dist))
                    {
                        overlaps.Add(new
                        {
                            objectA = colliders[i].gameObject.name,
                            objectB = colliders[j].gameObject.name,
                            penetrationDistance = dist,
                            separationDirection = FormatVector3(dir)
                        });
                    }

                    // Limit checks to prevent long running
                    if (overlaps.Count >= 50) break;
                }
                if (overlaps.Count >= 50) break;
            }

            return new
            {
                success = true,
                totalColliders = colliders.Length,
                overlappingPairs = overlaps.Count,
                overlaps = overlaps.Count > 0 ? overlaps : null,
                message = overlaps.Count == 0
                    ? $"No overlapping colliders found among {colliders.Length} colliders."
                    : $"Found {overlaps.Count} overlapping pairs."
            };
        }

        private static object GetRigidbodies()
        {
            var rigidbodies = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);

            return new
            {
                success = true,
                count = rigidbodies.Length,
                rigidbodies = rigidbodies.Select(rb => new
                {
                    name = rb.gameObject.name,
                    instanceId = rb.gameObject.GetInstanceID(),
                    position = FormatVector3(rb.transform.position),
                    mass = rb.mass,
                    useGravity = rb.useGravity,
                    isKinematic = rb.isKinematic,
                    velocity = FormatVector3(rb.linearVelocity),
                    angularVelocity = FormatVector3(rb.angularVelocity),
                    constraints = rb.constraints.ToString(),
                    colliderCount = rb.GetComponents<Collider>().Length
                }).ToList()
            };
        }

        #region Helpers

        private static Vector3? ParseVector3(object data)
        {
            if (data == null) return null;
            if (data is Newtonsoft.Json.Linq.JArray arr && arr.Count >= 3)
                return new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
            if (data is Newtonsoft.Json.Linq.JObject obj)
                return new Vector3(obj.Value<float>("x"), obj.Value<float>("y"), obj.Value<float>("z"));
            return null;
        }

        private static object FormatVector3(Vector3 v) => new { x = Math.Round(v.x, 4), y = Math.Round(v.y, 4), z = Math.Round(v.z, 4) };

        private static int ParseLayerMask(string layerMask)
        {
            if (string.IsNullOrEmpty(layerMask)) return ~0; // All layers
            if (int.TryParse(layerMask, out int mask)) return mask;
            int layer = LayerMask.NameToLayer(layerMask);
            return layer >= 0 ? 1 << layer : ~0;
        }

        private class TrackedObject
        {
            public string name;
            public int instanceId;
            public Vector3 startPosition;
            public GameObject go;
        }

        #endregion
    }
}
