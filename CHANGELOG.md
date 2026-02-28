# Changelog

All notable changes to Unixxty MCP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-02-28

### Added
- **NavMesh tools** (`navmesh_manage`) - 10 actions: bake, clear, status, query paths, settings, areas, agents, surfaces
- **Terrain tools** (`terrain_manage`) - 13 actions: create, heightmap ops, texture painting, trees, detail density
- **Roslyn runtime compilation** - Dynamically loads Roslyn from Unity's Mono libraries for hot_patch method body compilation. No bundled DLLs or defines needed.
- **Harmony 2.3.3** bundled as `0Harmony.dll` (MIT license) for Play Mode method patching
- **Hot patching** (`hot_patch`) - Redirect methods, compile new method bodies at runtime, rollback patches. Auto-reverts on Play Mode exit.
- **Smart script editing** (`smart_edit`) - Search/replace, line operations, unified diff application
- **Compilation watcher** (`compile_and_watch`) - Trigger compilation and monitor for errors/warnings
- **AI vision** (`vision_capture`) - Base64 PNG screenshot capture for AI vision analysis
- **Debug play testing** (`debug_play`) - Automated Play Mode testing with state capture
- **Type reflection** (`type_inspector`) - Inspect types, fields, methods, and properties via reflection
- **UGUI management** (`manage_ugui`) - Canvas and UI element creation and manipulation
- **File import** (`file_import`) - Import external files into the Unity project
- **Server instructions** (`server_instructions`) - Custom per-project AI instructions via MCP initialize response
- **Scene diffing** (`scene_diff`) - Snapshot and diff scene hierarchy changes
- **Undo/redo control** (`undo_redo`) - Programmatic undo/redo system access
- **Asset previews** (`asset_preview`) - Generate asset thumbnail previews as base64 PNG
- **Physics simulation** (`physics_simulate`) - Edit-mode physics stepping, raycasts, overlap checks
- **Lighting bake** (`lighting_bake`) - Lightmap baking control: start, stop, status, settings, probes
- Console clear action for `console_write`
- Samples for custom tool and resource creation
- MIT license

### Changed
- Tool count increased from 45 to 68
- `hot_patch` no longer requires `USE_ROSLYN` define - Roslyn loads dynamically from Unity's installation
- Updated package metadata for Asset Store readiness
- Comprehensive documentation rewrite covering all 68 tools

## [1.6.9] - 2025-12-15

### Added
- Initial fork from Bluepuff71/UnityMCP
- 45 built-in tools across 15 categories
- 28 MCP resources (scene, project, editor, build, console, animation, asset)
- Attribute-based tool registration (`[MCPTool]`, `[MCPParam]`)
- HTTP transport on localhost:8080
- Native C plugin for domain reload persistence
- Batch execution for atomic multi-tool operations
- Search tools for tool discovery
- Editor window for server status and configuration
