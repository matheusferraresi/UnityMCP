# Native Proxy Design: Seamless MCP Server During Domain Reload

**Date:** 2026-01-25
**Status:** Approved
**License:** GPLv2 (compatible with Mongoose library)

## Problem Statement

When Unity recompiles scripts, the C# domain reloads and the MCP HTTP server stops. AI assistants (Claude, etc.) receive connection errors during this window, breaking the seamless experience.

**Goal:** Zero connection errors. Always return valid JSON-RPC responses, even during recompilation.

## Solution Overview

Implement the HTTP server as a native C plugin that survives domain reload. Native plugins remain loaded in memory even when C# code is recompiled.

```
┌─────────────────────────────────────────────────────────────┐
│                    Native Plugin (C)                         │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  HTTP Server Thread (Mongoose)                         │  │
│  │  - Always running on port 8080                         │  │
│  │  - Receives all MCP requests                           │  │
│  │  - If C# available: forward via callback               │  │
│  │  - If C# unavailable: return "recompiling" response    │  │
│  └───────────────────────────────────────────────────────┘  │
│                            ↕                                 │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  Callback Registry                                     │  │
│  │  - C# registers handler after each domain load         │  │
│  │  - Set to NULL before domain unload                    │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                            ↕
┌─────────────────────────────────────────────────────────────┐
│                 C# MCPServer (existing logic)                │
│  - Registers callback with native plugin on init            │
│  - Handles tool/resource invocation                         │
│  - Re-registers after each domain reload                    │
└─────────────────────────────────────────────────────────────┘
```

## Technical Decisions

### HTTP Library: Mongoose

