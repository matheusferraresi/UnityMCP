using System.Linq;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace MyProject.Editor.Resources
{
    /// <summary>
    /// Example: Expose project data as an MCP resource using [MCPResource].
    /// Resources provide read-only data that AI assistants can query.
    /// </summary>
    public static class MyCustomResource
    {
        [MCPResource("custom://project-stats", "Project Statistics",
            Description = "Returns a summary of GameObjects, scripts, and materials in the project",
            MimeType = "application/json")]
        public static object GetProjectStats()
        {
            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            return new
            {
                total_gameobjects = allObjects.Length,
                active_gameobjects = allObjects.Count(go => go.activeInHierarchy),
                root_objects = allObjects.Count(go => go.transform.parent == null),
                with_rigidbody = allObjects.Count(go => go.GetComponent<Rigidbody>() != null),
                with_collider = allObjects.Count(go => go.GetComponent<Collider>() != null),
                with_renderer = allObjects.Count(go => go.GetComponent<Renderer>() != null)
            };
        }
    }
}
