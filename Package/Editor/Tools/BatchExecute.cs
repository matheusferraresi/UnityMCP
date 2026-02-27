using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Executes multiple MCP tool calls in a single batch for dramatically reduced round-trip overhead.
    /// Inspired by CoderGamester/mcp-unity's batch_execute with atomic Undo support.
    /// </summary>
    public static class BatchExecute
    {
        private const int MaxOperations = 100;
        private const int DefaultMaxOperations = 25;

        [MCPTool("batch_execute", "Execute multiple tool calls in a single batch (10-100x faster than sequential). Supports atomic mode with Undo rollback.",
            Category = "Utility", DestructiveHint = true)]
        public static object Execute(
            [MCPParam("operations", "Array of operations: [{\"tool\": \"tool_name\", \"params\": {...}, \"id\": \"optional_id\"}, ...]", required: true)] List<object> operations,
            [MCPParam("stop_on_error", "Stop executing remaining operations on first error (default: true)")] bool stopOnError = true,
            [MCPParam("atomic", "If true, all operations are wrapped in a single Undo group and rolled back on failure (default: false)")] bool atomic = false,
            [MCPParam("max_operations", "Maximum operations to execute (default: 25, max: 100)", Minimum = 1, Maximum = 100)] int maxOperations = DefaultMaxOperations)
        {
            if (operations == null || operations.Count == 0)
            {
                throw MCPException.InvalidParams("'operations' array is required and must not be empty.");
            }

            int limit = Mathf.Clamp(maxOperations, 1, MaxOperations);
            if (operations.Count > limit)
            {
                throw MCPException.InvalidParams($"Too many operations ({operations.Count}). Maximum allowed: {limit}. Increase max_operations up to {MaxOperations}.");
            }

            var results = new List<object>();
            int successCount = 0;
            int failCount = 0;
            bool aborted = false;
            string undoGroupName = null;

            // Start Undo group for atomic mode
            if (atomic)
            {
                undoGroupName = $"BatchExecute ({operations.Count} ops)";
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName(undoGroupName);
            }

            int undoGroup = atomic ? Undo.GetCurrentGroup() : -1;

            try
            {
                for (int i = 0; i < operations.Count; i++)
                {
                    var op = operations[i];
                    JObject opObj;

                    try
                    {
                        opObj = op is JObject jo ? jo : JObject.FromObject(op);
                    }
                    catch
                    {
                        var parseError = new
                        {
                            index = i,
                            id = (string)null,
                            success = false,
                            error = "Invalid operation format. Expected {\"tool\": \"name\", \"params\": {...}}"
                        };
                        results.Add(parseError);
                        failCount++;

                        if (stopOnError)
                        {
                            aborted = true;
                            break;
                        }
                        continue;
                    }

                    string toolName = opObj["tool"]?.ToString();
                    string opId = opObj["id"]?.ToString() ?? $"op_{i}";
                    JObject toolParams = opObj["params"] as JObject ?? new JObject();

                    if (string.IsNullOrEmpty(toolName))
                    {
                        var missingTool = new
                        {
                            index = i,
                            id = opId,
                            success = false,
                            error = "Missing 'tool' field in operation."
                        };
                        results.Add(missingTool);
                        failCount++;

                        if (stopOnError)
                        {
                            aborted = true;
                            break;
                        }
                        continue;
                    }

                    // Prevent nested batch calls
                    if (toolName == "batch_execute")
                    {
                        var nestedError = new
                        {
                            index = i,
                            id = opId,
                            success = false,
                            error = "Nested batch_execute is not allowed."
                        };
                        results.Add(nestedError);
                        failCount++;

                        if (stopOnError)
                        {
                            aborted = true;
                            break;
                        }
                        continue;
                    }

                    // Execute the tool
                    try
                    {
                        object result = ToolRegistry.InvokeWithJson(toolName, toolParams.ToString());

                        results.Add(new
                        {
                            index = i,
                            id = opId,
                            success = true,
                            tool = toolName,
                            result
                        });
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            index = i,
                            id = opId,
                            success = false,
                            tool = toolName,
                            error = ex.Message
                        });
                        failCount++;

                        if (stopOnError)
                        {
                            aborted = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Unexpected error during batch execution
                if (atomic && undoGroup >= 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                }

                return new
                {
                    success = false,
                    error = $"Batch execution failed: {ex.Message}",
                    results,
                    successCount,
                    failCount
                };
            }

            // Handle atomic rollback on failure
            bool rolledBack = false;
            if (atomic && failCount > 0 && undoGroup >= 0)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                rolledBack = true;
            }

            // Collapse the undo group
            if (atomic)
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            return new
            {
                success = failCount == 0,
                results,
                summary = new
                {
                    total = operations.Count,
                    executed = successCount + failCount,
                    succeeded = successCount,
                    failed = failCount,
                    skipped = operations.Count - (successCount + failCount),
                    aborted,
                    atomic,
                    rolledBack
                }
            };
        }
    }
}
