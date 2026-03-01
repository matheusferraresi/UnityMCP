#!/usr/bin/env python3
"""
UnixxtyMCP Dev Runner — Auto-reloads sidecar and triggers Unity recompile on file changes.

Watches:
  tools/*.py          → Restart sidecar process
  Package/**/*.cs     → Trigger Unity recompile via MCP

Usage:
  python tools/dev.py [sidecar args...]

Examples:
  python tools/dev.py                          # defaults
  python tools/dev.py --verbose --log dev.log  # passed to sidecar.py
"""

import os
import sys
import signal
import subprocess
import time

# ─── Configuration ───────────────────────────────────────────────────────────

POLL_INTERVAL = 1.5  # seconds between file checks
DEBOUNCE_SECONDS = 2.0  # ignore rapid successive changes
SIDECAR_RESTART_DELAY = 0.3  # brief pause before restarting sidecar

# Resolve paths relative to this script
TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(TOOLS_DIR)
PACKAGE_DIR = os.path.join(PROJECT_ROOT, "Package")
SIDECAR_SCRIPT = os.path.join(TOOLS_DIR, "sidecar.py")

# Colors for terminal output
RED = "\033[31m"
GREEN = "\033[32m"
YELLOW = "\033[33m"
CYAN = "\033[36m"
DIM = "\033[2m"
RESET = "\033[0m"
BOLD = "\033[1m"


# ─── File Watching ───────────────────────────────────────────────────────────

def collect_files(directory, extension):
    """Collect all files with given extension under directory."""
    files = {}
    for root, dirs, filenames in os.walk(directory):
        # Skip hidden dirs and __pycache__
        dirs[:] = [d for d in dirs if not d.startswith(".") and d != "__pycache__"]
        for f in filenames:
            if f.endswith(extension):
                path = os.path.join(root, f)
                try:
                    files[path] = os.stat(path).st_mtime
                except OSError:
                    pass
    return files


def find_changes(old_snapshot, new_snapshot):
    """Compare two snapshots and return list of (path, change_type) tuples."""
    changes = []
    for path, mtime in new_snapshot.items():
        if path not in old_snapshot:
            changes.append((path, "added"))
        elif mtime > old_snapshot[path]:
            changes.append((path, "modified"))
    for path in old_snapshot:
        if path not in new_snapshot:
            changes.append((path, "deleted"))
    return changes


def relative_path(path):
    """Make path relative to project root for cleaner display."""
    try:
        return os.path.relpath(path, PROJECT_ROOT)
    except ValueError:
        return path


# ─── Sidecar Process Management ─────────────────────────────────────────────

class SidecarProcess:
    def __init__(self, extra_args):
        self.extra_args = extra_args
        self.process = None

    def start(self):
        """Start the sidecar subprocess."""
        cmd = [sys.executable, SIDECAR_SCRIPT] + self.extra_args
        self.process = subprocess.Popen(
            cmd,
            # Inherit stdout/stderr so logs appear in the same terminal
            stdout=sys.stdout,
            stderr=sys.stderr,
        )
        print(f"{GREEN}[dev]{RESET} Sidecar started (PID {self.process.pid})")

    def stop(self):
        """Stop the sidecar subprocess gracefully."""
        if self.process and self.process.poll() is None:
            print(f"{YELLOW}[dev]{RESET} Stopping sidecar (PID {self.process.pid})...")
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
            print(f"{DIM}[dev]{RESET} Sidecar stopped")

    def restart(self):
        """Stop then start the sidecar."""
        self.stop()
        time.sleep(SIDECAR_RESTART_DELAY)
        self.start()

    def is_alive(self):
        return self.process and self.process.poll() is None


# ─── Unity Recompile Trigger ────────────────────────────────────────────────

