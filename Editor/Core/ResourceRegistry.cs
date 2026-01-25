using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Registry for discovering and invoking MCP resources marked with [MCPResource] attribute.
    /// </summary>
    public static class ResourceRegistry
    {
        private static Dictionary<string, ResourceInfo> _resources = new Dictionary<string, ResourceInfo>();
        private static bool _isInitialized = false;

        /// <summary>
        /// Gets the number of registered resources.
        /// </summary>
        public static int Count => _resources.Count;

        /// <summary>
        /// Auto-discover resources when the editor loads.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void DiscoverResources()
        {
            RefreshResources();
        }

        /// <summary>
        /// Manually refresh the resource registry. Useful for testing or after loading new assemblies.
        /// </summary>
        public static void RefreshResources()
        {
            _resources.Clear();
            _isInitialized = false;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Skip system and Unity assemblies for performance
                string assemblyName = assembly.FullName;
                if (assemblyName.StartsWith("System", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("Unity.", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("mscorlib", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("netstandard", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("Mono.", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    DiscoverResourcesInAssembly(assembly);
                }
                catch (ReflectionTypeLoadException reflectionException)
                {
                    Debug.LogWarning($"[ResourceRegistry] Failed to load types from assembly {assembly.GetName().Name}: {reflectionException.Message}");
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[ResourceRegistry] Error scanning assembly {assembly.GetName().Name}: {exception.Message}");
                }
            }

            _isInitialized = true;
            Debug.Log($"[ResourceRegistry] Discovered {_resources.Count} MCP resources");
        }

        private static void DiscoverResourcesInAssembly(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    try
                    {
                        var resourceAttribute = method.GetCustomAttribute<MCPResourceAttribute>();
                        if (resourceAttribute != null)
                        {
                            // Validate that the method has no required parameters
                            var parameters = method.GetParameters();
                            if (parameters.Length > 0 && parameters.Any(p => !p.HasDefaultValue))
                            {
                                Debug.LogWarning($"[ResourceRegistry] Resource method {type.FullName}.{method.Name} has required parameters. Resource methods should have no required parameters. Skipping.");
                                continue;
                            }

                            if (_resources.ContainsKey(resourceAttribute.Uri))
                            {
                                Debug.LogWarning($"[ResourceRegistry] Duplicate resource URI '{resourceAttribute.Uri}' found in {type.FullName}.{method.Name}. Skipping.");
                                continue;
                            }

                            var resourceInfo = new ResourceInfo(resourceAttribute, method);
                            _resources[resourceAttribute.Uri] = resourceInfo;
                        }
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"[ResourceRegistry] Error processing method {type.FullName}.{method.Name}: {exception.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Gets all resource definitions for the MCP resources/list response.
        /// </summary>
        public static IEnumerable<ResourceDefinition> GetDefinitions()
        {
            EnsureInitialized();
            return _resources.Values.Select(resourceInfo => resourceInfo.ToDefinition());
        }

        /// <summary>
        /// Gets a specific resource definition by URI.
        /// </summary>
        public static ResourceDefinition GetDefinition(string uri)
        {
            EnsureInitialized();
            if (_resources.TryGetValue(uri, out var resourceInfo))
            {
                return resourceInfo.ToDefinition();
            }
            return null;
        }

        /// <summary>
        /// Checks if a resource with the given URI exists.
        /// </summary>
        public static bool HasResource(string uri)
        {
            EnsureInitialized();
            return _resources.ContainsKey(uri);
        }

        /// <summary>
        /// Invokes a resource by URI and returns its content.
        /// </summary>
        /// <param name="uri">The URI of the resource to invoke.</param>
        /// <returns>The ResourceContent result.</returns>
        /// <exception cref="MCPException">Thrown if the resource is not found or invocation fails.</exception>
        public static ResourceContent Invoke(string uri)
        {
            EnsureInitialized();

            if (!_resources.TryGetValue(uri, out var resourceInfo))
            {
                throw new MCPException($"Unknown resource: {uri}", MCPErrorCodes.MethodNotFound);
            }

            return resourceInfo.Invoke(uri);
        }

        private static void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                RefreshResources();
            }
        }
    }

    /// <summary>
    /// Internal class holding metadata and invocation logic for a discovered resource.
    /// </summary>
    internal class ResourceInfo
    {
        private readonly MCPResourceAttribute _attribute;
        private readonly MethodInfo _method;

        public string Uri => _attribute.Uri;
        public string Description => _attribute.Description;

        public ResourceInfo(MCPResourceAttribute attribute, MethodInfo method)
        {
            _attribute = attribute;
            _method = method;
        }

        /// <summary>
        /// Creates a ResourceDefinition for the MCP resources/list response.
        /// </summary>
        public ResourceDefinition ToDefinition()
        {
            // Extract a name from the URI (last segment after ://)
            string name = ExtractNameFromUri(_attribute.Uri);

            return new ResourceDefinition(
                _attribute.Uri,
                name,
                _attribute.Description,
                "application/json"
            );
        }

        private static string ExtractNameFromUri(string uri)
        {
            // For URIs like "editor://active_tool", extract "active_tool"
            // For URIs like "scene://gameobject/{id}", extract "gameobject"
            int schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
            {
                string path = uri.Substring(schemeEnd + 3);
                // Remove any template parameters
                int templateStart = path.IndexOf('{');
                if (templateStart > 0)
                {
                    path = path.Substring(0, templateStart).TrimEnd('/');
                }
                return path;
            }
            return uri;
        }

        /// <summary>
        /// Invokes the resource and returns its content.
        /// </summary>
        public ResourceContent Invoke(string uri)
        {
            try
            {
                // Invoke with no arguments (resources are parameterless)
                var result = _method.Invoke(null, null);

                if (result == null)
                {
                    return ResourceContent.Text(uri, "null", "application/json");
                }

                // If the result is already a ResourceContent, return it directly
                if (result is ResourceContent resourceContent)
                {
                    return resourceContent;
                }

                // Convert the result to JSON
                string jsonResult = JsonConvert.SerializeObject(result, new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    NullValueHandling = NullValueHandling.Include,
                    Formatting = Formatting.Indented
                });

                return new ResourceContent
                {
                    uri = uri,
                    mimeType = "application/json",
                    text = jsonResult
                };
            }
            catch (TargetInvocationException invocationException)
            {
                // Unwrap the inner exception for cleaner error messages
                var innerException = invocationException.InnerException ?? invocationException;

                if (innerException is MCPException)
                {
                    throw innerException;
                }

                throw new MCPException($"Resource invocation failed: {innerException.Message}", innerException, MCPErrorCodes.InternalError);
            }
            catch (Exception exception)
            {
                throw new MCPException($"Resource invocation failed: {exception.Message}", exception, MCPErrorCodes.InternalError);
            }
        }
    }
}
