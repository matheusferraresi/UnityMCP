# Unity MCP

Pure C# MCP (Model Context Protocol) server for Unity 6 that enables AI assistants like Claude to interact with the Unity Editor.

## Features

- Attribute-based tool registration with `[MCPTool]` and `[MCPParam]`
- HTTP server on localhost:8080
- Editor-only (designed for development workflows)
- Extensible architecture for custom tools

## Requirements

- Unity 6 (6000.0 or later)

## Installation

### Via Git URL (Recommended)

1. Open Unity Package Manager (Window > Package Manager)
2. Click the `+` button in the top-left corner
3. Select "Add package from git URL..."
4. Enter: `https://github.com/emeryporter/UnityMCP.git`
5. Click "Add"

### Via Local Path

1. Clone or download this repository to your local machine
2. Open Unity Package Manager (Window > Package Manager)
3. Click the `+` button in the top-left corner
4. Select "Add package from disk..."
5. Navigate to the `package.json` file in the UnityMCP folder
6. Click "Open"

### Manual Installation

1. Clone or download this repository
2. Copy the entire `UnityMCP` folder into your project's `Packages` directory

## Usage

Once installed, the MCP server will be available in the Unity Editor. Configure your AI assistant (e.g., Claude) to connect to `http://localhost:8080`.

## License

MIT License - See [LICENSE](LICENSE) for details.
