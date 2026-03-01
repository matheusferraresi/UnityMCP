#!/usr/bin/env python3
"""Wait for Unity MCP server to be ready. Polls until connected and not compiling."""

import argparse
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
import urllib.error


def check_ready(port):
    """Returns (connected, ready, info) tuple."""
    url = f"http://localhost:{port}"
    payload = json.dumps({
        "jsonrpc": "2.0",
        "method": "tools/call",
        "params": {"name": "wait_for_ready", "arguments": {}},
        "id": 1
    }).encode()

    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            data = json.loads(resp.read())
            text = data.get("result", {}).get("content", [{}])[0].get("text", "{}")
            info = json.loads(text)
            is_ready = info.get("ready", False) and not info.get("is_compiling", True)
            return True, is_ready, info
    except (urllib.error.URLError, ConnectionRefusedError, OSError):
        return False, False, None
    except Exception as e:
        print(f"  Unexpected error: {e}", file=sys.stderr)
        return False, False, None


def kill_port(port):
    """Find and kill the process holding a port (Windows only). Returns True if killed."""
    if os.name != "nt":
        print("  --kill-port is only supported on Windows", file=sys.stderr)
        return False

    try:
        # Find PID holding the port
        result = subprocess.run(
            ["netstat", "-ano", "-p", "TCP"],
            capture_output=True, text=True, timeout=5
        )
        pid = None
        for line in result.stdout.splitlines():
            if f":{port}" in line and "LISTENING" in line:
                parts = line.split()
                pid = parts[-1]
                break

        if not pid:
            print(f"  No process found listening on port {port}", file=sys.stderr)
            return False

        # Check what process it is
        result = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=5
        )
        proc_name = result.stdout.strip().split(",")[0].strip('"') if result.stdout.strip() else "unknown"
        print(f"  Port {port} held by PID {pid} ({proc_name})", file=sys.stderr)

        # Kill it
        subprocess.run(["taskkill", "/F", "/PID", pid], capture_output=True, timeout=5)
        print(f"  Killed PID {pid}", file=sys.stderr)
        time.sleep(1)  # Give OS time to release the port
        return True

    except Exception as e:
        print(f"  Failed to kill port holder: {e}", file=sys.stderr)
        return False


def main():
    parser = argparse.ArgumentParser(description="Wait for Unity MCP server to be ready")
    parser.add_argument("--port", type=int, default=8080, help="MCP server port (default: 8080)")
    parser.add_argument("--timeout", type=int, default=120, help="Max wait seconds (default: 120)")
    parser.add_argument("--interval", type=float, default=2, help="Poll interval seconds (default: 2)")
    parser.add_argument("--kill-port", action="store_true",
                        help="Kill any process holding the port before waiting (Windows only)")
    args = parser.parse_args()

    if args.kill_port:
        print(f"Checking for orphaned process on port {args.port}...", file=sys.stderr)
        kill_port(args.port)

    start = time.time()
    print(f"Waiting for Unity MCP on port {args.port} (timeout: {args.timeout}s)...", file=sys.stderr)

    while time.time() - start < args.timeout:
        connected, ready, info = check_ready(args.port)

        if not connected:
            print(f"  [{time.time() - start:.0f}s] Not reachable...", file=sys.stderr)
        elif ready:
            elapsed = time.time() - start
            print(f"  [{elapsed:.0f}s] Ready! {info}", file=sys.stderr)
            print("ready")
            sys.exit(0)
        else:
            status = []
            if info.get("is_compiling"):
                status.append("compiling")
            if info.get("is_updating"):
                status.append("updating")
            if info.get("is_playing"):
                status.append("playing")
            print(f"  [{time.time() - start:.0f}s] Connected but {', '.join(status) or 'not ready'}...", file=sys.stderr)

        time.sleep(args.interval)

    print(f"Timeout after {args.timeout}s", file=sys.stderr)
    print("timeout")
    sys.exit(1)


if __name__ == "__main__":
    main()
