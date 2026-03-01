# UnixxtyMCP Sidecar Architecture

## Date: 2026-03-01

## Problem Statement

The MCP server lives inside the Unity process it controls. This causes a class of recurring issues:

- **Port 8080 binding failure** after restarts/crashes (Mongoose uses `SO_EXCLUSIVEADDRUSE` on Windows, DllMain only gets 100ms for cleanup)
- **Domain reload disruption** — C# layer dies, requests error or timeout (30s)
- **Single-request serialization** — one static buffer, no concurrency
- **256KB response limit** — baked into native C arrays
- **Can't update native DLL** without restarting Unity
- **Wait script races** — catches old server before shutdown completes

## Current Architecture

```
Claude Code ──HTTP──→ Native DLL (port 8080, Mongoose) ──shared buffer──→ C# polling (main thread) ──→ Tool execution
```

### Native C Proxy (Proxy~/proxy.c)
- Mongoose HTTP server on dedicated OS thread
- Survives domain reloads (Unity never unloads native plugins)
- Single static buffers: `s_request_buffer[256KB]`, `s_response_buffer[256KB]`
- Spin-wait at 1ms intervals for C# response, 50ms during domain reload
- 30-second hard timeout per request
- `DllMain(DLL_PROCESS_DETACH)`: 100ms sleep, can't call `WaitForSingleObject` (loader lock)

### C# Proxy Layer (MCPProxy.cs)
- `[InitializeOnLoad]` re-hooks after every domain reload
- `EditorApplication.update += PollForRequests` — main thread, once per frame
- `SetPollingActive(0)` before reload, `SetPollingActive(1)` after
- All tool execution synchronous on main thread (required for Unity APIs)
- Retry logic: 5 attempts, 1s delay on `StartServer` failure

### Key Constants
```
PROXY_MAX_RESPONSE_SIZE   = 256KB
PROXY_MAX_REQUEST_SIZE    = 256KB
PROXY_REQUEST_TIMEOUT_MS  = 30000 (30s)
PROXY_RECOMPILE_POLL_INTERVAL_MS = 50ms
```

## Proposed Architecture: External Sidecar

```
Claude Code ──HTTP:8080──→ Sidecar (Python) ──HTTP:8081──→ Unity (native DLL)
                                              [Step 2: WebSocket replaces HTTP]
```

**Key inversion**: Unity is the client, sidecar owns the port permanently.

### Step 1: Transparent Proxy (implemented)
- `tools/sidecar.py` — Python stdlib, ~200 lines
- Accepts HTTP on 8080, forwards to Unity on 8081
- Retries on Unity disconnect (domain reload, restart)
- Health endpoint: `GET /status`
- Persistent logging

### Step 2: WebSocket Client (future)
- Unity connects OUT to sidecar via WebSocket
- Remove native DLL entirely
- Domain reload = WS disconnect/reconnect (natural)
- No port binding issues ever

### Step 3: Enhanced Features (future)
- Request queuing with priority
- Streaming responses (MCP SSE)
- Multi-instance routing by project
- Concurrent request support
- Persistent diagnostics

## What MUST Stay Inside Unity

All tool execution requires the Unity main thread:
- Scene access (SceneManager, GameObject, Component, AssetDatabase)
- Play mode control (EditorApplication.isPlaying)
- Script compilation (CompilationPipeline)
- Physics simulation
- Profiler, Test Runner, Build Pipeline
- Hot patching (Harmony), Roslyn compilation
- UI Toolkit, Input injection
- Window focus (user32.dll P/Invoke)

## What Moves to the Sidecar

Transport and orchestration only:
- HTTP port ownership (permanent, survives Unity restarts)
- Request queuing and retry
- Health monitoring
- Logging and diagnostics
- Response size handling (no 256KB limit)

## Benefits

| Problem | Current | Sidecar |
|---------|---------|---------|
| Port binding after restart | DLL dies, port stuck | Sidecar stays alive |
| Domain reload disruption | Requests error/timeout | Sidecar retries automatically |
| Response size | 256KB hard limit | Unlimited |
| Wait-for-restart | Script races old server | Sidecar knows exact state |
| Multiple Unity instances | Port conflict | Route by instance |
| Native DLL updates | Requires restart | No native DLL (Step 2) |

## Files

| File | Purpose |
|------|---------|
| `tools/sidecar.py` | External proxy server |
| `tools/wait-for-unity.py` | CLI tool for checking Unity readiness |
| `Package/Editor/Core/MCPProxy.cs` | C# proxy (now on port 8081) |
| `Proxy~/proxy.c` | Native DLL (to be removed in Step 2) |
| `Package/Editor/Core/MCPServer.cs` | Request router |
| `Package/Editor/Core/ToolRegistry.cs` | Tool discovery and invocation |

## Mongoose Socket Details

On Windows, Mongoose uses `SO_EXCLUSIVEADDRUSE` (strictest mode):
```c
// mongoose.c line 10432
setsockopt(fd, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, &on, sizeof(on))
```

This means if the previous process didn't cleanly release the socket, the new process CANNOT bind. Combined with `DllMain`'s 100ms sleep limitation, this is the root cause of port sticking.

## Known Issues (from MCP_ISSUES_PHASE1.md)

Domain-reload cluster (all solved by sidecar):
1. Domain reload resilience — tools fail with "interrupted" or "timed out"
3. compile_and_watch auto-attach race
4. No reliable wait-until-ready (partially solved)
7. execute_menu_item misleading error during compile
8. compile_and_watch empty on domain reload
9. compile_and_watch hangs indefinitely
