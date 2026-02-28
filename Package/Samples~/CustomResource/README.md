# Custom Resource Example

This sample shows how to expose project data as an MCP resource.

## Setup

1. Import this sample via Package Manager > Unity MCP > Samples > Custom Resource Example
2. The resource is automatically registered on domain reload
3. AI assistants can read it via `resources/read` with URI `custom://project-stats`

## How It Works

Decorate a static method with `[MCPResource]`:

```csharp
[MCPResource("custom://my-data", "My Data",
    Description = "Description of the data",
    MimeType = "application/json")]
public static object GetMyData()
{
    return new { key = "value" };
}
```

### Key Points

- The method must be `public static` and return `object`
- The URI scheme is flexible (`custom://`, `project://`, etc.)
- Return data is serialized to JSON
- Resources are read-only - use tools for write operations
- Place in an Editor/ folder or Editor-only assembly definition
