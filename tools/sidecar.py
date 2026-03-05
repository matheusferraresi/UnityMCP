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

# ─── Exclusive Operation Coordinator ─────────────────────────────────────────
# Prevents multi-agent conflicts for operations that must not overlap
# (compilation, play mode transitions, scene loads).

_exclusive_lock = threading.Lock()
_exclusive_op = None  # {category, tool, job_id, started, request_id}
LOCK_TIMEOUT = 120  # seconds — auto-expire stale locks

EXCLUSIVE_TOOLS = {
    "compile_and_watch": {"category": "compile", "async": True,
                          "condition": lambda args: args.get("action", "start") == "start"},
    "recompile_scripts": {"category": "compile", "async": False},
    "unity_refresh":     {"category": "compile", "async": False,
                          "condition": lambda args: args.get("compile") == "request"},
    "playmode_enter":    {"category": "playmode", "async": False},
    "playmode_exit":     {"category": "playmode", "async": False},
    "debug_play":        {"category": "playmode", "async": False},
    "scene_load":        {"category": "scene", "async": False},
    "scene_create":      {"category": "scene", "async": False},
}

CATEGORY_LABELS = {
    "compile": "Compilation",
    "playmode": "Play mode transition",
    "scene": "Scene operation",
}


def _is_lock_expired():
    """Check if the current exclusive lock has exceeded LOCK_TIMEOUT."""
    if _exclusive_op is None:
        return False
    return (time.time() - _exclusive_op["started"]) > LOCK_TIMEOUT


def classify_tool(name, args):
    """Return the exclusive category for a tool, or None if not exclusive."""
    spec = EXCLUSIVE_TOOLS.get(name)
    if spec is None:
        return None
    condition = spec.get("condition")
    if condition and not condition(args):
        return None
    return spec["category"]


def try_acquire_exclusive(tool_name, category, args, request_id):
    """
    Try to acquire the exclusive lock for an operation.
    Returns (allowed, response_override).
      - allowed=True, response_override=None  → forward to Unity
      - allowed=False, response_override=dict → return this to the agent immediately
    """
    global _exclusive_op

    with _exclusive_lock:
        # Auto-expire stale locks
        if _exclusive_op is not None and _is_lock_expired():
            logger.warning(
                f"  [{request_id}] Expired stale exclusive lock: "
                f"{_exclusive_op['tool']} (held {time.time() - _exclusive_op['started']:.0f}s)"
            )
            _exclusive_op = None

        if _exclusive_op is None:
            # No active exclusive op — acquire lock
            _exclusive_op = {
                "category": category,
                "tool": tool_name,
                "job_id": None,
                "started": time.time(),
                "request_id": request_id,
            }
            logger.info(f"  [{request_id}] Acquired exclusive lock: {category}/{tool_name}")
            return True, None

        active = _exclusive_op

        # Same category — redirect or coalesce
        if active["category"] == category:
            # compile_and_watch(start) while another compile is active → attach to existing job
            if tool_name == "compile_and_watch" and args.get("action") == "start":
                job_id = active.get("job_id") or "pending"
                logger.info(
                    f"  [{request_id}] Attached to existing {active['tool']} "
                    f"(job_id={job_id}, started by [{active['request_id']}])"
                )
                return False, {
                    "success": True,
                    "message": "Attached to compilation started by another agent",
                    "job_id": job_id,
                    "status": "compiling",
                    "coordinated_by": "sidecar",
                }

            # Same category, different tool (e.g. recompile_scripts while compile_and_watch active)
            label = CATEGORY_LABELS.get(category, category)
            logger.info(f"  [{request_id}] Blocked by active {active['tool']} (same category: {category})")
            return False, {
                "success": False,
                "error": f"{label} already in progress ({active['tool']}). "
                         f"Wait for it to complete before starting {tool_name}.",
                "retry_after_ms": 3000,
                "coordinated_by": "sidecar",
            }

        # Different category — block with hint
        active_label = CATEGORY_LABELS.get(active["category"], active["category"])
        logger.info(
            f"  [{request_id}] Blocked: {tool_name} ({category}) "
            f"cannot run during {active['tool']} ({active['category']})"
        )
        return False, {
            "success": False,
            "error": f"{active_label} in progress ({active['tool']}). "
                     f"Wait for it to complete before {tool_name}.",
            "retry_after_ms": 3000,
            "coordinated_by": "sidecar",
        }


