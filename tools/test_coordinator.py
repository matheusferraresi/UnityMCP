#!/usr/bin/env python3
"""
Test suite for the sidecar exclusive operation coordinator.
Sends JSON-RPC requests to the sidecar on port 8080 and validates coordination behavior.
"""

import json
import sys
import time
import urllib.request
import urllib.error

SIDECAR_URL = "http://localhost:8080"
_req_counter = 0


def rpc(tool_name, arguments=None, request_id=None):
    """Send a tools/call JSON-RPC request to the sidecar. Returns parsed response."""
    global _req_counter
    if request_id is None:
        _req_counter += 1
        request_id = f"test-{_req_counter}"

    body = json.dumps({
        "jsonrpc": "2.0",
        "method": "tools/call",
        "params": {"name": tool_name, "arguments": arguments or {}},
        "id": request_id,
    }).encode()

    req = urllib.request.Request(
        SIDECAR_URL, data=body,
        headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def get_content(response):
    """Extract the text content dict from a JSON-RPC response."""
    content = response.get("result", {}).get("content", [])
    for item in content:
        if item.get("type") == "text":
            try:
                return json.loads(item["text"])
            except (json.JSONDecodeError, TypeError):
                return {"raw": item["text"]}
    return {}


def status():
    """GET /status from sidecar."""
    req = urllib.request.Request(f"{SIDECAR_URL}/status")
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read())


def passed(name):
    print(f"  PASS  {name}")


def failed(name, reason):
    print(f"  FAIL  {name}: {reason}")
    return False


# ─── Tests ────────────────────────────────────────────────────────────────────

def test_status_no_lock():
    """Status endpoint should show exclusive_op: null when nothing is locked."""
    s = status()
    if s.get("exclusive_op") is not None:
        return failed("status_no_lock", f"Expected null, got {s['exclusive_op']}")
    passed("status_no_lock")
    return True


def test_readonly_passthrough():
    """Read-only tools should always pass through."""
    resp = rpc("scene_get_hierarchy", {}, "test-readonly")
    data = get_content(resp)
    if not data.get("success"):
        return failed("readonly_passthrough", f"Expected success, got {data}")
    passed("readonly_passthrough")
    return True


def test_compile_acquires_lock():
    """compile_and_watch(start) should acquire the exclusive lock."""
    resp = rpc("compile_and_watch", {"action": "start"}, "agent-A")
    data = get_content(resp)
    if not data.get("success") and not data.get("job_id"):
        return failed("compile_acquires_lock", f"Expected success with job_id, got {data}")

    s = status()
    op = s.get("exclusive_op")
    if op is None:
        return failed("compile_acquires_lock", "exclusive_op is null after compile start")
    if op["category"] != "compile":
        return failed("compile_acquires_lock", f"Expected category=compile, got {op['category']}")

    passed("compile_acquires_lock")
    return data.get("job_id", op.get("job_id"))


def test_same_category_coalesce(expected_job_id):
    """Second compile_and_watch(start) should attach to existing job."""
    resp = rpc("compile_and_watch", {"action": "start"}, "agent-B")
    data = get_content(resp)

    if data.get("coordinated_by") != "sidecar":
        return failed("same_category_coalesce", f"Expected sidecar coordination, got {data}")
    if not data.get("success"):
        return failed("same_category_coalesce", f"Expected success=true, got {data}")
    if "Attached" not in data.get("message", ""):
        return failed("same_category_coalesce", f"Expected 'Attached' message, got {data.get('message')}")

    passed("same_category_coalesce")
    return True


def test_cross_category_block():
    """playmode_enter should be blocked while compile lock is held."""
    resp = rpc("playmode_enter", {}, "agent-B-play")
    data = get_content(resp)

    if data.get("coordinated_by") != "sidecar":
        return failed("cross_category_block", f"Expected sidecar coordination, got {data}")
    if data.get("success") is not False:
        return failed("cross_category_block", f"Expected success=false, got {data}")
    if "retry_after_ms" not in data:
        return failed("cross_category_block", f"Expected retry_after_ms, got {data}")

    passed("cross_category_block")
    return True


