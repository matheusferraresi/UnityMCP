using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Package Manager tool for installing, removing, and listing Unity packages.
    /// Inspired by CoderGamester/mcp-unity's add_package tool.
    /// </summary>
    public static class PackageManagerTools
    {
        [MCPTool("package_manage", "Manage Unity packages: add from registry/git/disk, remove, list, search",
            Category = "Editor", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("action", "Action to perform", required: true,
                Enum = new[] { "add", "remove", "list", "search", "info" })] string action,
            [MCPParam("package_name", "Package name (e.g., 'com.unity.cinemachine')")] string packageName = null,
            [MCPParam("version", "Package version (e.g., '3.1.5')")] string version = null,
            [MCPParam("source", "Install source: registry, git, disk (default: registry)")] string source = "registry",
            [MCPParam("git_url", "Git repository URL for git source")] string gitUrl = null,
            [MCPParam("branch", "Git branch (optional)")] string branch = null,
            [MCPParam("path", "Subfolder path within git repo, or local disk path")] string path = null)
        {
            return action?.ToLowerInvariant() switch
            {
                "add" => AddPackage(packageName, version, source, gitUrl, branch, path),
                "remove" => RemovePackage(packageName),
                "list" => ListPackages(),
                "search" => SearchPackages(packageName),
                "info" => GetPackageInfo(packageName),
                _ => throw MCPException.InvalidParams($"Unknown action: '{action}'.")
            };
        }

        private static object AddPackage(string packageName, string version, string source, string gitUrl, string branch, string path)
        {
            string identifier;

            switch (source?.ToLower())
            {
                case "git":
                    if (string.IsNullOrEmpty(gitUrl))
                        throw MCPException.InvalidParams("'git_url' is required for git source.");

                    identifier = gitUrl;
                    if (!string.IsNullOrEmpty(path))
                        identifier += $"?path={path}";
                    if (!string.IsNullOrEmpty(branch))
                        identifier += (identifier.Contains("?") ? "&" : "?") + $"#branch={branch}";
                    break;

                case "disk":
                    if (string.IsNullOrEmpty(path))
                        throw MCPException.InvalidParams("'path' is required for disk source.");
                    identifier = $"file:{path}";
                    break;

                default: // registry
                    if (string.IsNullOrEmpty(packageName))
                        throw MCPException.InvalidParams("'package_name' is required.");
                    identifier = string.IsNullOrEmpty(version) ? packageName : $"{packageName}@{version}";
                    break;
            }

            var request = Client.Add(identifier);

            // Wait for completion (synchronous for MCP tool)
            while (!request.IsCompleted)
                System.Threading.Thread.Sleep(100);

            if (request.Status == StatusCode.Success)
            {
                var info = request.Result;
                return new
                {
                    success = true,
                    message = $"Package '{info.displayName}' ({info.name}@{info.version}) installed.",
                    name = info.name,
                    version = info.version,
                    displayName = info.displayName,
                    source = info.source.ToString()
                };
            }

            return new
            {
                success = false,
                error = $"Failed to add package: {request.Error?.message ?? "Unknown error"}",
                errorCode = request.Error?.errorCode.ToString()
            };
        }

        private static object RemovePackage(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                throw MCPException.InvalidParams("'package_name' is required.");

            var request = Client.Remove(packageName);
            while (!request.IsCompleted)
                System.Threading.Thread.Sleep(100);

            if (request.Status == StatusCode.Success)
            {
                return new
                {
                    success = true,
                    message = $"Package '{packageName}' removed."
                };
            }

            return new
            {
                success = false,
                error = $"Failed to remove package: {request.Error?.message ?? "Unknown error"}"
            };
        }

        private static object ListPackages()
        {
            var request = Client.List();
            while (!request.IsCompleted)
                System.Threading.Thread.Sleep(100);

            if (request.Status != StatusCode.Success)
            {
                return new
                {
                    success = false,
                    error = $"Failed to list packages: {request.Error?.message ?? "Unknown error"}"
                };
            }

            var packages = request.Result.Select(p => new
            {
                name = p.name,
                version = p.version,
                displayName = p.displayName,
                source = p.source.ToString()
            }).OrderBy(p => p.name).ToArray();

            return new
            {
                success = true,
                packages,
                count = packages.Length
            };
        }

        private static object SearchPackages(string query)
        {
            if (string.IsNullOrEmpty(query))
                throw MCPException.InvalidParams("'package_name' is required as search query.");

            var request = Client.SearchAll();
            while (!request.IsCompleted)
                System.Threading.Thread.Sleep(100);

            if (request.Status != StatusCode.Success)
            {
                return new
                {
                    success = false,
                    error = $"Failed to search packages: {request.Error?.message ?? "Unknown error"}"
                };
            }

            var results = request.Result
                .Where(p => p.name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             (p.displayName != null && p.displayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new
                {
                    name = p.name,
                    version = p.versions.latest,
                    displayName = p.displayName,
                    description = p.description?.Length > 200 ? p.description.Substring(0, 200) + "..." : p.description
                }).ToArray();

            return new
            {
                success = true,
                results,
                count = results.Length
            };
        }

        private static object GetPackageInfo(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                throw MCPException.InvalidParams("'package_name' is required.");

            var listRequest = Client.List();
            while (!listRequest.IsCompleted)
                System.Threading.Thread.Sleep(100);

            if (listRequest.Status != StatusCode.Success)
            {
                return new
                {
                    success = false,
                    error = "Failed to list packages."
                };
            }

            var pkg = listRequest.Result.FirstOrDefault(p => p.name == packageName);
            if (pkg == null)
            {
                return new
                {
                    success = false,
                    error = $"Package '{packageName}' not found in project."
                };
            }

            return new
            {
                success = true,
                name = pkg.name,
                version = pkg.version,
                displayName = pkg.displayName,
                description = pkg.description,
                source = pkg.source.ToString(),
                category = pkg.category,
                documentationUrl = pkg.documentationUrl,
                dependencies = pkg.dependencies?.Select(d => new { name = d.name, version = d.version }).ToArray()
            };
        }
    }
}
