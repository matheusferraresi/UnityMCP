# UnixxtyMCP - AI Agent Guide

## Overview

UnixxtyMCP is a 100% Unity-native MCP (Model Context Protocol) server that runs entirely inside the Unity Editor. No external runtime (Python, Node.js, .NET) is needed. AI assistants connect via HTTP to control the editor.

## Architecture

```
AI Client (Claude Code, Cursor, etc.)
    | HTTP POST (JSON-RPC) to localhost:8080
    v
Native C Plugin (background thread, Mongoose HTTP)
    | Stores request, waits for response
    v
C# MCPProxy.PollForRequests() [Unity main thread via EditorApplication.update]
    | Routes to MCPServer.HandleRequest()
    | Invokes tool via ToolRegistry
    | Returns JSON response
    v
Native C Plugin sends HTTP response back to client
```

**Key invariants:**
- All tool execution happens on Unity's main thread
- The native C plugin HTTP server survives domain reloads
- One request at a time (sequential, not concurrent)
- Default port: **8080** (configurable in Window > Unixxty MCP)
- Max response size: 256KB

## Tool Categories (68 tools)

| Category | Tools | Description |
|----------|-------|-------------|
| **Scene** | scene_create, scene_load, scene_save, scene_get_active, scene_get_hierarchy, scene_screenshot | Scene lifecycle and inspection |
| **Scene Tracking** | scene_diff | Snapshot/diff scene hierarchy changes |
| **GameObject** | gameobject_manage, gameobject_find | Create, modify, delete, duplicate, move GameObjects |
| **Component** | component_manage | Add, remove, inspect, set properties on components |
| **Asset** | asset_manage, manage_script, manage_material, manage_shader, manage_texture, manage_scriptable_object, prefab_manage, file_import, asset_preview | Full asset pipeline control + external import + previews |
| **Animation** | animation_controller | AnimatorController authoring |
| **VFX** | manage_vfx | Particles, line renderers, trail renderers |
| **UI (UGUI)** | manage_ugui | Canvas, Button, Text, Image, Panel, ScrollView, InputField, etc. |
| **Console** | console_read, console_write | Read/write/clear Unity Console |
| **Tests** | tests_run, tests_get_job | Async test execution |
| **Profiler** | profiler_start, profiler_stop, profiler_get_job | Performance profiling |
| **Build** | build_start, build_get_job | Player builds |
| **UIToolkit** | uitoolkit_query, uitoolkit_get_styles, uitoolkit_click, uitoolkit_get_value, uitoolkit_set_value, uitoolkit_navigate | Editor UI automation |
| **Editor** | manage_editor, playmode_enter/exit/pause/step, selection_get/set, execute_menu_item, unity_refresh, recompile_scripts, package_manage, undo_redo | Editor state, controls, undo system |
| **Script Editing** | smart_edit, validate_script_advanced | Targeted diff editing + Roslyn validation |
| **Compilation** | compile_and_watch | Trigger compilation, watch for errors with structured results |
| **Hot Patching** | hot_patch | Harmony 2.3.3 method-level Play Mode patching |
| **Vision** | vision_capture | Base64 PNG screenshots for multimodal AI |
| **Play Mode Debug** | debug_play | Automated play testing with state capture |
| **Type Reflection** | type_inspector | Deep C# type inspection via reflection |
| **Physics** | physics_simulate | Edit-mode physics stepping, raycasts, overlap checks |
| **Lighting** | lighting_bake | Lightmap baking: start, stop, status, settings |
| **Navigation** | navmesh_manage | NavMesh: bake, clear, query paths, agents, areas, surfaces |
| **Terrain** | terrain_manage | Terrain: create, heightmap, textures, trees, detail density |
| **Configuration** | server_instructions | Custom per-project AI instructions |
| **Utility** | batch_execute, search_tools | Batch operations (10-100x faster), tool discovery |
| **Debug** | test_echo, test_add, test_unity_info, test_list_scenes | Connectivity testing |

