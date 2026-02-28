using UnityEngine;
using UnityMCP.Editor.Core;

namespace MyProject.Editor.Tools
{
    /// <summary>
    /// Example: Create your own MCP tool using [MCPTool] and [MCPParam] attributes.
    /// Place this script anywhere in an Editor/ folder or an Editor-only assembly.
    /// Unity MCP will auto-discover it on the next domain reload.
    /// </summary>
    public static class MyCustomTool
    {
        [MCPTool("my_tool", "A custom tool that greets the user and optionally shouts",
            Category = "Custom")]
        public static object Execute(
            [MCPParam("name", "Name to greet", required: true)] string name,
            [MCPParam("shout", "Whether to shout the greeting")] bool shout = false,
            [MCPParam("repeat", "How many times to repeat")] int repeat = 1)
        {
            string greeting = $"Hello, {name}! Welcome to Unity MCP.";
            if (shout)
                greeting = greeting.ToUpper();

            var lines = new string[repeat];
            for (int i = 0; i < repeat; i++)
                lines[i] = greeting;

            string message = string.Join("\n", lines);
            Debug.Log($"[MyCustomTool] {message}");

            return new
            {
                success = true,
                message,
                name,
                shout,
                repeat
            };
        }
    }
}