def test_readonly_during_lock():
    """Read-only tools should still work while exclusive lock is held."""
    resp = rpc("search_tools", {"query": "compile"}, "test-readonly-during-lock")
    data = get_content(resp)
    if not data.get("success") and not data.get("tools"):
        return failed("readonly_during_lock", f"Expected passthrough, got {data}")
    passed("readonly_during_lock")
    return True


def test_poll_releases_lock(job_id):
    """Polling get_job with a completed job should release the lock."""
    # Wait for compilation to finish (Unity can take 30s+ on full recompile)
    for i in range(45):
        resp = rpc("compile_and_watch", {"action": "get_job", "job_id": job_id}, f"poll-{i}")
        data = get_content(resp)
        job_status = data.get("status", "")
        if job_status in ("succeeded", "failed"):
            break
        time.sleep(1)
    else:
        return failed("poll_releases_lock", f"Compilation didn't finish in 45s, last status: {job_status}")

    # Lock should now be released
    s = status()
    if s.get("exclusive_op") is not None:
        return failed("poll_releases_lock", f"Lock still held after job {job_status}: {s['exclusive_op']}")

    passed(f"poll_releases_lock (job {job_status})")
    return True


def test_lock_free_after_release():
    """After lock release, new exclusive ops should work."""
    s = status()
    if s.get("exclusive_op") is not None:
        return failed("lock_free_after_release", f"Lock still held: {s['exclusive_op']}")
    passed("lock_free_after_release")
    return True


def test_sync_exclusive_and_release():
    """scene_load (sync) should acquire and immediately release on response."""
    # Get current scene first
    resp = rpc("scene_get_active", {}, "get-scene")
    data = get_content(resp)
    scene_name = data.get("name") or data.get("sceneName") or ""

    # Load the same scene (safe, won't break anything)
    if scene_name:
        resp = rpc("scene_load", {"scene_name": scene_name}, "sync-exclusive")
        data = get_content(resp)
        # Whether it succeeds or fails, lock should be released
        s = status()
        if s.get("exclusive_op") is not None:
            return failed("sync_exclusive_release", f"Lock stuck after sync op: {s['exclusive_op']}")
        passed("sync_exclusive_release")
    else:
        passed("sync_exclusive_release (skipped, no active scene)")
    return True


# ─── Runner ───────────────────────────────────────────────────────────────────

def main():
    print("\n=== Sidecar Coordinator Test Suite ===\n")

    # Verify sidecar is reachable
    try:
        s = status()
        print(f"  Sidecar: {s['sidecar']}, Unity: {'connected' if s['unity_connected'] else 'DISCONNECTED'}")
        if s.get("exclusive_op"):
            print(f"  WARNING: Stale lock detected: {s['exclusive_op']}")
            print(f"  Waiting for auto-expiry or manual clear...\n")
    except Exception as e:
        print(f"  ERROR: Cannot reach sidecar at {SIDECAR_URL}: {e}")
        print(f"  Launch sidecar first: python tools/gui.py")
        sys.exit(1)

    print()
    results = []

    # Test 1: Clean state
    results.append(test_status_no_lock())

    # Test 2: Read-only passthrough
    results.append(test_readonly_passthrough())

    # Test 3: Compile acquires lock
    job_id = test_compile_acquires_lock()
    results.append(bool(job_id))

    if job_id:
        # Test 4: Same-category coalesce
        results.append(test_same_category_coalesce(job_id))

        # Test 5: Cross-category block
        results.append(test_cross_category_block())

        # Test 6: Read-only during lock
        results.append(test_readonly_during_lock())

        # Test 7: Poll releases lock
        results.append(test_poll_releases_lock(job_id))

    # Test 8: Lock free after release
    results.append(test_lock_free_after_release())

    # Test 9: Sync exclusive acquire + release
    results.append(test_sync_exclusive_and_release())

    # Summary
    total = len(results)
    passed_count = sum(1 for r in results if r)
    print(f"\n=== {passed_count}/{total} tests passed ===\n")
    sys.exit(0 if passed_count == total else 1)


if __name__ == "__main__":
    main()