## Hot Patching (Harmony 2.3.3)

UnixxtyMCP bundles [Harmony](https://github.com/pardeike/Harmony) (MIT license, v2.3.3) in `Plugins/0Harmony.dll` for Play Mode method patching.

**How it works:**
1. Agent edits a method via `smart_edit` or `manage_script`
2. `hot_patch` detects changed methods via source diff
3. Harmony patches the method in-memory (no domain reload)
4. Patches auto-revert when exiting Play Mode

**Actions:** `patch`, `redirect`, `rollback`, `status`

**Limitations:**
- Method bodies only (no new types, fields, or signatures)
- Falls back to native JIT redirect if Harmony prefix patching fails
- Reverts on domain reload

## Adding New Tools

1. Create a new `.cs` file in `Package/Editor/Tools/`
2. Add a `public static` method with the `[MCPTool]` attribute
3. Parameters use `[MCPParam]` for schema generation
4. Return an anonymous object - it will be JSON-serialized automatically
5. Tools are auto-discovered on domain reload (no registration needed)

```csharp
using UnixxtyMCP.Editor;
using UnixxtyMCP.Editor.Core;

public static class MyTools
{
    [MCPTool("my_tool", "Does something useful", Category = "MyCategory")]
    public static object DoSomething(
        [MCPParam("input", "The input value", required: true)] string input,
        [MCPParam("count", "How many times", Minimum = 1, Maximum = 100)] int count = 10)
    {
        return new { success = true, result = input, count };
    }
}
```

## Adding New Resources

Same pattern in `Package/Editor/Resources/`:

```csharp
[MCPResource("mycategory://data", "Description of what this returns")]
public static object GetData()
{
    return new { key = "value" };
}
```

## Batch Operations

Use `batch_execute` to run multiple tools in a single call (10-100x faster):

```json
{
  "tool": "batch_execute",
  "params": {
    "operations": [
      {"tool": "gameobject_manage", "params": {"action": "create", "name": "Cube1", "primitiveType": "Cube"}, "id": "op1"},
      {"tool": "gameobject_manage", "params": {"action": "create", "name": "Sphere1", "primitiveType": "Sphere"}, "id": "op2"}
    ],
    "atomic": true
  }
}
```

## Common Pitfalls

1. **Domain reload**: During script compilation, the C# side is unavailable but the native HTTP server keeps the connection alive. Requests queue until the C# proxy resumes polling.
2. **Play mode**: Some tools only work in Edit mode. Check tool annotations for `readOnlyHint`.
3. **Response size**: Max 256KB per response. Use pagination for large datasets (console_read, scene_get_hierarchy).
4. **Main thread**: All operations execute on Unity's main thread. Long-running operations (tests, builds, profiler) use async job patterns with polling.
5. **Hot patching**: Only works during Play Mode. Method bodies only - no new fields or types.

## Configuration

MCP client config (`.mcp.json`):
```json
{
  "mcpServers": {
    "unity-mcp": {
      "type": "http",
      "url": "http://localhost:8080/"
    }
  }
}
```

## File Structure

```
Package/
├── Editor/
│   ├── Core/           - Server, registries, protocol
│   ├── Tools/          - All MCP tools ([MCPTool] methods)
│   ├── Resources/      - All MCP resources ([MCPResource] methods)
│   ├── Prompts/        - MCP prompts ([MCPPrompt] methods)
│   ├── Utilities/      - Shared helpers (MethodPatcher, RoslynValidator)
│   ├── Services/       - Job managers (test, build, profiler)
│   └── UI/             - Editor window
├── Plugins/
│   ├── 0Harmony.dll    - Harmony 2.3.3 (MIT) for hot patching
│   ├── Windows/        - Native HTTP server (Windows)
│   ├── macOS/          - Native HTTP server (macOS)
│   └── Linux/          - Native HTTP server (Linux)
└── package.json        - Unity package manifest
```
