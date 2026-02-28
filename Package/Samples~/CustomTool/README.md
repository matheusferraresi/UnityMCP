# Custom Tool Example

This sample shows how to create your own MCP tool that AI assistants can invoke.

## Setup

1. Import this sample via Package Manager > Unixxty MCP > Samples > Custom Tool Example
2. The tool is automatically discovered by Unixxty MCP on domain reload
3. Ask your AI assistant to call `my_tool` with a name parameter

## How It Works

Any static method decorated with `[MCPTool]` is automatically registered:

```csharp
[MCPTool("tool_name", "Description of what the tool does", Category = "MyCategory")]
public static object Execute(
    [MCPParam("param_name", "Parameter description", required: true)] string param)
{
    // Your tool logic here
    return new { success = true, message = "Done!" };
}
```

### Key Points

- The method must be `public static` and return `object`
- Use `[MCPParam]` on each parameter with name, description, and optional constraints
- Return an anonymous object - it's serialized to JSON automatically
- Throw `MCPException.InvalidParams(msg)` for validation errors
- Place in an Editor/ folder or Editor-only assembly definition