**Selected:** [Mongoose](https://github.com/cesanta/mongoose) (12k+ GitHub stars)

**Rationale:**
- Battle-tested: Used by NASA, Siemens, Google, Samsung, Qualcomm
- Runs on the International Space Station
- Security audited by Cisco Talos, Microsoft Security Response Center
- Single-file C library (~200KB compiled)
- Dual license: GPLv2 (free for open source) or commercial

**Alternatives considered:**
- CivetWeb (MIT license, but stagnant - last release April 2023)

### License: GPLv2

UnityMCP will be licensed under GPLv2, making Mongoose free to use.

## Architecture

### Directory Structure

```
UnityMCP/
├── Editor/
│   ├── Core/
│   │   ├── MCPServer.cs          # Modified - HTTP removed, HandleRequest() added
│   │   ├── NativeProxy.cs        # NEW - P/Invoke bindings + callback registration
│   │   ├── ToolRegistry.cs       # Unchanged
│   │   ├── ResourceRegistry.cs   # Unchanged
│   │   └── ...
│   └── ...
├── Plugins/
│   ├── Windows/x86_64/UnityMCPProxy.dll
│   ├── macOS/UnityMCPProxy.bundle    # Universal: arm64 + x86_64
│   └── Linux/x86_64/libUnityMCPProxy.so
├── NativeProxy~/                      # Source (excluded from Unity via ~)
│   ├── mongoose.c
│   ├── mongoose.h
│   ├── proxy.c
│   ├── proxy.h
│   ├── build_all.sh
│   ├── build_windows.bat
│   ├── build_macos.sh
│   ├── build_linux.sh
│   └── README.md
├── Tests~/                            # Excluded from package
│   ├── connection_monitor.py
│   └── ReloadTests.cs
└── package.json
```

### Native Plugin Implementation

**File: `NativeProxy~/proxy.c`** (~400 lines)

```c
#include "mongoose.h"
#include <string.h>

// Configuration
#define MAX_RESPONSE_SIZE 65536
#define REQUEST_TIMEOUT_MS 30000

// State
static struct mg_mgr s_mgr;
static volatile int s_running = 0;
static volatile int s_callback_valid = 0;
static void* s_csharp_callback = NULL;

// Response buffer (for synchronous C# callback)
static char s_response_buffer[MAX_RESPONSE_SIZE];
static volatile int s_has_response = 0;

// Recompiling response
static const char* RECOMPILING_RESPONSE =
    "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,"
    "\"message\":\"Unity is recompiling. Please retry in a moment.\"},\"id\":null}";

// Timeout response
static const char* TIMEOUT_RESPONSE =
    "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,"
    "\"message\":\"Request timed out.\"},\"id\":null}";

// Callback type matching C# delegate
typedef void (*RequestCallback)(const char* json_request);

// Exported functions
#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void RegisterCallback(RequestCallback callback) {
    s_csharp_callback = (void*)callback;
    s_callback_valid = (callback != NULL) ? 1 : 0;
}

EXPORT void SendResponse(const char* json) {
    if (json && strlen(json) < MAX_RESPONSE_SIZE) {
        strcpy(s_response_buffer, json);
        s_has_response = 1;
    }
}

EXPORT int StartServer(int port);
EXPORT void StopServer(void);

// HTTP handler
static void HandleHttpRequest(struct mg_connection *c, struct mg_http_message *hm) {
    // CORS preflight
    if (mg_strcmp(hm->method, mg_str("OPTIONS")) == 0) {
        mg_http_reply(c, 204,
            "Access-Control-Allow-Origin: *\r\n"
            "Access-Control-Allow-Methods: POST, OPTIONS\r\n"
            "Access-Control-Allow-Headers: Content-Type\r\n", "");
        return;
    }

    // Only POST allowed
    if (mg_strcmp(hm->method, mg_str("POST")) != 0) {
        mg_http_reply(c, 405, "", "Method Not Allowed");
        return;
    }

    // Check if C# is available
    if (!s_callback_valid || s_csharp_callback == NULL) {
        mg_http_reply(c, 200,
            "Content-Type: application/json\r\n"
            "Access-Control-Allow-Origin: *\r\n",
            "%s", RECOMPILING_RESPONSE);
        return;
    }

    // Extract request body
    char* request_body = (char*)malloc(hm->body.len + 1);
    memcpy(request_body, hm->body.buf, hm->body.len);
    request_body[hm->body.len] = '\0';

    // Call C# callback
    s_has_response = 0;
    ((RequestCallback)s_csharp_callback)(request_body);
    free(request_body);

    // Wait for response with timeout
    int waited_ms = 0;
    while (!s_has_response && waited_ms < REQUEST_TIMEOUT_MS) {
        mg_mgr_poll(&s_mgr, 10);
        waited_ms += 10;
    }

    if (s_has_response) {
        mg_http_reply(c, 200,
            "Content-Type: application/json\r\n"
            "Access-Control-Allow-Origin: *\r\n",
            "%s", s_response_buffer);
    } else {
        mg_http_reply(c, 200,
            "Content-Type: application/json\r\n"
            "Access-Control-Allow-Origin: *\r\n",
            "%s", TIMEOUT_RESPONSE);
    }
}

static void EventHandler(struct mg_connection *c, int ev, void *ev_data) {
    if (ev == MG_EV_HTTP_MSG) {
        HandleHttpRequest(c, (struct mg_http_message*)ev_data);
    }
}

EXPORT int StartServer(int port) {
    if (s_running) return 0;  // Already running

    mg_mgr_init(&s_mgr);

    char addr[32];
    snprintf(addr, sizeof(addr), "http://localhost:%d", port);

    struct mg_connection *c = mg_http_listen(&s_mgr, addr, EventHandler, NULL);
    if (c == NULL) {
        return -1;  // Failed to bind
    }

    s_running = 1;
    return 0;
}

EXPORT void StopServer(void) {
    if (!s_running) return;
    s_running = 0;
    mg_mgr_free(&s_mgr);
}
```

### C# Integration

**File: `Editor/Core/NativeProxy.cs`**

```csharp
using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Core
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void RequestCallback([MarshalAs(UnmanagedType.LPStr)] string jsonRequest);

    [InitializeOnLoad]
    public static class NativeProxy
    {
        private const string DLL_NAME = "UnityMCPProxy";

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int StartServer(int port);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void StopServer();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RegisterCallback(RequestCallback callback);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendResponse([MarshalAs(UnmanagedType.LPStr)] string json);

        private static RequestCallback s_callback;  // prevent GC collection
        private static bool s_initialized = false;

        static NativeProxy()
        {
            Initialize();
        }

        private static void Initialize()
        {
            if (s_initialized) return;

            try
            {
                // Start server (only does something on first call)
                int result = StartServer(8080);
                if (result != 0)
                {
                    Debug.LogWarning("[NativeProxy] Failed to start native server, falling back to managed server.");
                    return;
                }

                // Register callback
                s_callback = OnRequest;
                RegisterCallback(s_callback);

                // Register for domain unload
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
                EditorApplication.quitting += OnQuit;

                s_initialized = true;
                Debug.Log("[NativeProxy] Native MCP proxy initialized on port 8080");
            }
            catch (DllNotFoundException)
            {
                Debug.LogWarning("[NativeProxy] Native plugin not found, falling back to managed server.");
            }
        }

        private static void OnBeforeReload()
        {
            // Unregister callback before domain unloads
            RegisterCallback(null);
        }

        private static void OnQuit()
        {
            RegisterCallback(null);
            StopServer();
        }

        private static void OnRequest(string jsonRequest)
        {
            try
            {
                string response = MCPServer.Instance.HandleRequest(jsonRequest);
                SendResponse(response);
            }
            catch (Exception ex)
            {
                string errorResponse = $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32603,\"message\":\"{EscapeJson(ex.Message)}\"}},\"id\":null}}";
                SendResponse(errorResponse);
            }
        }

        private static string EscapeJson(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
```

### MCPServer Modifications

**Changes to `Editor/Core/MCPServer.cs`:**

1. Remove `HttpListener` and all HTTP handling code
2. Add synchronous `HandleRequest(string)` method
3. Simplify `MCPServerDomainReload` class

```csharp
public class MCPServer
{
    // Remove: HttpListener _listener
    // Remove: ListenAsync, HandleRequestAsync, WriteJsonRpcResponse, etc.
    // Keep: All tool/resource handling logic

    /// <summary>
    /// Handles a raw JSON-RPC request. Called from NativeProxy.
    /// </summary>
    public string HandleRequest(string jsonRequest)
    {
        try
        {
            var requestObject = JObject.Parse(jsonRequest);
            string requestId = requestObject["id"]?.ToString();
            string method = requestObject["method"]?.ToString();

            if (string.IsNullOrEmpty(method))
            {
                return CreateErrorResponse(-32600, "Missing 'method' field", requestId).ToString();
            }

            JToken paramsToken = requestObject["params"];

            JObject response = method switch
            {
                "initialize" => HandleInitialize(requestId),
                "tools/list" => HandleToolsList(requestId),
                "tools/call" => HandleToolsCallSync(paramsToken, requestId),
                "resources/list" => HandleResourcesList(requestId),
                "resources/read" => HandleResourcesReadSync(paramsToken, requestId),
                _ => CreateErrorResponse(-32601, $"Method not found: {method}", requestId)
            };

            return response.ToString(Formatting.None);
        }
        catch (JsonException ex)
        {
            return CreateErrorResponse(-32700, $"Parse error: {ex.Message}", null).ToString();
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(-32603, $"Internal error: {ex.Message}", null).ToString();
        }
    }

    // Tool/resource calls now synchronous (called from native thread, must block)
    private JObject HandleToolsCallSync(JToken paramsToken, string requestId)
    {
        // Uses MainThreadDispatcher.Enqueue + wait pattern
        // Blocks until Unity main thread completes the operation
    }
}
```

## Build Process

### Build Scripts

**`NativeProxy~/build_windows.bat`**
```batch
@echo off
cd /d "%~dp0"

:: Build with MSVC
cl /LD /O2 /DNDEBUG /DMG_ENABLE_LINES=0 proxy.c mongoose.c /Fe:UnityMCPProxy.dll ws2_32.lib

:: Copy to Plugins folder
copy /Y UnityMCPProxy.dll "..\Plugins\Windows\x86_64\"
```

**`NativeProxy~/build_macos.sh`**
```bash
#!/bin/bash
cd "$(dirname "$0")"

# Build universal binary (arm64 + x86_64)
clang -shared -O2 -DNDEBUG -DMG_ENABLE_LINES=0 \
    proxy.c mongoose.c \
    -o UnityMCPProxy.bundle \
    -arch arm64 -arch x86_64 \
    -framework CoreFoundation -framework Security

# Copy to Plugins folder
cp UnityMCPProxy.bundle ../Plugins/macOS/
```

**`NativeProxy~/build_linux.sh`**
```bash
#!/bin/bash
cd "$(dirname "$0")"

# Build shared library
gcc -shared -fPIC -O2 -DNDEBUG -DMG_ENABLE_LINES=0 \
    proxy.c mongoose.c \
    -o libUnityMCPProxy.so \
    -lpthread

# Copy to Plugins folder
cp libUnityMCPProxy.so ../Plugins/Linux/x86_64/
```

### GitHub Actions CI

**`.github/workflows/build-native.yml`**
```yaml
name: Build Native Plugins

on:
  push:
    paths:
      - 'NativeProxy~/**'
  workflow_dispatch:

jobs:
  build-windows:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: ilammy/msvc-dev-cmd@v1
      - run: cd NativeProxy~ && build_windows.bat
      - uses: actions/upload-artifact@v4
        with:
          name: windows-plugin
          path: Plugins/Windows/x86_64/UnityMCPProxy.dll

  build-macos:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v4
      - run: cd NativeProxy~ && chmod +x build_macos.sh && ./build_macos.sh
      - uses: actions/upload-artifact@v4
        with:
          name: macos-plugin
          path: Plugins/macOS/UnityMCPProxy.bundle

  build-linux:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: cd NativeProxy~ && chmod +x build_linux.sh && ./build_linux.sh
      - uses: actions/upload-artifact@v4
        with:
          name: linux-plugin
          path: Plugins/Linux/x86_64/libUnityMCPProxy.so
```

## Error Handling

| Scenario | Native Plugin Behavior | Response to Client |
|----------|----------------------|-------------------|
| Unity running normally | Forward to C#, return response | Normal MCP response |
| Domain reload in progress | `s_callback_valid == 0` | `{"error": {"code": -32000, "message": "Unity is recompiling..."}}` |
| C# throws exception | Catch in NativeProxy, format error | `{"error": {"code": -32603, "message": "..."}}` |
| Request timeout (30s) | Native code times out | `{"error": {"code": -32000, "message": "Request timed out"}}` |
| Port already in use | StartServer returns -1 | Log warning, fall back to managed server |
| Native plugin missing | DllNotFoundException | Log warning, fall back to managed server |

## Testing Strategy

### Automated Tests

**External Connection Monitor (`Tests~/connection_monitor.py`):**
```python
import requests
import time
import sys

URL = "http://localhost:8080/"
PAYLOAD = {"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}
DURATION_SECONDS = 30
POLL_INTERVAL_MS = 100

def main():
    errors = []
    iterations = (DURATION_SECONDS * 1000) // POLL_INTERVAL_MS

    for i in range(iterations):
        try:
            r = requests.post(URL, json=PAYLOAD, timeout=2)
            data = r.json()

            # Must be valid JSON-RPC (either result or error)
            if "result" not in data and "error" not in data:
                errors.append(f"Invalid response at {i}: {data}")

        except requests.exceptions.ConnectionError as e:
            errors.append(f"Connection error at iteration {i}: {e}")
        except Exception as e:
            errors.append(f"Unexpected error at {i}: {e}")

        time.sleep(POLL_INTERVAL_MS / 1000)

    if errors:
        print(f"FAILED: {len(errors)} errors")
        for e in errors[:10]:
            print(f"  - {e}")
        sys.exit(1)
    else:
        print(f"PASSED: {iterations} requests, zero connection errors")
        sys.exit(0)

if __name__ == "__main__":
    main()
```

**Unity Reload Trigger (`Tests~/ReloadTests.cs`):**
```csharp
using UnityEditor;
using UnityEngine;

public static class ReloadTests
{
    [MenuItem("Tests/Trigger 5 Rapid Reloads")]
    public static void TriggerRapidReloads()
    {
        EditorApplication.delayCall += () => TriggerReloadSequence(5);
    }

    private static void TriggerReloadSequence(int remaining)
    {
        if (remaining <= 0) return;

        Debug.Log($"Triggering reload {remaining}...");
        EditorUtility.RequestScriptReload();

        EditorApplication.delayCall += () =>
        {
            // Wait a bit, then trigger next
            EditorApplication.delayCall += () => TriggerReloadSequence(remaining - 1);
        };
    }
}
```

### Manual Test Scenarios

| Test | Steps | Expected Result |
|------|-------|-----------------|
| Normal operation | Start Unity, call MCP tools | Tools work as before |
| Domain reload | Modify any script, save | Brief "recompiling" responses, then normal |
| Rapid recompiles | Save script 5 times quickly | Never get connection error |
| Unity restart | Close and reopen Unity | Server works immediately |
| Port conflict | Two Unity instances, same port | Second instance logs warning, uses fallback |

## Success Criteria

1. **Zero connection errors** - All requests return valid JSON-RPC
2. **Graceful degradation** - "Recompiling" message during domain reload
3. **Cross-platform** - Works on Windows, macOS, Linux
4. **Fallback** - If native plugin fails, managed server still works
5. **No user action required** - Works automatically out of the box

## Implementation Plan

1. **Phase 1: Native Plugin** (2-3 days)
   - Set up `NativeProxy~/` folder structure
   - Implement `proxy.c` with Mongoose
   - Build scripts for all platforms

2. **Phase 2: C# Integration** (1 day)
   - Create `NativeProxy.cs` with P/Invoke bindings
   - Modify `MCPServer.cs` to expose `HandleRequest()`
   - Implement callback registration lifecycle

3. **Phase 3: Testing** (1 day)
   - Create connection monitor script
   - Manual testing on all platforms
   - CI pipeline setup

4. **Phase 4: Polish** (0.5 days)
   - Error handling edge cases
   - Logging and diagnostics
   - Documentation
