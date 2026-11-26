import hashlib
import json
import os
import socket
import stat
import tempfile
import threading
import time
import unittest

import codex_gate

FAKE = """#!/bin/sh
echo "$@" > "$REC/argv"
env | sort > "$REC/env"
case "$1" in
  --version) echo "codex-cli 0.0.0-fake"; exit 0;;
  login) echo "to-stdout"; echo "to-stderr" >&2; exit 3;;
  app-server) echo "to-stdout"; echo "secret-account@example" >&2; touch "$REC/started"; sleep "${SLEEP:-0}"; exit 0;;
  logout) touch "$REC/started"; sleep 30; exit 0;;
esac
"""


class GateTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="gate-test-")
        self.rec = os.path.join(self.dir, "rec"); os.mkdir(self.rec)
        self.home = os.path.join(self.dir, "home"); os.mkdir(self.home, 0o700)
        self.codex = os.path.join(self.dir, "codex")
        self.script = FAKE.replace("$REC", self.rec)
        with open(self.codex, "w") as f:
            f.write(self.script)
        os.chmod(self.codex, 0o700)
        self.pin = os.path.join(self.dir, "codex.sha256")
        with open(self.pin, "w") as f:
            f.write(hashlib.sha256(self.script.encode()).hexdigest())
        self.root = os.path.join(self.dir, "root"); os.makedirs(os.path.join(self.root, "a", "b"))
        self.start()

    def start(self, **kw):
        self.sock_path = os.path.join(self.dir, "gate-%d.sock" % time.monotonic_ns())
        self.gate = codex_gate.Gate(sock_path=self.sock_path, codex=self.codex, home=self.home, pin_file=self.pin, scan_root=self.root, **kw)
        threading.Thread(target=self.gate.serve, daemon=True).start()
        for _ in range(200):                  # the file shows up at bind, the door opens at listen
            try:
                probe = socket.socket(socket.AF_UNIX); probe.connect(self.sock_path); probe.close()
                break
            except OSError:
                time.sleep(0.02)
        time.sleep(0.2)                       # let the gate finish with the probe, it holds a place while it does

    def connect(self, first):
        s = socket.socket(socket.AF_UNIX); s.settimeout(10); s.connect(self.sock_path)
        s.sendall(first if isinstance(first, bytes) else json.dumps(first).encode() + b"\n")
        return s

    def talk(self, first):
        s = self.connect(first); data = b""
        while True:
            chunk = s.recv(4096)
            if not chunk:
                break
            data += chunk
        s.close()
        if b"\x1e" not in data:
            return data, None
        body, _, frame = data.rpartition(b"\x1e")
        return body, json.loads(frame)

    def test_socket_is_not_open_to_everyone(self):
        self.assertEqual(stat.S_IMODE(os.stat(self.sock_path).st_mode), 0o660)

    def test_unknown_commands_are_refused_and_nothing_starts(self):
        for bad in ({"cmd": "exec"}, {"cmd": "shell", "args": ["id"]}, {"cmd": 5}, {"nocmd": 1}, [1], b"not json\n", b"\n"):
            body, frame = self.talk(bad)
            self.assertEqual(json.loads(body), {"error": "unknown_command"}); self.assertIsNone(frame)
        self.assertFalse(os.path.exists(os.path.join(self.rec, "argv")))

    def test_arguments_are_fixed_and_free_ones_are_ignored(self):
        body, frame = self.talk({"cmd": "login-status", "args": ["--evil"], "argv": ["x"]})
        self.assertEqual(open(os.path.join(self.rec, "argv")).read().split(), ["login", "status"])

    def test_each_command_maps_to_its_list(self):
        for cmd, argv in (("version", ["--version"]), ("login", ["login", "--device-auth"]), ("login-status", ["login", "status"]), ("logout", ["logout"])):
            if cmd == "logout":
                continue              # the fake sleeps; covered by the kill test
            self.talk({"cmd": cmd})
            self.assertEqual(open(os.path.join(self.rec, "argv")).read().split(), argv)

    def test_exit_frame_and_merged_stderr_for_login_commands(self):
        body, frame = self.talk({"cmd": "login"})
        self.assertEqual(frame, {"exit": 3})
        self.assertIn(b"to-stdout", body); self.assertIn(b"to-stderr", body)     # merged

    def test_stderr_is_dropped_for_app_server(self):
        body, frame = self.talk({"cmd": "app-server"})
        self.assertEqual(frame, {"exit": 0}); self.assertIn(b"to-stdout", body); self.assertNotIn(b"secret-account", body)

    def test_child_gets_only_a_small_environment(self):
        os.environ["JULOS_SECRET_TEST"] = "x"
        try:
            self.talk({"cmd": "version"})
        finally:
            del os.environ["JULOS_SECRET_TEST"]
        names = {line.split("=")[0] for line in open(os.path.join(self.rec, "env")).read().split("\n") if "=" in line}
        self.assertTrue(names <= {"PATH", "HOME", "CODEX_HOME", "NO_COLOR", "LANG", "PWD", "SHLVL", "_", "OLDPWD"}, names)   # PWD, SHLVL and _ are the shell's own
        self.assertNotIn("JULOS_SECRET_TEST", names)

    def test_closing_the_connection_kills_the_program(self):
        s = self.connect({"cmd": "logout"})
        for _ in range(100):
            if os.path.exists(os.path.join(self.rec, "started")):
                break
            time.sleep(0.05)
        s.close()
        started = time.monotonic()
        # the child sleeps 30 s; the gate must not keep it: its slot comes back soon
        for _ in range(100):
            if self.gate.slots._value == codex_gate.MAX_CONNECTIONS:
                break
            time.sleep(0.05)
        self.assertEqual(self.gate.slots._value, codex_gate.MAX_CONNECTIONS)
        self.assertLess(time.monotonic() - started, 5)

    def test_at_most_n_connections(self):
        self.start(max_connections=2)
        held = [self.connect({"cmd": "logout"}) for _ in range(2)]
        for _ in range(100):
            if self.gate.slots._value == 0:
                break
            time.sleep(0.02)
        body, frame = self.talk({"cmd": "version"})
        self.assertEqual(body, b"{\"error\": \"busy\"}\n")
        for s in held:
            s.close()

    def test_silent_first_line_times_out(self):
        codex_gate.FIRST_LINE_SECONDS = 0.3
        try:
            s = socket.socket(socket.AF_UNIX); s.settimeout(5); s.connect(self.sock_path)
            self.assertEqual(json.loads(s.recv(100)), {"error": "unknown_command"})
        finally:
            codex_gate.FIRST_LINE_SECONDS = 5

    # ---------------------------------------------------------------- self-test

    def selftest(self, word="0123456789abcdef", canary="/nonexistent/canary"):
        body, frame = self.talk({"cmd": "selftest", "canary": canary, "word": word})
        self.assertEqual(frame, {"exit": 0})
        return json.loads(body)

    def test_selftest_is_green_on_a_clean_tree(self):
        r = self.selftest()
        expected_root = os.geteuid() == 0
        self.assertEqual(r["checks"]["no_julos_data"], True)
        self.assertEqual(r["checks"]["home_private"], True)
        self.assertEqual(r["checks"]["codex_pinned"], True)
        self.assertEqual(r["checks"]["uid_not_root"], not expected_root)

    def test_selftest_finds_a_planted_word_within_depth_4_and_goes_red(self):
        with open(os.path.join(self.root, "a", "b", "copy.txt"), "w") as f:
            f.write("xx 0123456789abcdef yy")
        r = self.selftest()
        self.assertFalse(r["checks"]["no_julos_data"]); self.assertFalse(r["ok"])

    def test_selftest_finds_the_word_across_a_read_boundary(self):
        with open(os.path.join(self.root, "big"), "wb") as f:
            f.write(b"." * ((1 << 20) - 5) + b"0123456789abcdef")
        self.assertFalse(self.selftest()["checks"]["no_julos_data"])

    def test_selftest_does_not_look_deeper_than_4(self):
        deep = os.path.join(self.root, "1", "2", "3"); os.makedirs(deep)
        with open(os.path.join(deep, "ok.txt"), "w") as f:
            f.write("0123456789abcdef")             # file at depth 4: seen
        self.assertFalse(self.selftest()["checks"]["no_julos_data"])
        os.remove(os.path.join(deep, "ok.txt"))
        os.makedirs(os.path.join(deep, "4"))
        with open(os.path.join(deep, "4", "too-deep.txt"), "w") as f:
            f.write("0123456789abcdef")             # depth 5: not looked at
        self.assertTrue(self.selftest()["checks"]["no_julos_data"])

    def test_selftest_goes_red_when_the_canary_path_exists(self):
        canary = os.path.join(self.dir, "canary")
        open(canary, "w").write("whatever")
        self.assertFalse(self.selftest(canary=canary)["checks"]["no_julos_data"])

    def test_selftest_goes_red_for_a_wrong_pin_a_wrong_mode_and_a_timeout(self):
        open(self.pin, "w").write("0" * 64)
        self.assertFalse(self.selftest()["checks"]["codex_pinned"])
        os.chmod(self.home, 0o755)
        self.assertFalse(self.selftest()["checks"]["home_private"])
        self.start(scan_seconds=-1)
        with open(os.path.join(self.root, "a", "x"), "w") as f:
            f.write("nothing")
        self.assertFalse(self.selftest()["checks"]["no_julos_data"])      # not finished in time = not proven

    def test_selftest_arguments_are_validated(self):
        for bad in ({"cmd": "selftest"}, {"cmd": "selftest", "canary": "/x", "word": "short"}, {"cmd": "selftest", "canary": "/x", "word": "ZZZZZZZZZZZZZZZZ"}, {"cmd": "selftest", "canary": 5, "word": "0123456789abcdef"}):
            body, _ = self.talk(bad)
            self.assertEqual(json.loads(body), {"error": "unknown_command"})


if __name__ == "__main__":
    unittest.main()
