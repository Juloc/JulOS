#!/usr/bin/env python3
"""codex-gate: the only door into the Codex container (docs/CODEX-SIDECAR.md).

Listens on one Unix socket. A client sends one JSON line {"cmd": ...}; the command name maps to a FIXED argument list of `codex`, no free arguments
ever reach the program. After the first line the connection is raw bytes both ways. When the program ends, the gate sends one last line
"\\x1e{"exit":N}\\n" (record separator first, so it cannot be mistaken for a JSON line of app-server) and closes. If the client closes the
connection, the program is killed. The command "selftest" runs no Codex: it checks the separation from inside this container.
Python 3 standard library only. Logs: command, duration, exit code, never content.
"""
import hashlib
import json
import os
import re
import signal
import socket
import subprocess
import sys
import threading
import time

COMMANDS = {
    "app-server": ["app-server"],
    "login": ["login", "--device-auth"],
    "login-status": ["login", "status"],
    "logout": ["logout"],
    "version": ["--version"],
}
WORD = re.compile(r"[0-9a-f]{16}")
MAX_CONNECTIONS = 8
FIRST_LINE_SECONDS = 5
SCAN_LIMIT_SECONDS = 10
SCAN_DEPTH = 4
SCAN_BYTES_PER_FILE = 8 << 20
SCAN_SKIP = ("/proc", "/sys")


