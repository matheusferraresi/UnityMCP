/*
 * UnityMCP Native Proxy - Implementation
 *
 * This native plugin provides an HTTP server that survives Unity domain reloads.
 * It acts as a proxy between the external MCP server and Unity's managed code.
 *
 * When C# is unavailable (during recompile), it returns a "recompiling" message.
 * When C# is available, it forwards requests via callback and waits for response.
 *
 * License: GPLv2 (compatible with Mongoose library)
 */

#include "proxy.h"
#include "mongoose.h"
#include <string.h>
#include <stdio.h>
#include <stdlib.h>

/*
 * Internal state
 */
static struct mg_mgr s_mgr;
static struct mg_connection* s_listener = NULL;
static volatile int s_running = 0;
static volatile int s_callback_valid = 0;
static RequestCallback s_csharp_callback = NULL;

/* Response buffer for synchronous C# callback */
static char s_response_buffer[PROXY_MAX_RESPONSE_SIZE];
static volatile int s_has_response = 0;

/*
 * Standard JSON-RPC error responses
 */
static const char* RECOMPILING_RESPONSE =
    "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,"
    "\"message\":\"Unity is recompiling. Please retry in a moment.\"},\"id\":null}";

static const char* TIMEOUT_RESPONSE =
    "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,"
    "\"message\":\"Request timed out.\"},\"id\":null}";

static const char* INTERNAL_ERROR_RESPONSE =
    "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,"
    "\"message\":\"Internal error processing request.\"},\"id\":null}";

/*
 * CORS headers for all responses
 */
static const char* CORS_HEADERS =
    "Content-Type: application/json\r\n"
    "Access-Control-Allow-Origin: *\r\n"
    "Access-Control-Allow-Methods: POST, OPTIONS\r\n"
    "Access-Control-Allow-Headers: Content-Type\r\n";

/*
 * Handle an incoming HTTP request.
 *
 * This function processes the HTTP request:
 * 1. CORS preflight (OPTIONS) -> 204 No Content
 * 2. Non-POST methods -> 405 Method Not Allowed
 * 3. If callback not valid -> Return "recompiling" response
 * 4. Otherwise -> Call C# callback and wait for response
 */
static void HandleHttpRequest(struct mg_connection* connection, struct mg_http_message* http_message)
{
    /* Handle CORS preflight request */
    if (mg_strcmp(http_message->method, mg_str("OPTIONS")) == 0)
    {
        mg_http_reply(connection, 204, CORS_HEADERS, "");
        return;
    }

    /* Only allow POST method for JSON-RPC */
    if (mg_strcmp(http_message->method, mg_str("POST")) != 0)
    {
        mg_http_reply(connection, 405,
            "Content-Type: text/plain\r\n"
            "Access-Control-Allow-Origin: *\r\n",
            "Method Not Allowed. Use POST for JSON-RPC requests.");
        return;
    }

    /* Check if C# callback is available */
    if (!s_callback_valid || s_csharp_callback == NULL)
    {
        mg_http_reply(connection, 200, CORS_HEADERS, "%s", RECOMPILING_RESPONSE);
        return;
    }

    /* Extract request body with null termination */
    size_t body_length = http_message->body.len;
    if (body_length == 0)
    {
        mg_http_reply(connection, 400, CORS_HEADERS,
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32700,"
            "\"message\":\"Parse error: Empty request body.\"},\"id\":null}");
        return;
    }

    char* request_body = (char*)malloc(body_length + 1);
    if (request_body == NULL)
    {
        mg_http_reply(connection, 200, CORS_HEADERS, "%s", INTERNAL_ERROR_RESPONSE);
        return;
    }

    memcpy(request_body, http_message->body.buf, body_length);
    request_body[body_length] = '\0';

    /* Clear response state and invoke C# callback */
    s_has_response = 0;
    s_response_buffer[0] = '\0';

    /*
     * Call the C# callback. This is a synchronous call from native to managed code.
     * C# must call SendResponse() before returning from the callback.
     */
    s_csharp_callback(request_body);

    free(request_body);
    request_body = NULL;

    /*
     * Wait for the response with timeout.
     * The callback should have set s_has_response = 1 via SendResponse().
     * We poll the event manager while waiting to keep the server responsive.
     */
    int waited_ms = 0;
    const int poll_interval_ms = 10;

    while (!s_has_response && waited_ms < PROXY_REQUEST_TIMEOUT_MS)
    {
        mg_mgr_poll(&s_mgr, poll_interval_ms);
        waited_ms += poll_interval_ms;

        /* Re-check callback validity in case domain reload started during wait */
        if (!s_callback_valid)
        {
            break;
        }
    }

    /* Send the response */
    if (s_has_response && s_response_buffer[0] != '\0')
    {
        mg_http_reply(connection, 200, CORS_HEADERS, "%s", s_response_buffer);
    }
    else if (!s_callback_valid)
    {
        /* Domain reload happened while waiting */
        mg_http_reply(connection, 200, CORS_HEADERS, "%s", RECOMPILING_RESPONSE);
    }
    else
    {
        /* Timeout or empty response */
        mg_http_reply(connection, 200, CORS_HEADERS, "%s", TIMEOUT_RESPONSE);
    }
}

