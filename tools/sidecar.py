#!/usr/bin/env python3
"""
UnixxtyMCP Sidecar — Transparent proxy that sits between Claude Code and Unity.

Architecture:
  Claude Code ──HTTP:8080──→ sidecar.py ──HTTP:8081──→ Unity (native DLL)

Benefits over direct connection:
  - Port 8080 stays alive across Unity restarts
  - Request queuing during domain reloads (Unity temporarily unreachable on 8081)
  - Health/status endpoint
  - Persistent logging
  - No response size limit on the sidecar side

Usage:
  python sidecar.py [--port 8080] [--unity-port 8081] [--log sidecar.log]
"""

import argparse
import json
import logging
import sys
import time
import threading
import urllib.request
import urllib.error
from http.server import HTTPServer, BaseHTTPRequestHandler
from collections import deque

# ─── Configuration ───────────────────────────────────────────────────────────

logger = logging.getLogger("sidecar")

# Global state
unity_port = 8081
unity_connected = False
last_unity_check = 0
HEALTH_CHECK_INTERVAL = 5  # seconds between Unity health checks
REQUEST_TIMEOUT = 60  # seconds to wait for Unity response (longer than native 30s)
RETRY_INTERVAL = 1  # seconds between retries when Unity is down
MAX_RETRIES = 30  # max retries before giving up on a queued request


# ─── Unity Health Check ─────────────────────────────────────────────────────

def check_unity_health():
    """Ping Unity's native DLL to see if it's responsive."""
    global unity_connected, last_unity_check
    now = time.time()
    if now - last_unity_check < HEALTH_CHECK_INTERVAL:
        return unity_connected

    last_unity_check = now
    try:
        req = urllib.request.Request(
            f"http://localhost:{unity_port}",
            data=json.dumps({
                "jsonrpc": "2.0",
                "method": "tools/list",
                "id": "_health"
            }).encode(),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=3) as resp:
            resp.read()
            unity_connected = True
    except Exception:
        unity_connected = False

    return unity_connected


def forward_to_unity(body_bytes, timeout=REQUEST_TIMEOUT):
    """Forward a JSON-RPC request to Unity and return the response bytes."""
    req = urllib.request.Request(
        f"http://localhost:{unity_port}",
        data=body_bytes,
        headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def forward_with_retry(body_bytes, request_id="?"):
    """Forward to Unity with retries during domain reloads / restarts."""
    last_error = None
    for attempt in range(MAX_RETRIES):
        try:
            result = forward_to_unity(body_bytes)
            if attempt > 0:
                logger.info(f"  [req {request_id}] Unity responded after {attempt + 1} attempts")
            return result
        except (urllib.error.URLError, ConnectionRefusedError, OSError) as e:
            last_error = e
            logger.debug(f"  [req {request_id}] Unity unreachable (attempt {attempt + 1}/{MAX_RETRIES}): {e}")
            time.sleep(RETRY_INTERVAL)
        except Exception as e:
            # Non-retryable error (e.g., timeout with data partially received)
            raise e

    raise last_error or Exception("Unity unreachable after max retries")


# ─── HTTP Request Handler ────────────────────────────────────────────────────

class SidecarHandler(BaseHTTPRequestHandler):
    """Handles incoming MCP requests from Claude Code."""

    def log_message(self, format, *args):
        """Route access logs through our logger."""
        logger.debug(f"HTTP: {format % args}")

    def do_GET(self):
        """Health/status endpoint."""
        if self.path == "/status":
            check_unity_health()
            status = {
                "sidecar": "running",
                "unity_connected": unity_connected,
                "unity_port": unity_port,
            }
            body = json.dumps(status).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_error(404)

    def do_POST(self):
        """Forward JSON-RPC requests to Unity."""
        content_length = int(self.headers.get("Content-Length", 0))
        body_bytes = self.rfile.read(content_length)

        # Parse request ID for logging
        request_id = "?"
        try:
            parsed = json.loads(body_bytes)
            request_id = parsed.get("id", "?")
            method = parsed.get("method", "?")
            tool_name = ""
            if method == "tools/call":
                tool_name = f" → {parsed.get('params', {}).get('name', '?')}"
            logger.info(f"→ [{request_id}] {method}{tool_name}")
        except Exception:
            pass

        try:
            response_bytes = forward_with_retry(body_bytes, request_id)

            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(response_bytes)))
            self.end_headers()
            self.wfile.write(response_bytes)

            # Log response size
            logger.info(f"← [{request_id}] {len(response_bytes)} bytes")

        except Exception as e:
            # Unity is down — return a proper JSON-RPC error
            logger.warning(f"✗ [{request_id}] Unity unreachable: {e}")
            error_response = json.dumps({
                "jsonrpc": "2.0",
                "error": {
                    "code": -32000,
                    "message": f"Unity not reachable on port {unity_port}. Is Unity running with Unixxty MCP active?"
                },
                "id": request_id
            }).encode()

            self.send_response(200)  # JSON-RPC errors are still HTTP 200
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(error_response)))
            self.end_headers()
            self.wfile.write(error_response)


# ─── Health check background thread ─────────────────────────────────────────

def health_check_loop():
    """Periodically check Unity connectivity and log state changes."""
    global unity_connected
    was_connected = None
    while True:
        check_unity_health()
        if unity_connected != was_connected:
            if unity_connected:
                logger.info("Unity connected on port %d", unity_port)
            else:
                logger.warning("Unity disconnected from port %d", unity_port)
            was_connected = unity_connected
        time.sleep(HEALTH_CHECK_INTERVAL)


# ─── Main ────────────────────────────────────────────────────────────────────

def main():
    global unity_port

    parser = argparse.ArgumentParser(description="UnixxtyMCP Sidecar — proxy between Claude Code and Unity")
    parser.add_argument("--port", type=int, default=8080, help="Port for Claude Code to connect to (default: 8080)")
    parser.add_argument("--unity-port", type=int, default=8081, help="Unity MCP port (default: 8081)")
    parser.add_argument("--log", type=str, default=None, help="Log file path (default: stderr only)")
    parser.add_argument("--verbose", action="store_true", help="Enable debug logging")
    args = parser.parse_args()

    unity_port = args.unity_port

    # Setup logging
    log_level = logging.DEBUG if args.verbose else logging.INFO
    handlers = [logging.StreamHandler(sys.stderr)]
    if args.log:
        handlers.append(logging.FileHandler(args.log))
    logging.basicConfig(
        level=log_level,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
        handlers=handlers
    )

    # Start health check thread
    health_thread = threading.Thread(target=health_check_loop, daemon=True)
    health_thread.start()

    # Start HTTP server
    server = HTTPServer(("127.0.0.1", args.port), SidecarHandler)
    logger.info(f"Sidecar listening on http://127.0.0.1:{args.port}")
    logger.info(f"Forwarding to Unity on http://127.0.0.1:{unity_port}")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info("Sidecar shutting down")
        server.shutdown()


if __name__ == "__main__":
    main()
