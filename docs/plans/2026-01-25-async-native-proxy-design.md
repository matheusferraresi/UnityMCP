# Async Native Proxy Design

## Problem

The native proxy processes HTTP requests synchronously on Unity's main thread during `PollEvents()`. Long-running tool execution blocks Unity, causing the "Hold on (busy for Xs)" dialog.

## Solution

Run the native HTTP server on a dedicated background thread. Only dispatch to Unity's main thread when calling Unity APIs.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ Native DLL                                                  │
│                                                             │
│  ┌──────────────────────┐    ┌────────────────────────────┐│
│  │ Server Thread        │    │ Shared State (atomic)      ││
│  │                      │    │ - s_running                ││
│  │ while(s_running):    │    │ - s_callback_valid         ││
│  │   mg_mgr_poll(10ms)  │    │ - s_response_buffer        ││
│  │                      │    │ - s_has_response           ││
│  └──────────┬───────────┘    └────────────────────────────┘│
│             │                                               │
│             ▼                                               │
│  ┌──────────────────────┐                                  │
│  │ HandleHttpRequest    │                                  │
│  │ (on server thread)   │                                  │
│  │                      │                                  │
│  │ 1. Parse request     │                                  │
│  │ 2. Call C# callback  │────► C# OnRequest (background)   │
│  │ 3. Send response     │◄──── SendResponse()              │
│  └──────────────────────┘                                  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ C# Side                                                     │
│                                                             │
│  OnRequest (background thread)                              │
│  │                                                          │
│  ├─► Parse JSON, validate (no Unity APIs)                   │
│  │                                                          │
│  ├─► Tool execution                                         │
│  │   │                                                      │
│  │   └─► DispatchAndWait(() => UnityAPI()) ───► Main Thread │
│  │       (blocks until complete)            ◄───┘           │
│  │                                                          │
│  ├─► Build response (no Unity APIs)                         │
│  │                                                          │
│  └─► SendResponse()                                         │
└─────────────────────────────────────────────────────────────┘
```

## Native Changes (proxy.c)

### Thread Management

```c
static pthread_t s_server_thread;
static volatile int s_running = 0;

static void* ServerThreadFunc(void* arg) {
    while (s_running) {
        mg_mgr_poll(&s_mgr, 10);
    }
    return NULL;
}

EXPORT int StartServer(int port) {
    if (s_running) return 1;  // Already running

    mg_mgr_init(&s_mgr);
    s_listener = mg_http_listen(&s_mgr, address, EventHandler, NULL);
    if (!s_listener) return -1;

    s_running = 1;
    pthread_create(&s_server_thread, NULL, ServerThreadFunc, NULL);
    return 0;
}

EXPORT void StopServer(void) {
    if (!s_running) return;

    s_running = 0;
    pthread_join(s_server_thread, NULL);
    mg_mgr_free(&s_mgr);
}
```

### Request Handling

Remove the wait loop - callback is now synchronous on background thread:

```c
static void HandleHttpRequest(struct mg_connection* c, struct mg_http_message* hm) {
    if (!s_callback_valid) {
        mg_http_reply(c, 200, CORS, "%s", RECOMPILING_RESPONSE);
        return;
    }

    // Callback executes entirely on this thread
    s_has_response = 0;
    s_csharp_callback(request_body);

    if (s_has_response) {
        mg_http_reply(c, 200, CORS, "%s", s_response_buffer);
    } else {
        mg_http_reply(c, 200, CORS, "%s", TIMEOUT_RESPONSE);
    }
}
```

### Windows Compatibility

Use Windows threads on Windows:

```c
#ifdef _WIN32
    #include <windows.h>
    static HANDLE s_server_thread;
    // Use CreateThread, WaitForSingleObject
#else
    #include <pthread.h>
    // Use pthread_create, pthread_join
#endif
```

## C# Changes

### MainThreadDispatcher Addition

```csharp
public static class MainThreadDispatcher
{
    // Existing
    public static void Enqueue(Action action);

    // New: synchronous dispatch from background thread
    public static T DispatchAndWait<T>(Func<T> func, int timeoutMs = 30000)
    {
        if (IsMainThread())
            return func();

        var completionSource = new TaskCompletionSource<T>();

        Enqueue(() => {
            try {
                completionSource.SetResult(func());
            } catch (Exception ex) {
                completionSource.SetException(ex);
            }
        });

        if (!completionSource.Task.Wait(timeoutMs))
            throw new TimeoutException("Main thread dispatch timed out");

        return completionSource.Task.Result;
    }
}
```

### NativeProxy Changes

Remove `EditorApplication.update` polling - no longer needed:

```csharp
static NativeProxy()
{
    Initialize();
}

private static void Initialize()
{
    int result = StartServer(DEFAULT_PORT);
    if (result < 0) return;  // Failed

    s_callback = OnRequest;
    RegisterCallback(s_callback);

    AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
    EditorApplication.quitting += OnQuit;

    s_initialized = true;
}

// OnUpdate() removed - no longer needed
```

### Tool Execution

Tools that call Unity APIs wrap them in `DispatchAndWait`:

```csharp
// Before (assumed main thread)
var go = GameObject.Find(name);

// After (works from any thread)
var go = MainThreadDispatcher.DispatchAndWait(() => GameObject.Find(name));
```

## Thread Lifecycle

| Event | Native Thread | C# Callback |
|-------|---------------|-------------|
| DLL loads | Starts | Not registered |
| C# initializes | Running | Registered |
| Domain reload starts | Running | Unregistered |
| During reload | Running, returns "recompiling" | N/A |
| Domain reload ends | Running | Re-registered |
| Editor quits | Stops | Unregistered |

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Request during dispatch | Queued in Mongoose |
| Main thread timeout | Return error response |
| Exception in tool | Return JSON-RPC error |
| Domain reload mid-request | Return "recompiling" error |
| Thread creation fails | Fall back to managed server |

## Benefits

1. **Unity stays responsive** - Main thread only used for actual Unity API calls
2. **Faster requests** - JSON parsing/serialization on background thread
3. **Simpler native code** - No wait loop, cleaner request handling
4. **Domain reload survival** - Thread keeps running, same as before

## Implementation Tasks

1. Update `proxy.c` with thread management (Windows + POSIX)
2. Remove wait loop from `HandleHttpRequest`
3. Add `DispatchAndWait` to `MainThreadDispatcher`
4. Remove `OnUpdate` polling from `NativeProxy.cs`
5. Update tools to use `DispatchAndWait` for Unity APIs
6. Rebuild native DLL
7. Test domain reload behavior
8. Test long-running tool execution
