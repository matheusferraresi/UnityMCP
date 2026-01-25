using UnityEditor;

namespace UnityMCP.Editor.Resources.Editor
{
    /// <summary>
    /// Resource provider for the currently active editor tool.
    /// </summary>
    public static class ActiveTool
    {
        /// <summary>
        /// Gets information about the currently active editor tool.
        /// </summary>
        /// <returns>Object containing the current tool and view tool information.</returns>
        [MCPResource("editor://active_tool", "Currently active editor tool (Move, Rotate, Scale, etc.)")]
        public static object GetActiveTool()
        {
            return new
            {
                tool = Tools.current.ToString(),
                viewTool = Tools.viewTool.ToString(),
                pivotMode = Tools.pivotMode.ToString(),
                pivotRotation = Tools.pivotRotation.ToString(),
                handlePosition = new
                {
                    x = Tools.handlePosition.x,
                    y = Tools.handlePosition.y,
                    z = Tools.handlePosition.z
                },
                handleRotation = new
                {
                    x = Tools.handleRotation.eulerAngles.x,
                    y = Tools.handleRotation.eulerAngles.y,
                    z = Tools.handleRotation.eulerAngles.z
                }
            };
        }
    }
}
