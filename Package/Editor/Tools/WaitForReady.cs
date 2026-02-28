using UnityEditor;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Tool that reports whether the Unity Editor is ready to accept operations.
    /// Replaces blind sleep/retry patterns — AI assistants can poll this to know
    /// when it's safe to proceed after domain reloads, compilation, or asset imports.
    /// </summary>
    public static class WaitForReady
    {
        [MCPTool("wait_for_ready",
            "Check if Unity Editor is ready (not compiling, not updating). " +
            "Call this after writing scripts, exiting play mode, or importing assets. " +
            "If not ready, poll again in 1-2 seconds.",
            Category = "Editor", ReadOnlyHint = true)]
        public static object Execute(
            [MCPParam("include_details", "Include detailed state breakdown (default: true)")] bool includeDetails = true)
        {
            bool isCompiling = EditorApplication.isCompiling;
            bool isUpdating = EditorApplication.isUpdating;
            bool isPlaying = EditorApplication.isPlaying;
            bool isReady = !isCompiling && !isUpdating;

            if (!includeDetails)
            {
                return new
                {
                    success = true,
                    ready = isReady
                };
            }

            string reason;
            if (isCompiling && isUpdating)
                reason = "compiling_and_updating";
            else if (isCompiling)
                reason = "compiling";
            else if (isUpdating)
                reason = "updating_assets";
            else
                reason = "ready";

            return new
            {
                success = true,
                ready = isReady,
                reason,
                is_compiling = isCompiling,
                is_updating = isUpdating,
                is_playing = isPlaying,
                message = isReady
                    ? "Editor is ready to accept operations."
                    : "Editor is busy. Poll again in 1-2 seconds."
            };
        }
    }
}
