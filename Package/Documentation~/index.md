# Unixxty MCP Documentation

## Overview

Unixxty MCP is a pure C# Model Context Protocol (MCP) server that runs inside the Unity Editor. It enables AI assistants like Claude to directly interact with your Unity project - creating GameObjects, editing scripts, running tests, building projects, and even hot-patching code during Play Mode.

## Quick Start

### 1. Install the Package

**Via Git URL (recommended):**
1. Open Unity Package Manager (`Window > Package Manager`)
2. Click `+` > `Add package from git URL`
3. Enter: `https://github.com/matheusferraresi/UnityMCP.git?path=/Package`

### 2. Verify Installation

After installation, open `Window > Unixxty MCP` in the editor. You should see:
- Server status: Running
- Port: 8080
- Tools registered: 68

### 3. Configure Your AI Client

Add to your MCP client configuration:

```json
{
  "mcpServers": {
    "unity-mcp": {
      "url": "http://localhost:8080"
    }
  }
}
```

## Architecture

Unixxty MCP runs entirely inside Unity's editor process:

```
AI Assistant (Claude, etc.)
    ↓ HTTP (JSON-RPC 2.0)
Unixxty MCP Server (localhost:8080)
    ↓ Direct API calls
Unity Editor APIs
```

- **No external process** - No Python, Node.js, or sidecar needed
- **Domain reload safe** - Native C plugin maintains connection across recompilation
- **Editor-only** - Zero impact on runtime builds

## Key Features

### 68 Built-in Tools
Organized across 25+ categories: GameObject management, script editing, scene manipulation, prefab operations, physics simulation, NavMesh, terrain, lighting, and more.

### Play Mode Hot Patching
Edit method bodies during Play Mode without stopping. Uses Harmony 2.3.3 for method interception and Roslyn for runtime compilation.

### Extensibility
Add custom tools and resources with simple C# attributes. See the included samples.

## Extending Unixxty MCP

### Custom Tools

```csharp
[MCPTool("my_tool", "Description", Category = "Custom")]
public static object Execute(
    [MCPParam("name", "Parameter description", required: true)] string name)
{
    return new { success = true, message = $"Hello {name}" };
}
```

### Custom Resources

```csharp
[MCPResource("custom://data", "My Data", MimeType = "application/json")]
public static object GetData()
{
    return new { key = "value" };
}
```

See `Samples~/CustomTool` and `Samples~/CustomResource` for complete examples.

## Troubleshooting

### Server not starting
- Check `Window > Unixxty MCP` for error messages
- Verify port 8080 is not in use by another process
- Try restarting Unity

### Tools not registering
- Ensure your tool class is in an Editor assembly
- Check that methods are `public static` returning `object`
- Verify `[MCPTool]` attribute is applied correctly

### Hot patch not working
- Hot patching only works in Play Mode
- Check `hot_patch status` for Harmony and Roslyn availability
- Method signature changes require full recompilation (exit Play Mode)
