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
import ctypes
import ctypes.wintypes
import json
import logging
import os
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
unity_port = 8081  # primary port (backwards compat, used for forwarding)
unity_connected = False
last_unity_check = 0
HEALTH_CHECK_INTERVAL = 5  # seconds between Unity health checks
REQUEST_TIMEOUT = 60  # seconds to wait for Unity response (longer than native 30s)
RETRY_INTERVAL = 1  # seconds between retries when Unity is down
MAX_RETRIES = 30  # max retries before giving up on a queued request

# Multi-instance tracking: {port: {"connected": bool, "label": str}}
DISCOVERY_PORTS = range(8081, 8091)  # scan ports 8081-8090
unity_instances = {}


# ─── Window Focus Management (Windows only) ────────────────────────────────

# Focus actions the sidecar handles directly (not forwarded to Unity)
FOCUS_ACTIONS = {"focus", "restore_focus", "set_auto_focus", "get_settings"}

# Win32 API via ctypes
if sys.platform == "win32":
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32

    user32.GetForegroundWindow.restype = ctypes.wintypes.HWND
    user32.SetForegroundWindow.argtypes = [ctypes.wintypes.HWND]
    user32.SetForegroundWindow.restype = ctypes.wintypes.BOOL
    user32.ShowWindow.argtypes = [ctypes.wintypes.HWND, ctypes.c_int]
    user32.ShowWindow.restype = ctypes.wintypes.BOOL
    user32.GetWindowThreadProcessId.argtypes = [ctypes.wintypes.HWND, ctypes.POINTER(ctypes.wintypes.DWORD)]
    user32.GetWindowThreadProcessId.restype = ctypes.wintypes.DWORD
    kernel32.GetCurrentThreadId.restype = ctypes.wintypes.DWORD
    user32.AttachThreadInput.argtypes = [ctypes.wintypes.DWORD, ctypes.wintypes.DWORD, ctypes.wintypes.BOOL]
    user32.AttachThreadInput.restype = ctypes.wintypes.BOOL

    SW_RESTORE = 9
    SW_SHOW = 5

# Sidecar-managed focus state
_previous_foreground_window = 0
_auto_focus_enabled = False
_settings_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".sidecar_settings.json")


def _load_settings():
    """Load persistent settings from disk."""
    global _auto_focus_enabled
    try:
        with open(_settings_file, "r") as f:
            settings = json.load(f)
            _auto_focus_enabled = settings.get("auto_focus", False)
    except (FileNotFoundError, json.JSONDecodeError):
        pass


def _save_settings():
    """Save persistent settings to disk."""
    try:
        with open(_settings_file, "w") as f:
            json.dump({"auto_focus": _auto_focus_enabled}, f)
    except Exception as e:
        logger.warning(f"Failed to save settings: {e}")


def _find_unity_hwnd():
    """Find the Unity Editor main window handle by scanning for its process."""
    if sys.platform != "win32":
        return 0

    import subprocess
    try:
        # Find Unity process(es)
        result = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq Unity.exe", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=5
        )
        for line in result.stdout.strip().split("\n"):
            if "Unity.exe" in line:
                parts = line.strip('"').split('","')
                if len(parts) >= 2:
                    pid = int(parts[1])
                    # Enumerate windows to find one belonging to this PID
                    hwnd = _find_window_by_pid(pid)
                    if hwnd:
                        return hwnd
    except Exception as e:
        logger.debug(f"Failed to find Unity window: {e}")
    return 0


def _find_window_by_pid(target_pid):
    """Enumerate top-level windows and find one matching the target PID."""
    result = [0]

    @ctypes.WINFUNCTYPE(ctypes.wintypes.BOOL, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)
    def enum_callback(hwnd, lparam):
        pid = ctypes.wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == target_pid and user32.IsWindowVisible(hwnd):
            # Check it has a title (main window, not child)
            length = user32.GetWindowTextLengthW(hwnd)
            if length > 0:
                result[0] = hwnd
                return False  # Stop enumeration
        return True

    user32.EnumWindows(enum_callback, 0)
    return result[0]