def release_exclusive(reason="completed"):
    """Release the exclusive lock."""
    global _exclusive_op
    with _exclusive_lock:
        if _exclusive_op is not None:
            logger.info(
                f"  Released exclusive lock: {_exclusive_op['tool']} ({reason}, "
                f"held {time.time() - _exclusive_op['started']:.1f}s)"
            )
            _exclusive_op = None


def check_release_on_response(tool_name, args, response_bytes):
    """Inspect a Unity response and release the exclusive lock if the operation completed."""
    global _exclusive_op

    with _exclusive_lock:
        if _exclusive_op is None:
            return

        spec = EXCLUSIVE_TOOLS.get(tool_name)

        # For async tools (compile_and_watch): extract job_id on start, release on completion
        if tool_name == "compile_and_watch":
            action = args.get("action", "start")

            if action == "start" and _exclusive_op["job_id"] is None:
                # Extract job_id from response
                try:
                    resp = json.loads(response_bytes)
                    content = resp.get("result", {}).get("content", [])
                    for item in content:
                        if item.get("type") == "text":
                            data = json.loads(item["text"])
                            job_id = data.get("job_id")
                            if job_id:
                                _exclusive_op["job_id"] = job_id
                                logger.info(f"  Stored job_id={job_id} for exclusive compile op")
                except Exception:
                    pass
                return  # Don't release — compilation is ongoing

            if action == "get_job":
                # Check if job completed
                try:
                    resp = json.loads(response_bytes)
                    content = resp.get("result", {}).get("content", [])
                    for item in content:
                        if item.get("type") == "text":
                            data = json.loads(item["text"])
                            status = data.get("status", "")
                            if status in ("succeeded", "failed"):
                                _exclusive_op = None
                                logger.info(f"  Released exclusive lock: compile job {status}")
                except Exception:
                    pass
                return

        # For synchronous exclusive tools: release as soon as we get a response
        if spec and not spec.get("async", False):
            _exclusive_op = None
            logger.info(f"  Released exclusive lock: {tool_name} (sync response received)")


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


DOMAIN_RELOAD_MARKERS = [
    "domain reload",
    "Domain Reload",
    "Request interrupted",
    "AppDomainUnloadedException",
    "request processing timed out",
]
DOMAIN_RELOAD_MAX_RETRIES = 10  # retry up to 10 times specifically for domain reload responses


def _is_domain_reload_response(response_bytes):
    """Check if a Unity response indicates a domain reload is in progress."""
    try:
        text = response_bytes.decode("utf-8", errors="replace")
        return any(marker in text for marker in DOMAIN_RELOAD_MARKERS)
    except Exception:
        return False


