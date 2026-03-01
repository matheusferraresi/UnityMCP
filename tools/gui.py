#!/usr/bin/env python3
"""
UnixxtyMCP Sidecar GUI — Visual dashboard for the sidecar proxy.

Replaces tools/dev.py with a tkinter window that:
  - Manages the sidecar subprocess (start/stop/restart)
  - Shows live status of sidecar + Unity instances
  - Streams sidecar logs in a scrollable panel
  - Watches files for changes (auto-restart sidecar, trigger Unity recompile)

Usage:
  python tools/gui.py
"""

import json
import os
import queue
import subprocess
import sys
import threading
import time
import tkinter as tk
from tkinter import ttk, scrolledtext
import urllib.request
import urllib.error

# ─── Paths ───────────────────────────────────────────────────────────────────

TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(TOOLS_DIR)
PACKAGE_DIR = os.path.join(PROJECT_ROOT, "Package")
SIDECAR_SCRIPT = os.path.join(TOOLS_DIR, "sidecar.py")
SETTINGS_FILE = os.path.join(TOOLS_DIR, ".gui_settings.json")

# ─── Timing ──────────────────────────────────────────────────────────────────

STATUS_POLL_MS = 2000
FILE_POLL_MS = 1500
LOG_DRAIN_MS = 100
UPTIME_MS = 1000
DEBOUNCE_SECONDS = 2.0
MAX_LOG_LINES = 2000


# ─── File Watching (same logic as dev.py) ────────────────────────────────────

def collect_files(directory, extension):
    files = {}
    if not os.path.isdir(directory):
        return files
    for root, dirs, filenames in os.walk(directory):
        dirs[:] = [d for d in dirs if not d.startswith(".") and d != "__pycache__"]
        for f in filenames:
            if f.endswith(extension):
                path = os.path.join(root, f)
                try:
                    files[path] = os.stat(path).st_mtime
                except OSError:
                    pass
    return files


def find_changes(old, new):
    changes = []
    for path, mtime in new.items():
        if path not in old:
            changes.append((path, "added"))
        elif mtime > old[path]:
            changes.append((path, "modified"))
    for path in old:
        if path not in new:
            changes.append((path, "deleted"))
    return changes


def relative_path(path):
    try:
        return os.path.relpath(path, PROJECT_ROOT)
    except ValueError:
        return path


# ─── Main Application ────────────────────────────────────────────────────────