class Gate:
    def __init__(self, sock_path="/run/codex/codex.sock", codex="codex", home="/home/codex", pin_file="/usr/local/share/codex.sha256",
                 scan_root="/", scan_seconds=SCAN_LIMIT_SECONDS, max_connections=MAX_CONNECTIONS, group=None):
        self.sock_path, self.codex, self.home, self.pin_file = sock_path, codex, home, pin_file
        self.scan_root, self.scan_seconds, self.group = scan_root, scan_seconds, group
        self.slots = threading.BoundedSemaphore(max_connections)

    # ------------------------------------------------------------------ server

    def serve(self):
        os.makedirs(os.path.join(self.home, "work"), mode=0o700, exist_ok=True)
        os.chmod(self.home, 0o700)            # the volume may start with other rights; the self-test then checks owner and mode
        os.chmod(os.path.join(self.home, "work"), 0o700)
        if os.path.exists(self.sock_path):
            os.unlink(self.sock_path)
        server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        server.bind(self.sock_path)
        os.chmod(self.sock_path, 0o660)       # the group (gid shared with JulOS) opens it, nobody else
        server.listen(16)
        while True:
            conn, _ = server.accept()
            threading.Thread(target=self.handle, args=(conn,), daemon=True).start()

    def handle(self, conn):
        if not self.slots.acquire(blocking=False):
            self.first_line(conn)                 # read what the client sent first, or closing would reset the connection before it sees the answer
            self.say(conn, {"error": "busy"})
            conn.close()
            return
        started, name, code = time.monotonic(), "?", None
        try:
            request = self.first_line(conn)
            name = request.get("cmd") if isinstance(request, dict) else None
            if name == "selftest" and self.valid_selftest(request):
                self.say(conn, self.selftest(request["canary"], request["word"]))
                code = 0
            elif isinstance(name, str) and name in COMMANDS:
                code = self.run(conn, COMMANDS[name])
            else:
                name = "unknown"
                self.say(conn, {"error": "unknown_command"})
                return
            conn.sendall(b"\x1e" + json.dumps({"exit": code}).encode() + b"\n")
        except OSError:
            pass
        finally:
            sys.stderr.write("gate: cmd=%s ms=%d exit=%s\n" % (name, (time.monotonic() - started) * 1000, code))
            sys.stderr.flush()
            try:
                conn.shutdown(socket.SHUT_RDWR)       # close() alone would not end the connection while the reading thread still sits in recv
            except OSError:
                pass
            conn.close()
            self.slots.release()

    @staticmethod
    def say(conn, obj):
        try:
            conn.sendall(json.dumps(obj).encode() + b"\n")
        except OSError:
            pass

    @staticmethod
    def first_line(conn):
        conn.settimeout(FIRST_LINE_SECONDS)
        data = b""
        try:
            while not data.endswith(b"\n") and len(data) < 4096:
                chunk = conn.recv(1)           # byte by byte: nothing after the newline may be swallowed
                if not chunk:
                    break
                data += chunk
            return json.loads(data)
        except (socket.timeout, ValueError):
            return None
        finally:
            conn.settimeout(None)

    @staticmethod
    def valid_selftest(r):
        return isinstance(r.get("canary"), str) and 0 < len(r["canary"]) < 1024 and isinstance(r.get("word"), str) and WORD.fullmatch(r["word"]) is not None

    # ------------------------------------------------------------------ the program

    def run(self, conn, args):
        env = {"PATH": os.environ.get("PATH", "/usr/local/bin:/usr/bin:/bin"), "HOME": self.home, "CODEX_HOME": self.home, "NO_COLOR": "1", "LANG": "C.UTF-8"}
        app_server = args == ["app-server"]
        child = subprocess.Popen([self.codex] + args, stdin=subprocess.PIPE if app_server else subprocess.DEVNULL, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE if app_server else subprocess.STDOUT, env=env, cwd=os.path.join(self.home, "work"),
                                 start_new_session=True, close_fds=True)
        killed = threading.Event()

        def kill():
            killed.set()
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except OSError:
                pass

        def from_client():                     # bytes to the program; the client closing the connection ends the program
            try:
                while True:
                    data = conn.recv(65536)
                    if not data:
                        break
                    if app_server:
                        child.stdin.write(data)
                        child.stdin.flush()
            except (OSError, ValueError):
                pass
            kill()

        def drop_errors():                     # stderr of app-server can name the account: read and thrown away
            while child.stderr.read(4096):
                pass

        threading.Thread(target=from_client, daemon=True).start()
        if app_server:
            threading.Thread(target=drop_errors, daemon=True).start()
        try:
            while True:
                data = os.read(child.stdout.fileno(), 65536)
                if not data:
                    break
                conn.sendall(data)
        except OSError:
            kill()
        code = child.wait()
        kill()                                 # anything left of the process group
        for pipe in (child.stdin, child.stdout, child.stderr):
            if pipe is not None:
                try:
                    pipe.close()
                except OSError:
                    pass
        return code

    # ------------------------------------------------------------------ self-test

    def selftest(self, canary, word):
        checks = {
            "uid_not_root": os.geteuid() != 0,
            "no_julos_data": self.no_julos_data(canary, word),
            "no_docker_socket": not any(os.path.exists(p) for p in ("/var/run/docker.sock", "/run/docker.sock")),
            "home_private": self.home_private(),
            "codex_pinned": self.codex_pinned(),
        }
        return {"ok": all(checks.values()), "checks": checks}

    def no_julos_data(self, canary, word):
        if os.path.lexists(canary) or os.path.lexists("/data/secret.key"):
            return False
        needle = word.encode()
        deadline = time.monotonic() + self.scan_seconds
        for top, dirs, files in os.walk(self.scan_root, followlinks=False):
            rel = os.path.relpath(top, self.scan_root)
            depth = 0 if rel == "." else rel.count("/") + 1        # folders between the root and here; a file in here sits at depth + 1
            dirs[:] = [d for d in dirs if depth + 2 <= SCAN_DEPTH and os.path.join(top, d) not in SCAN_SKIP]
            for name in files:
                if time.monotonic() > deadline:
                    return False               # not finished in time: not proven, so not green
                path = os.path.join(top, name)
                if os.path.islink(path) or not os.path.isfile(path):
                    continue
                try:
                    if self.contains(path, needle):
                        return False
                except OSError:
                    continue
        return True

    @staticmethod
    def contains(path, needle):
        seen, tail = 0, b""
        with open(path, "rb") as f:
            while seen < SCAN_BYTES_PER_FILE:
                chunk = f.read(1 << 20)
                if not chunk:
                    return False
                seen += len(chunk)
                if needle in tail + chunk:
                    return True
                tail = chunk[-len(needle):]
        return False

    def home_private(self):
        try:
            info = os.stat(self.home)
        except OSError:
            return False
        return info.st_uid == os.geteuid() and (info.st_mode & 0o777) == 0o700

    def codex_pinned(self):
        try:
            path = self.codex if os.path.isabs(self.codex) else next(os.path.join(d, self.codex) for d in os.environ.get("PATH", "").split(":") if os.path.isfile(os.path.join(d, self.codex)))
            with open(self.pin_file) as f:
                pinned = f.read().strip().lower()
            digest = hashlib.sha256()
            with open(path, "rb") as f:
                for block in iter(lambda: f.read(1 << 20), b""):
                    digest.update(block)
            actual = digest.hexdigest()
            return len(pinned) == 64 and actual == pinned
        except (OSError, StopIteration):
            return False


if __name__ == "__main__":
    Gate().serve()