def forward_with_retry(body_bytes, request_id="?"):
    """Forward to Unity with retries during domain reloads / restarts."""
    last_error = None
    domain_reload_retries = 0
    for attempt in range(MAX_RETRIES):
        try:
            result = forward_to_unity(body_bytes)

            # Check if Unity returned a domain reload error in the response body
            if _is_domain_reload_response(result):
                domain_reload_retries += 1
                if domain_reload_retries <= DOMAIN_RELOAD_MAX_RETRIES:
                    logger.info(
                        f"  [req {request_id}] Domain reload detected in response "
                        f"(retry {domain_reload_retries}/{DOMAIN_RELOAD_MAX_RETRIES})"
                    )
                    time.sleep(RETRY_INTERVAL)
                    continue
                else:
                    logger.warning(
                        f"  [req {request_id}] Domain reload persists after "
                        f"{DOMAIN_RELOAD_MAX_RETRIES} retries, returning response as-is"
                    )

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
        """Health/status endpoint. Returns cached state (background thread updates it)."""
        if self.path == "/status":
            instances = [
                {"port": port, "connected": info["connected"], "label": info["label"]}
                for port, info in sorted(unity_instances.items())
            ]
            exclusive_info = None
            with _exclusive_lock:
                if _exclusive_op is not None:
                    exclusive_info = {
                        "category": _exclusive_op["category"],
                        "tool": _exclusive_op["tool"],
                        "job_id": _exclusive_op.get("job_id"),
                        "elapsed_seconds": round(time.time() - _exclusive_op["started"], 1),
                    }
            status = {
                "sidecar": "running",
                "unity_connected": unity_connected,
                "unity_port": unity_port,
                "auto_focus": _auto_focus_enabled,
                "exclusive_op": exclusive_info,
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

    def _send_json(self, data_bytes, label=""):
        """Send a JSON response, swallowing broken-pipe errors."""
        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data_bytes)))
            self.end_headers()
            self.wfile.write(data_bytes)
            if label:
                logger.info(f"← {label}")
        except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
            pass

    def _make_jsonrpc_result(self, result_dict, request_id):
        """Wrap a dict as a JSON-RPC 2.0 success response with MCP content."""
        return json.dumps({
            "jsonrpc": "2.0",
            "result": {
                "content": [{"type": "text", "text": json.dumps(result_dict, indent=2)}]
            },
            "id": request_id,
        }).encode()

    def do_POST(self):
        """Forward JSON-RPC requests to Unity, with focus interception and exclusive op coordination."""
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

        # ── Sidecar-handled: focus actions ──
        if parsed:
            is_focus, action, focus_args = is_focus_action(parsed)
            if is_focus:
                logger.info(f"  [{request_id}] Handling focus action '{action}' in sidecar")
                result = handle_focus_action(action, focus_args)
                if result is not None:
                    response = self._make_jsonrpc_result(result, request_id)
                    self._send_json(response, f"[{request_id}] {len(response)} bytes (sidecar-handled)")
                    return

        # ── Exclusive operation coordination ──
        tool_name_str = ""
        tool_args = {}
        is_exclusive = False
        exclusive_category = None

        if parsed and parsed.get("method") == "tools/call":
            params = parsed.get("params", {})
            tool_name_str = params.get("name", "")
            tool_args = params.get("arguments", {})
            exclusive_category = classify_tool(tool_name_str, tool_args)

            if exclusive_category:
                is_exclusive = True
                allowed, override = try_acquire_exclusive(
                    tool_name_str, exclusive_category, tool_args, request_id
                )
                if not allowed:
                    response = self._make_jsonrpc_result(override, request_id)
                    self._send_json(response, f"[{request_id}] {len(response)} bytes (coordinated)")
                    return

        # ── Forward to Unity ──
        try:
            response_bytes = forward_with_retry(body_bytes, request_id)

            # Check if this response should release an exclusive lock
            if tool_name_str:
                check_release_on_response(tool_name_str, tool_args, response_bytes)
            # Also check get_job polling (compile_and_watch with action=get_job is not exclusive itself)
            if tool_name_str == "compile_and_watch" and tool_args.get("action") == "get_job":
                check_release_on_response(tool_name_str, tool_args, response_bytes)

            self._send_json(response_bytes, f"[{request_id}] {len(response_bytes)} bytes")

        except Exception as e:
            # Unity is down — release exclusive lock if we were the ones holding it
            if is_exclusive:
                release_exclusive(reason=f"Unity unreachable: {e}")

            logger.warning(f"✗ [{request_id}] Unity unreachable: {e}")
            error_response = json.dumps({
                "jsonrpc": "2.0",
                "error": {
                    "code": -32000,
                    "message": f"Unity not reachable on port {unity_port}. Is Unity running with Unixxty MCP active?"
                },
                "id": request_id
            }).encode()
            self._send_json(error_response)


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

    # Guard: check if port is already in use
    import socket
    test_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        test_sock.bind(("127.0.0.1", args.port))
        test_sock.close()
    except OSError:
        logger.error(f"Port {args.port} is already in use. Another sidecar may be running.")
        sys.exit(1)

    # Start HTTP server (threaded so health checks don't block request handling)
    class ThreadedHTTPServer(HTTPServer):
        from socketserver import ThreadingMixIn
        # Inline mixin to avoid import at module level
    ThreadedHTTPServer = type("ThreadedHTTPServer",
                              (__import__("socketserver").ThreadingMixIn, HTTPServer),
                              {"daemon_threads": True})
    server = ThreadedHTTPServer(("127.0.0.1", args.port), SidecarHandler)
    logger.info(f"Sidecar listening on http://127.0.0.1:{args.port}")
    logger.info(f"Forwarding to Unity on http://127.0.0.1:{unity_port}")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info("Sidecar shutting down")
        server.shutdown()


if __name__ == "__main__":
    main()