def trigger_unity_recompile(port=8081):
    """Send recompile_scripts request to Unity via its direct port."""
    import json
    import urllib.request
    import urllib.error

    request_body = json.dumps({
        "jsonrpc": "2.0",
        "method": "tools/call",
        "params": {
            "name": "recompile_scripts",
            "arguments": {}
        },
        "id": "_dev_recompile"
    }).encode()

    try:
        req = urllib.request.Request(
            f"http://localhost:{port}",
            data=request_body,
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            result = json.loads(resp.read())
            # Check if it returned a compile job or error
            content = result.get("result", {}).get("content", [{}])
            if content:
                text = content[0].get("text", "")
                if "error" in text.lower():
                    print(f"{RED}[dev]{RESET} Unity recompile error: {text[:200]}")
                else:
                    print(f"{CYAN}[dev]{RESET} Unity recompile triggered")
            return True
    except urllib.error.URLError:
        print(f"{DIM}[dev]{RESET} Unity not reachable — skipping recompile trigger")
        return False
    except Exception as e:
        print(f"{DIM}[dev]{RESET} Recompile trigger failed: {e}")
        return False


# ─── Main Loop ───────────────────────────────────────────────────────────────

def main():
    # Everything after script name is passed to sidecar
    sidecar_args = sys.argv[1:]

    # Extract unity-port from args if present (for recompile trigger)
    unity_port = 8081
    for i, arg in enumerate(sidecar_args):
        if arg == "--unity-port" and i + 1 < len(sidecar_args):
            try:
                unity_port = int(sidecar_args[i + 1])
            except ValueError:
                pass

    print(f"{BOLD}{CYAN}UnixxtyMCP Dev Runner{RESET}")
    print(f"{DIM}Watching for file changes...{RESET}")
    print(f"{DIM}  tools/*.py        → restart sidecar{RESET}")
    print(f"{DIM}  Package/**/*.cs   → trigger Unity recompile{RESET}")
    print()

    sidecar = SidecarProcess(sidecar_args)
    sidecar.start()

    # Take initial snapshots
    py_snapshot = collect_files(TOOLS_DIR, ".py")
    cs_snapshot = collect_files(PACKAGE_DIR, ".cs")
    last_change_time = 0

    # Handle Ctrl+C gracefully
    def signal_handler(sig, frame):
        print(f"\n{YELLOW}[dev]{RESET} Shutting down...")
        sidecar.stop()
        sys.exit(0)

    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    try:
        while True:
            time.sleep(POLL_INTERVAL)

            now = time.time()

            # Check if sidecar died unexpectedly
            if not sidecar.is_alive():
                print(f"{RED}[dev]{RESET} Sidecar exited unexpectedly — restarting...")
                time.sleep(1)
                sidecar.start()
                continue

            # Debounce: skip if a change was just processed
            if now - last_change_time < DEBOUNCE_SECONDS:
                continue

            # Check Python file changes → restart sidecar
            new_py = collect_files(TOOLS_DIR, ".py")
            py_changes = find_changes(py_snapshot, new_py)
            if py_changes:
                last_change_time = now
                for path, change_type in py_changes:
                    print(f"{YELLOW}[dev]{RESET} {change_type}: {relative_path(path)}")
                print(f"{YELLOW}[dev]{RESET} Restarting sidecar...")
                sidecar.restart()
                py_snapshot = collect_files(TOOLS_DIR, ".py")  # re-snapshot after restart
                cs_snapshot = collect_files(PACKAGE_DIR, ".cs")  # also refresh
                continue

            py_snapshot = new_py

            # Check C# file changes → trigger Unity recompile
            new_cs = collect_files(PACKAGE_DIR, ".cs")
            cs_changes = find_changes(cs_snapshot, new_cs)
            if cs_changes:
                last_change_time = now
                for path, change_type in cs_changes:
                    print(f"{CYAN}[dev]{RESET} {change_type}: {relative_path(path)}")
                trigger_unity_recompile(unity_port)
                cs_snapshot = collect_files(PACKAGE_DIR, ".cs")  # re-snapshot
                continue

            cs_snapshot = new_cs

    except KeyboardInterrupt:
        pass
    finally:
        sidecar.stop()


if __name__ == "__main__":
    main()