/*
 * Mongoose event handler for all connection events.
 */
static void EventHandler(struct mg_connection* connection, int event, void* event_data)
{
    if (event == MG_EV_HTTP_MSG)
    {
        struct mg_http_message* http_message = (struct mg_http_message*)event_data;
        HandleHttpRequest(connection, http_message);
    }
}

/*
 * Start the HTTP server on the specified port.
 */
EXPORT int StartServer(int port)
{
    if (s_running)
    {
        return 1;  /* Already running */
    }

    /* Initialize the event manager */
    mg_mgr_init(&s_mgr);

    /* Build the listen address string */
    char listen_address[64];
    snprintf(listen_address, sizeof(listen_address), "http://0.0.0.0:%d", port);

    /* Start listening for HTTP connections */
    s_listener = mg_http_listen(&s_mgr, listen_address, EventHandler, NULL);
    if (s_listener == NULL)
    {
        mg_mgr_free(&s_mgr);
        return -1;  /* Failed to bind to port */
    }

    s_running = 1;
    return 0;
}

/*
 * Stop the HTTP server and release resources.
 */
EXPORT void StopServer(void)
{
    if (!s_running)
    {
        return;
    }

    s_running = 0;
    s_listener = NULL;
    s_callback_valid = 0;
    s_csharp_callback = NULL;

    mg_mgr_free(&s_mgr);
}

/*
 * Register the C# callback for handling requests.
 */
EXPORT void RegisterCallback(RequestCallback callback)
{
    s_csharp_callback = callback;
    s_callback_valid = (callback != NULL) ? 1 : 0;

    /* Clear any pending response state when callback changes */
    s_has_response = 0;
    s_response_buffer[0] = '\0';
}

/*
 * Send a response back to the waiting HTTP request.
 */
EXPORT void SendResponse(const char* json)
{
    if (json == NULL)
    {
        return;
    }

    size_t json_length = strlen(json);
    if (json_length >= PROXY_MAX_RESPONSE_SIZE)
    {
        /* Response too large, truncate with error indicator */
        strncpy(s_response_buffer, json, PROXY_MAX_RESPONSE_SIZE - 1);
        s_response_buffer[PROXY_MAX_RESPONSE_SIZE - 1] = '\0';
    }
    else
    {
        strcpy(s_response_buffer, json);
    }

    s_has_response = 1;
}

/*
 * Poll the event manager to process network events.
 */
EXPORT void PollEvents(int timeout_ms)
{
    if (!s_running)
    {
        return;
    }

    mg_mgr_poll(&s_mgr, timeout_ms);
}

/*
 * Check if the server is currently running.
 */
EXPORT int IsServerRunning(void)
{
    return s_running;
}

/*
 * Check if a C# callback is currently registered.
 */
EXPORT int IsCallbackValid(void)
{
    return s_callback_valid;
}
