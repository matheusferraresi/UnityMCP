#!/usr/bin/env python3
"""Wait for Unity MCP server to be ready. Polls until connected and not compiling."""

import argparse
import json
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


def main():
    parser = argparse.ArgumentParser(description="Wait for Unity MCP server to be ready")
    parser.add_argument("--port", type=int, default=8080, help="MCP server port (default: 8080)")
    parser.add_argument("--timeout", type=int, default=120, help="Max wait seconds (default: 120)")
    parser.add_argument("--interval", type=float, default=2, help="Poll interval seconds (default: 2)")
    args = parser.parse_args()

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