class SidecarGUI(tk.Tk):

    def __init__(self):
        super().__init__()
        self.title("UnixxtyMCP Sidecar")
        self.minsize(640, 420)

        # State
        self.sidecar_proc = None
        self.sidecar_port = 8080
        self.unity_port = 8081
        self.start_time = None
        self.log_queue = queue.Queue()

        # File watching
        self.py_snap = {}
        self.cs_snap = {}
        self.last_change = 0

        # Instance tracking from /status
        self.instances = []

        # Build UI
        self._build_status_frame()
        self._build_controls()
        self._build_log_panel()

        # Load settings
        self._load_settings()

        # Window close
        self.protocol("WM_DELETE_WINDOW", self._on_close)

        # Auto-start sidecar
        self.after(200, self._start)

        # Schedule periodic tasks
        self.after(STATUS_POLL_MS, self._poll_status)
        self.after(FILE_POLL_MS, self._poll_files)
        self.after(LOG_DRAIN_MS, self._drain_logs)
        self.after(UPTIME_MS, self._tick_uptime)

    # ─── UI Construction ─────────────────────────────────────────────────

    def _build_status_frame(self):
        frame = ttk.LabelFrame(self, text="Status", padding=8)
        frame.pack(fill=tk.X, padx=8, pady=(8, 4))

        # Sidecar row
        self.sidecar_dot = tk.Label(frame, text="\u2b24", fg="gray", font=("", 9))
        self.sidecar_dot.grid(row=0, column=0, padx=(0, 4))
        self.sidecar_lbl = ttk.Label(frame, text="Sidecar: Stopped")
        self.sidecar_lbl.grid(row=0, column=1, sticky=tk.W)
        self.port_lbl = ttk.Label(frame, text="Port: 8080")
        self.port_lbl.grid(row=0, column=2, padx=(16, 0), sticky=tk.W)

        # Unity instances container (dynamic rows added by _apply_status)
        self.instance_frame = ttk.Frame(frame)
        self.instance_frame.grid(row=1, column=0, columnspan=3, sticky=tk.W, pady=(2, 0))
        self.instance_widgets = []  # list of (dot_label, text_label)

        # Uptime + auto-focus row
        self.uptime_lbl = ttk.Label(frame, text="Uptime: --:--:--")
        self.uptime_lbl.grid(row=2, column=0, columnspan=2, sticky=tk.W, pady=(4, 0))

        self.auto_focus_var = tk.BooleanVar(value=False)
        self.auto_focus_chk = ttk.Checkbutton(
            frame, text="Auto-focus", variable=self.auto_focus_var,
            command=self._on_auto_focus
        )
        self.auto_focus_chk.grid(row=2, column=2, padx=(16, 0), sticky=tk.W, pady=(4, 0))

        frame.columnconfigure(1, weight=1)
        frame.columnconfigure(2, weight=1)

    def _build_controls(self):
        frame = ttk.Frame(self, padding=(8, 4))
        frame.pack(fill=tk.X)

        self.start_btn = ttk.Button(frame, text="Start", command=self._start)
        self.start_btn.pack(side=tk.LEFT, padx=(0, 4))

        self.stop_btn = ttk.Button(frame, text="Stop", command=self._stop, state=tk.DISABLED)
        self.stop_btn.pack(side=tk.LEFT, padx=(0, 4))

        self.restart_btn = ttk.Button(frame, text="Restart", command=self._restart, state=tk.DISABLED)
        self.restart_btn.pack(side=tk.LEFT)

        self.clear_btn = ttk.Button(frame, text="Clear Logs", command=self._clear_logs)
        self.clear_btn.pack(side=tk.RIGHT)

    def _build_log_panel(self):
        frame = ttk.LabelFrame(self, text="Log", padding=4)
        frame.pack(fill=tk.BOTH, expand=True, padx=8, pady=(4, 8))

        self.log_text = scrolledtext.ScrolledText(
            frame, wrap=tk.WORD, state=tk.DISABLED,
            font=("Consolas", 9), bg="#1e1e1e", fg="#cccccc",
            insertbackground="#cccccc", relief=tk.FLAT, borderwidth=0
        )
        self.log_text.pack(fill=tk.BOTH, expand=True)

        # Color tags
        self.log_text.tag_configure("INFO", foreground="#4ec9b0")
        self.log_text.tag_configure("WARNING", foreground="#dcdcaa")
        self.log_text.tag_configure("ERROR", foreground="#f44747")
        self.log_text.tag_configure("DEBUG", foreground="#808080")
        self.log_text.tag_configure("DEV", foreground="#569cd6")

    # ─── Sidecar Process ─────────────────────────────────────────────────

    def _is_alive(self):
        return self.sidecar_proc is not None and self.sidecar_proc.poll() is None

    def _port_in_use(self):
        """Check if the sidecar port is already in use by another process."""
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            sock.bind(("127.0.0.1", self.sidecar_port))
            sock.close()
            return False
        except OSError:
            sock.close()
            return True

    def _start(self):
        if self._is_alive():
            return

        # Guard: refuse to spawn if another sidecar is already on this port
        if self._port_in_use():
            self._log(
                f"[gui] Port {self.sidecar_port} already in use — another sidecar is running. Kill it first.",
                "ERROR"
            )
            return

        cmd = [sys.executable, "-u", SIDECAR_SCRIPT,
               "--port", str(self.sidecar_port),
               "--unity-port", str(self.unity_port)]
        try:
            self.sidecar_proc = subprocess.Popen(
                cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                bufsize=1, text=True, encoding="utf-8", errors="replace"
            )
        except Exception as e:
            self._log(f"[gui] Failed to start sidecar: {e}", "ERROR")
            return

        self.start_time = time.time()
        self._log(f"[gui] Sidecar started (PID {self.sidecar_proc.pid})", "DEV")
        self._update_buttons()
        self._set_sidecar_status(True)

        # Snapshot files
        self.py_snap = collect_files(TOOLS_DIR, ".py")
        self.cs_snap = collect_files(PACKAGE_DIR, ".cs")

        # Stderr reader thread
        threading.Thread(target=self._read_stderr, daemon=True).start()

    def _stop(self):
        if not self._is_alive():
            return
        self._log("[gui] Stopping sidecar...", "DEV")
        self.sidecar_proc.terminate()
        try:
            self.sidecar_proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.sidecar_proc.kill()
            self.sidecar_proc.wait()
        self.sidecar_proc = None
        self.start_time = None
        self._log("[gui] Sidecar stopped", "DEV")
        self._update_buttons()
        self._set_sidecar_status(False)
        self._update_instances([])

    def _restart(self):
        self._stop()
        self.after(300, self._start)

    def _read_stderr(self):
        proc = self.sidecar_proc
        if not proc:
            return
        try:
            for line in proc.stderr:
                self.log_queue.put(line.rstrip("\n"))
        except (ValueError, OSError):
            pass

    # ─── Periodic Tasks ──────────────────────────────────────────────────

    def _drain_logs(self):
        count = 0
        while not self.log_queue.empty() and count < 50:
            line = self.log_queue.get_nowait()
            tag = self._classify(line)
            self._append_log(line, tag)
            count += 1
        self._trim_log()
        self.after(LOG_DRAIN_MS, self._drain_logs)

    def _poll_status(self):
        if self._is_alive():
            threading.Thread(target=self._fetch_status, daemon=True).start()
        else:
            # Detect unexpected exit
            if self.sidecar_proc is not None and self.sidecar_proc.poll() is not None:
                code = self.sidecar_proc.returncode
                self._log(f"[gui] Sidecar exited unexpectedly (code {code})", "ERROR")
                self.sidecar_proc = None
                self.start_time = None
                self._set_sidecar_status(False)
                self._update_buttons()
                self._update_instances([])
        self.after(STATUS_POLL_MS, self._poll_status)

    def _fetch_status(self):
        try:
            req = urllib.request.Request(f"http://localhost:{self.sidecar_port}/status")
            with urllib.request.urlopen(req, timeout=2) as resp:
                data = json.loads(resp.read())
            self.after(0, self._apply_status, data)
        except Exception:
            self.after(0, self._apply_status, None)

    def _apply_status(self, data):
        if data:
            self._set_sidecar_status(True)
            self.auto_focus_var.set(data.get("auto_focus", False))
            instances = data.get("instances", [])
            self._update_instances(instances)
        else:
            if not self._is_alive():
                self._set_sidecar_status(False)
                self._update_instances([])

    def _tick_uptime(self):
        if self.start_time and self._is_alive():
            elapsed = int(time.time() - self.start_time)
            h, m, s = elapsed // 3600, (elapsed % 3600) // 60, elapsed % 60
            self.uptime_lbl.config(text=f"Uptime: {h:02d}:{m:02d}:{s:02d}")
        else:
            self.uptime_lbl.config(text="Uptime: --:--:--")
        self.after(UPTIME_MS, self._tick_uptime)

    def _poll_files(self):
        if not self._is_alive():
            self.after(FILE_POLL_MS, self._poll_files)
            return

        now = time.time()
        if now - self.last_change < DEBOUNCE_SECONDS:
            self.after(FILE_POLL_MS, self._poll_files)
            return

        # Python files → restart sidecar
        new_py = collect_files(TOOLS_DIR, ".py")
        py_changes = find_changes(self.py_snap, new_py)
        if py_changes:
            self.last_change = now
            for path, kind in py_changes:
                self._log(f"[dev] {kind}: {relative_path(path)}", "DEV")
            self._log("[dev] Restarting sidecar...", "DEV")
            self._restart()
            self.after(FILE_POLL_MS, self._poll_files)
            return
        self.py_snap = new_py

        # C# files → trigger Unity recompile
        new_cs = collect_files(PACKAGE_DIR, ".cs")
        cs_changes = find_changes(self.cs_snap, new_cs)
        if cs_changes:
            self.last_change = now
            for path, kind in cs_changes:
                self._log(f"[dev] {kind}: {relative_path(path)}", "DEV")
            threading.Thread(target=self._trigger_recompile, daemon=True).start()
            self.cs_snap = collect_files(PACKAGE_DIR, ".cs")
        else:
            self.cs_snap = new_cs

        self.after(FILE_POLL_MS, self._poll_files)

    def _trigger_recompile(self):
        body = json.dumps({
            "jsonrpc": "2.0",
            "method": "tools/call",
            "params": {"name": "recompile_scripts", "arguments": {}},
            "id": "_dev_recompile"
        }).encode()
        try:
            req = urllib.request.Request(
                f"http://localhost:{self.unity_port}",
                data=body, headers={"Content-Type": "application/json"}
            )
            with urllib.request.urlopen(req, timeout=5) as resp:
                result = json.loads(resp.read())
                content = result.get("result", {}).get("content", [{}])
                text = content[0].get("text", "") if content else ""
                if "error" in text.lower():
                    self.log_queue.put(f"[dev] Unity recompile error: {text[:200]}")
                else:
                    self.log_queue.put("[dev] Unity recompile triggered")
        except urllib.error.URLError:
            self.log_queue.put("[dev] Unity not reachable — skipping recompile")
        except Exception as e:
            self.log_queue.put(f"[dev] Recompile failed: {e}")

    # ─── UI Helpers ──────────────────────────────────────────────────────

    def _set_sidecar_status(self, running):
        color = "#4ec9b0" if running else "#f44747"
        text = "Running" if running else "Stopped"
        self.sidecar_dot.config(fg=color)
        self.sidecar_lbl.config(text=f"Sidecar: {text}")
        self.port_lbl.config(text=f"Port: {self.sidecar_port}")

    def _update_instances(self, instances):
        """Rebuild Unity instance rows dynamically."""
        self.instances = instances

        # Clear existing widgets
        for dot, lbl in self.instance_widgets:
            dot.destroy()
            lbl.destroy()
        self.instance_widgets.clear()

        if not instances:
            # Show a single "Unity: Unknown" row
            dot = tk.Label(self.instance_frame, text="\u2b24", fg="gray", font=("", 9))
            dot.grid(row=0, column=0, padx=(0, 4))
            lbl = ttk.Label(self.instance_frame, text="Unity: No instances detected")
            lbl.grid(row=0, column=1, sticky=tk.W)
            self.instance_widgets.append((dot, lbl))
            return

        for i, inst in enumerate(instances):
            connected = inst.get("connected", False)
            label = inst.get("label", f"Port {inst.get('port', '?')}")
            port = inst.get("port", "?")

            color = "#4ec9b0" if connected else "#dcdcaa"
            status = "Connected" if connected else "Disconnected"

            dot = tk.Label(self.instance_frame, text="\u2b24", fg=color, font=("", 9))
            dot.grid(row=i, column=0, padx=(0, 4))
            lbl = ttk.Label(self.instance_frame, text=f"Unity ({label}): {status}  Port: {port}")
            lbl.grid(row=i, column=1, sticky=tk.W)
            self.instance_widgets.append((dot, lbl))

    def _update_buttons(self):
        alive = self._is_alive()
        self.start_btn.config(state=tk.DISABLED if alive else tk.NORMAL)
        self.stop_btn.config(state=tk.NORMAL if alive else tk.DISABLED)
        self.restart_btn.config(state=tk.NORMAL if alive else tk.DISABLED)

    def _log(self, text, tag="INFO"):
        self._append_log(text, tag)

    def _append_log(self, text, tag):
        self.log_text.config(state=tk.NORMAL)
        self.log_text.insert(tk.END, text + "\n", tag)
        self.log_text.see(tk.END)
        self.log_text.config(state=tk.DISABLED)

    def _trim_log(self):
        self.log_text.config(state=tk.NORMAL)
        line_count = int(self.log_text.index("end-1c").split(".")[0])
        if line_count > MAX_LOG_LINES:
            self.log_text.delete("1.0", f"{line_count - MAX_LOG_LINES}.0")
        self.log_text.config(state=tk.DISABLED)

    def _classify(self, line):
        if "[dev]" in line or "[gui]" in line:
            return "DEV"
        if "[ERROR]" in line or "[CRITICAL]" in line:
            return "ERROR"
        if "[WARNING]" in line:
            return "WARNING"
        if "[DEBUG]" in line:
            return "DEBUG"
        return "INFO"

    def _clear_logs(self):
        self.log_text.config(state=tk.NORMAL)
        self.log_text.delete("1.0", tk.END)
        self.log_text.config(state=tk.DISABLED)

    def _on_auto_focus(self):
        enabled = self.auto_focus_var.get()
        def _send():
            body = json.dumps({
                "jsonrpc": "2.0",
                "method": "tools/call",
                "params": {
                    "name": "manage_editor",
                    "arguments": {"action": "set_auto_focus", "enabled": enabled}
                },
                "id": "_gui_af"
            }).encode()
            try:
                req = urllib.request.Request(
                    f"http://localhost:{self.sidecar_port}",
                    data=body, headers={"Content-Type": "application/json"}
                )
                urllib.request.urlopen(req, timeout=3)
            except Exception:
                pass
        threading.Thread(target=_send, daemon=True).start()

    # ─── Settings Persistence ────────────────────────────────────────────

    def _load_settings(self):
        try:
            with open(SETTINGS_FILE, "r") as f:
                s = json.load(f)
            geo = s.get("geometry")
            if geo:
                self.geometry(geo)
            else:
                self.geometry("720x520")
        except (FileNotFoundError, json.JSONDecodeError):
            self.geometry("720x520")

    def _save_settings(self):
        try:
            with open(SETTINGS_FILE, "w") as f:
                json.dump({"geometry": self.geometry()}, f)
        except Exception:
            pass

    # ─── Shutdown ────────────────────────────────────────────────────────

    def _on_close(self):
        self._save_settings()
        if self._is_alive():
            self.sidecar_proc.terminate()
            try:
                self.sidecar_proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.sidecar_proc.kill()
        self.destroy()


# ─── Entry Point ─────────────────────────────────────────────────────────────

def main():
    app = SidecarGUI()
    app.mainloop()


if __name__ == "__main__":
    main()