def _set_foreground_with_attach(hwnd):
    """SetForegroundWindow with AttachThreadInput trick to bypass Windows restriction."""
    fg_hwnd = user32.GetForegroundWindow()
    fg_thread = user32.GetWindowThreadProcessId(fg_hwnd, None)
    cur_thread = kernel32.GetCurrentThreadId()

    attached = False
    if fg_thread != cur_thread:
        attached = bool(user32.AttachThreadInput(cur_thread, fg_thread, True))

    user32.ShowWindow(hwnd, SW_RESTORE)
    focused = bool(user32.SetForegroundWindow(hwnd))

    if attached:
        user32.AttachThreadInput(cur_thread, fg_thread, False)

    return focused


def handle_focus_action(action, params):
    """Handle focus-related manage_editor actions directly in the sidecar."""
    global _previous_foreground_window, _auto_focus_enabled

    if sys.platform != "win32":
        return {"success": False, "error": "Window focus management is only supported on Windows."}

    if action == "focus":
        _previous_foreground_window = user32.GetForegroundWindow()
        unity_hwnd = _find_unity_hwnd()

        if not unity_hwnd:
            return {"success": False, "error": "Could not find Unity Editor window. Is Unity running?"}

        focused = _set_foreground_with_attach(unity_hwnd)
        logger.info(f"Focus Unity: {'success' if focused else 'failed'}")

        return {
            "success": True,
            "focused": focused,
            "previous_window_saved": _previous_foreground_window != 0,
            "handled_by": "sidecar",
            "message": "Unity Editor focused. Use 'restore_focus' to return to previous window."
                if focused else "Focus request sent but may not have succeeded (Windows restrictions)."
        }

    elif action == "restore_focus":
        if _previous_foreground_window == 0:
            return {
                "success": False,
                "error": "No previous window saved. Call 'focus' first to save the previous window."
            }

        user32.ShowWindow(_previous_foreground_window, SW_SHOW)
        restored = bool(user32.SetForegroundWindow(_previous_foreground_window))
        _previous_foreground_window = 0
        logger.info(f"Restore focus: {'success' if restored else 'failed'}")

        return {
            "success": True,
            "restored": restored,
            "handled_by": "sidecar",
            "message": "Focus restored to previous window."
                if restored else "Restore request sent but may not have succeeded (Windows restrictions)."
        }

    elif action == "set_auto_focus":
        enabled = params.get("enabled", False)
        _auto_focus_enabled = bool(enabled)
        _save_settings()
        logger.info(f"Auto-focus {'enabled' if _auto_focus_enabled else 'disabled'}")

        return {
            "success": True,
            "auto_focus": _auto_focus_enabled,
            "handled_by": "sidecar",
            "message": "Auto-focus enabled. Unity will be focused automatically when needed."
                if _auto_focus_enabled else "Auto-focus disabled."
        }

    elif action == "get_settings":
        return {
            "success": True,
            "auto_focus": _auto_focus_enabled,
            "has_previous_window": _previous_foreground_window != 0,
            "platform": "windows",
            "handled_by": "sidecar"
        }

    return None  # Not a focus action


def is_focus_action(parsed_request):
    """Check if a parsed JSON-RPC request is a focus-related manage_editor call."""
    if parsed_request.get("method") != "tools/call":
        return False, None, None
    params = parsed_request.get("params", {})
    if params.get("name") != "manage_editor":
        return False, None, None
    args = params.get("arguments", {})
    action = args.get("action", "")
    if action in FOCUS_ACTIONS:
        return True, action, args
    return False, None, None


# ─── Unity Health Check ─────────────────────────────────────────────────────

