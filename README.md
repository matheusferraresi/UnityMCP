# Unity MCP - AI Assistant Integration for Unity Editor

![Unity 6+](https://img.shields.io/badge/Unity-6%2B-black?logo=unity)
![Release](https://img.shields.io/github/v/tag/Bluepuff71/UnityMCP?label=version)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue)

Model Context Protocol (MCP) server that enables AI assistants like Claude, Codex, and Cursor to automate Unity Editor tasks and game development workflows.

## Features

- **Zero telemetry** - Completely private Unity automation. No data collection.
- **40+ built-in tools** - Create GameObjects, run tests, build projects, manipulate scenes through AI.
- **Simple extension API** - Add custom AI tools with a single attribute.

## Requirements

- Unity 6 or later
- Any MCP client: Claude Code, Claude Desktop, Codex, Cursor, or other AI assistants

## Installation

1. Open Unity Package Manager (Window → Package Manager)
2. Click `+` → "Add package from git URL"
3. Enter the URL of whichever version you want

### Latest Version (Recommended)

To install the latest version:
```
https://github.com/Bluepuff71/UnityMCP.git?path=/Package
```

### Specific Version (If I messed something up)

To install a specific version, append `#version` tag:
```
https://github.com/Bluepuff71/UnityMCP.git?path=/Package#1.0.0
```

See [Releases](https://github.com/Bluepuff71/UnityMCP/releases) for available versions.

## Setup

### Claude Code

```bash
claude mcp add unity-mcp --transport http http://localhost:8080/
```

### Claude Desktop

Add this to your Claude Desktop configuration file:

**macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`

**Windows**: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "unity-mcp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:8080/"]
    }
  }
}
```

Restart Claude Desktop after saving.

### Other MCP Clients (Codex, Cursor, etc.)

Unity MCP runs an HTTP server at `http://localhost:8080/`. Any MCP client with HTTP transport support can connect directly. For stdio-only clients, use the mcp-remote bridge as shown above.

*Note: Configurations for clients other than Claude Code have not been tested. Open a PR!*

## How Unity Editor Automation Works

Unity MCP uses a native C plugin to maintain an HTTP server on a background thread, independent of Unity's C# scripting domain. This architecture ensures the AI assistant connection remains active during script recompilation.

```
┌─────────────────┐
│  MCP Client     │
│  (Claude, etc.) │
└────────┬────────┘
         │ HTTP
         ▼
┌─────────────────────────────────────┐
│  Native Plugin (C)                  │
│  - HTTP server on background thread │
│  - Survives domain reloads          │
└────────┬────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────┐
│  Unity C# (main thread)             │
│  - Executes tools when available    │
│  - Returns "recompiling" during     │
│    domain reload                    │
└─────────────────────────────────────┘
```

When Unity recompiles scripts, the C# domain unloads temporarily. During this time, the native plugin continues accepting HTTP requests but returns a "Unity is recompiling" error instead of disconnecting. Once recompilation completes, requests are forwarded to Unity's main thread for execution.

## Available MCP Tools

Unity MCP provides over 40 built-in tools organized by category:

### GameObject Management
- **gameobject_manage** - Create, modify, delete, duplicate GameObjects, or move them relative to other objects
- **gameobject_find** - Find GameObjects by name, tag, layer, component type, path, or instance ID with pagination

### Component Management
- **component_manage** - Add, remove, or set properties on components attached to GameObjects

### Scene Management
- **scene_create** - Create a new empty scene at a specified path
- **scene_load** - Load a scene by path or build index
- **scene_save** - Save the current scene, optionally to a new path
- **scene_get_active** - Get information about the currently active scene
- **scene_get_hierarchy** - Get the hierarchy of GameObjects in the current scene with pagination
- **scene_screenshot** - Capture a screenshot of the Game View

### Asset Management
- **asset_manage** - Create, delete, move, rename, duplicate, import, search, or get info about assets
- **prefab_manage** - Open/close prefab stage, save prefabs, or create prefabs from GameObjects
- **manage_material** - Create materials, set properties, assign to renderers, or set colors
- **manage_texture** - Modify texture import settings (format, compression, size, etc.)
- **manage_shader** - Create and manage shader assets
- **manage_script** - Create C# scripts from templates
- **manage_scriptableobject** - Create and manage ScriptableObject assets
- **manage_vfx** - Create and configure particle systems, trail renderers, and line renderers

### Build & Testing
- **build_start** - Start a player build asynchronously, returns job_id for polling
- **build_get_job** - Get build job status and results
- **tests_run** - Start Unity Test Runner asynchronously (EditMode or PlayMode)
- **tests_get_job** - Get test run job status and results

### Editor Control
- **playmode_enter** - Enter play mode
- **playmode_exit** - Exit play mode
- **playmode_pause** - Toggle or set pause state
- **playmode_step** - Advance a single frame while paused
- **selection_get** - Get currently selected objects in the Unity Editor
- **selection_set** - Set selection by instance IDs or asset paths
- **execute_menu_item** - Execute Unity Editor menu items by path (with safety blacklist)
- **editor_refresh** - Force Unity to refresh assets

### Console & Profiling
- **console_read** - Read Unity Console log entries with filtering and pagination
- **profiler_start** - Start profiler recording
- **profiler_stop** - Stop profiler recording and get results

### UI Toolkit
- **uitoolkit_query** - Query and inspect UI Toolkit elements in EditorWindows with drill-down support
- **uitoolkit_interact** - Simulate clicks and interactions with UI elements

### Batch Operations
- **batch_execute** - Execute multiple MCP commands in a single operation with fail-fast support

### Debug & Testing
- **test_echo** - Echo back input message (connectivity test)
- **test_add** - Add two numbers (parameter handling test)
- **test_unity_info** - Get basic Unity editor information
- **test_list_scenes** - List all scenes in build settings

## Available MCP Resources

Resources provide read-only access to Unity Editor state via URI patterns:

### Scene Resources
- **scene://gameobject/{id}** - GameObject details by instance ID
- **scene://gameobject/{id}/components** - List components on a GameObject
- **scene://gameobject/{id}/component/{type}** - Specific component details
- **scene://loaded** - List of all loaded scenes

### Editor Resources
- **editor://state** - Current editor state (play mode, compiling, focus, etc.)
- **editor://selection** - Currently selected objects
- **editor://windows** - List of open EditorWindows
- **editor://prefab-stage** - Current prefab stage information
- **editor://active-tool** - Currently active editor tool

### Project Resources
- **project://info** - Project name, path, Unity version
- **project://tags** - All project tags
- **project://layers** - All project layers
- **project://player-settings** - Player settings summary
- **project://quality-settings** - Quality settings configuration
- **project://physics-settings** - Physics engine settings
- **project://audio-settings** - Audio engine settings
- **project://input-settings** - Input system configuration
- **project://rendering-settings** - Rendering pipeline settings

### Build & Package Resources
- **build://settings** - Build settings and scene list
- **package://installed** - List of installed packages

### Console & Tests
- **console://summary** - Console message count summary
- **console://errors** - Recent console errors
- **tests://list** - Available tests in the project
- **profiler://state** - Current profiler state

### Animation & Menu
- **animation://animator/{id}** - Animator component state
- **menu://items** - Available Unity Editor menu items

### Asset Resources
- **asset://dependencies/{path}** - Asset dependencies for a given path

## Adding Custom AI Tools

Create a static method and mark it with `[MCPTool]`:

```csharp
using UnityMCP.Editor;
using UnityEngine;

public static class MyCustomTools
{
    [MCPTool("hello_world", "Says hello to the specified name")]
    public static string SayHello(
        [MCPParam("name", "Name to greet", required: true)] string name)
    {
        return $"Hello, {name}!";
    }

    [MCPTool("create_cube_at", "Creates a cube at specified position")]
    public static object CreateCube(
        [MCPParam("x", "X coordinate", required: true)] float x,
        [MCPParam("y", "Y coordinate", required: true)] float y,
        [MCPParam("z", "Z coordinate", required: true)] float z)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.position = new Vector3(x, y, z);
        cube.name = "Custom Cube";

        return new
        {
            success = true,
            message = $"Created cube at ({x}, {y}, {z})",
            instanceID = cube.GetInstanceID()
        };
    }
}
```

Tools are automatically discovered on domain reload. No registration needed.

## Adding Custom Resources

Resources expose read-only data to AI assistants. Use `[MCPResource]`:

```csharp
using UnityMCP.Editor;
using UnityEngine;

public static class MyCustomResources
{
    [MCPResource("unity://player/stats", "Current player statistics")]
    public static object GetPlayerStats()
    {
        var player = GameObject.Find("Player");
        if (player == null)
            return new { error = "Player not found" };

        return new
        {
            position = player.transform.position,
            health = player.GetComponent<Health>()?.CurrentHealth ?? 0,
            isGrounded = player.GetComponent<CharacterController>()?.isGrounded ?? false
        };
    }
}
```

Resources use URI patterns (e.g., `unity://player/stats`) and are read via `resources/read`.

## License

GPLv3 - see LICENSE file for details.