def _ping_port(port):
    """Ping a single Unity port. Returns True if responsive."""
    try:
        req = urllib.request.Request(
            f"http://localhost:{port}",
            data=json.dumps({
                "jsonrpc": "2.0",
                "method": "tools/list",
                "id": "_health"
            }).encode(),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=2) as resp:
            resp.read()
            return True
    except Exception:
        return False


def check_unity_health():
    """Ping Unity instances to see which are responsive."""
    global unity_connected, last_unity_check, unity_instances
    now = time.time()
    if now - last_unity_check < HEALTH_CHECK_INTERVAL:
        return unity_connected

    last_unity_check = now

    # Always check primary port
    primary_alive = _ping_port(unity_port)
    unity_connected = primary_alive

    # Discover additional instances on nearby ports
    new_instances = {}
    for port in DISCOVERY_PORTS:
        connected = _ping_port(port) if port != unity_port else primary_alive
        if connected or port in unity_instances:
            label = "Host" if port == 8081 else f"Clone {port - 8081 - 1}"
            new_instances[port] = {"connected": connected, "label": label}

    # If primary port not in range, add it too
    if unity_port not in new_instances:
        new_instances[unity_port] = {"connected": primary_alive, "label": "Host"}

    unity_instances = new_instances
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
            instances = [
                {"port": port, "connected": info["connected"], "label": info["label"]}
                for port, info in sorted(unity_instances.items())
            ]
            status = {
                "sidecar": "running",
                "unity_connected": unity_connected,
                "unity_port": unity_port,
                "auto_focus": _auto_focus_enabled,
                "instances": instances,
            }
            body = json.dumps(status).encode()
            try:
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                pass  # Client disconnected before response was sent
        else:
            self.send_error(404)

    def do_POST(self):
        """Forward JSON-RPC requests to Unity, intercepting focus actions."""
        content_length = int(self.headers.get("Content-Length", 0))
        body_bytes = self.rfile.read(content_length)

        # Parse request for routing
        request_id = "?"
        parsed = None
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

        # Check if this is a focus action we handle in the sidecar
        if parsed:
            is_focus, action, args = is_focus_action(parsed)
            if is_focus:
                logger.info(f"  [{request_id}] Handling focus action '{action}' in sidecar")
                result = handle_focus_action(action, args)
                if result is not None:
                    response = json.dumps({
                        "jsonrpc": "2.0",
                        "result": {
                            "content": [{"type": "text", "text": json.dumps(result, indent=2)}]
                        },
                        "id": request_id
                    }).encode()
                    try:
                        self.send_response(200)
                        self.send_header("Content-Type", "application/json")
                        self.send_header("Content-Length", str(len(response)))
                        self.end_headers()
                        self.wfile.write(response)
                        logger.info(f"← [{request_id}] {len(response)} bytes (sidecar-handled)")
                    except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                        pass
                    return

        try:
            response_bytes = forward_with_retry(body_bytes, request_id)

            try:
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(response_bytes)))
                self.end_headers()
                self.wfile.write(response_bytes)
                logger.info(f"← [{request_id}] {len(response_bytes)} bytes")
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                pass

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

            try:
                self.send_response(200)  # JSON-RPC errors are still HTTP 200
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(error_response)))
                self.end_headers()
                self.wfile.write(error_response)
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                pass


# ─── Health check background thread ─────────────────────────────────────────

def health_check_loop():
    """Periodically check Unity connectivity and log state changes."""
    prev_states = {}
    while True:
        check_unity_health()
        # Log per-instance state changes
        for port, info in unity_instances.items():
            was = prev_states.get(port)
            now_connected = info["connected"]
            if was is None or was != now_connected:
                if now_connected:
                    logger.info("Unity %s connected on port %d", info["label"], port)
                else:
                    logger.warning("Unity %s disconnected from port %d", info["label"], port)
        prev_states = {p: i["connected"] for p, i in unity_instances.items()}
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

    # Load persistent settings (auto-focus, etc.)
    _load_settings()

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
