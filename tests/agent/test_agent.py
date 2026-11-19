"""Tests fuer den Agenten (tools/julos-agent.py): Aktualisierung (Pruefsumme, atomarer Ersatz, nur eigene Herkunft) und Betrieb (Log, uninstall).

Ohne Anzeige und ohne PyGObject lauffaehig:  python3 -m unittest discover -s tests/agent
Der Agent wird als Modul geladen (nicht als Programm), dann laeuft nichts los und GTK wird nicht gebraucht.
"""
import contextlib
import hashlib
import http.server
import io
import importlib.util
import logging
import os
import stat
import tempfile
import threading
import unittest
from unittest import mock

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
AGENT_FILE = os.path.join(ROOT, "tools", "julos-agent.py")


def load_agent():
    spec = importlib.util.spec_from_file_location("julos_agent_under_test", AGENT_FILE)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


agent = load_agent()


def script_text(version, extra=""):
    return ('#!/usr/bin/env python3\nAGENT_VERSION = "%s"\n%sprint("neu")\n' % (version, extra)).encode()


class Origin:
    """Ein kleiner Server, der die 'neue Datei' unter dem festen Pfad ausliefert und mitzaehlt, was gefragt wurde."""

    def __init__(self, body, redirect_to=None):
        outer = self
        self.requests = []
        self.body = body
        self.redirect_to = redirect_to

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                outer.requests.append(self.path)
                if outer.redirect_to:
                    self.send_response(302)
                    self.send_header("Location", outer.redirect_to)
                    self.end_headers()
                    return
                self.send_response(200)
                self.send_header("Content-Length", str(len(outer.body)))
                self.end_headers()
                self.wfile.write(outer.body)

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = "http://127.0.0.1:%d" % self.httpd.server_address[1]
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.script = os.path.join(self.dir.name, "julos-agent.py")
        self.old = script_text("0.3.0")
        with open(self.script, "wb") as f:
            f.write(self.old)
        os.chmod(self.script, 0o750)

    def serve(self, body, **kw):
        o = Origin(body, **kw)
        self.addCleanup(o.close)
        return o

    def message(self, body, version="0.9.0", **over):
        m = {"path": agent.UPDATE_PATH, "sha256": hashlib.sha256(body).hexdigest(), "version": version}
        m.update(over)
        return m

    def current(self):
        with open(self.script, "rb") as f:
            return f.read()

    def leftovers(self):
        return [n for n in os.listdir(self.dir.name) if n != "julos-agent.py"]

    def update(self, origin, message, current="0.3.0"):
        return agent.apply_update(origin.url, message, script=self.script, allow_http=True, current=current)

    def test_good_file_replaces_the_agent_atomically_and_keeps_its_mode(self):
        new = script_text("0.9.0")
        o = self.serve(new)
        self.assertEqual(self.update(o, self.message(new)), ("0.9.0", True))
        self.assertEqual(self.current(), new)
        self.assertEqual(stat.S_IMODE(os.stat(self.script).st_mode), 0o750)
        self.assertEqual(self.leftovers(), [])
        self.assertEqual(o.requests, [agent.UPDATE_PATH])

    def test_replacement_goes_through_os_replace_from_a_temp_file_in_the_same_folder(self):
        new = script_text("0.9.0")
        o = self.serve(new)
        calls = []
        real = os.replace

        def spy(src, dst):
            calls.append((src, dst, self.current()))       # zum Zeitpunkt des Ersetzens ist die alte Datei noch ganz
            return real(src, dst)
        with mock.patch.object(agent.os, "replace", spy):
            self.update(o, self.message(new))
        (src, dst, before), = calls
        self.assertEqual(dst, self.script)
        self.assertEqual(os.path.dirname(src), self.dir.name)
        self.assertEqual(before, self.old)

    def test_wrong_checksum_is_refused_and_nothing_changes(self):
        new = script_text("0.9.0")
        o = self.serve(new)
        bad = self.message(new, sha256=hashlib.sha256(b"etwas anderes").hexdigest())
        with self.assertRaises(agent.UpdateError) as e:
            self.update(o, bad)
        self.assertEqual(e.exception.status, 422)
        self.assertIn("Pruefsumme", str(e.exception))
        self.assertEqual(self.current(), self.old)
        self.assertEqual(self.leftovers(), [])

    def test_a_malformed_checksum_is_refused_before_any_download(self):
        o = self.serve(script_text("0.9.0"))
        for bad in ("", "abc", "g" * 64, "A" * 63, None):
            with self.assertRaises(agent.UpdateError):
                self.update(o, {"path": agent.UPDATE_PATH, "sha256": bad, "version": "0.9.0"})
        self.assertEqual(o.requests, [])

    def test_only_the_own_server_and_the_fixed_path_are_allowed(self):
        new = script_text("0.9.0")
        o = self.serve(new)
        for path in ("https://evil.example" + agent.UPDATE_PATH, "http://127.0.0.1:1" + agent.UPDATE_PATH, "//evil.example" + agent.UPDATE_PATH,
                     "/other.py", agent.UPDATE_PATH + "/../x", agent.UPDATE_PATH + "?x=1", agent.UPDATE_PATH[1:], "", None, 7):
            with self.assertRaises(agent.UpdateError, msg=repr(path)) as e:
                self.update(o, self.message(new, path=path))
            self.assertEqual(e.exception.status, 400)
        self.assertEqual(o.requests, [])                    # nicht einmal angefragt
        self.assertEqual(self.current(), self.old)

    def test_the_url_is_built_from_the_configured_server_only(self):
        self.assertEqual(agent.update_url("https://os.example.org/", agent.UPDATE_PATH), "https://os.example.org" + agent.UPDATE_PATH)
        self.assertEqual(agent.update_url("https://os.example.org/julos", agent.UPDATE_PATH), "https://os.example.org/julos" + agent.UPDATE_PATH)
        with self.assertRaises(agent.UpdateError):
            agent.update_url("http://os.example.org", agent.UPDATE_PATH)       # kein TLS: wuerde die Datei im Klartext holen

    def test_a_redirect_to_another_origin_is_not_followed(self):
        new = script_text("0.9.0")
        other = self.serve(new)
        o = self.serve(new, redirect_to=other.url + agent.UPDATE_PATH)
        with self.assertRaises(agent.UpdateError) as e:
            self.update(o, self.message(new))
        self.assertEqual(e.exception.status, 502)
        self.assertEqual(other.requests, [])
        self.assertEqual(self.current(), self.old)

    def test_an_oversized_file_is_refused(self):
        big = script_text("0.9.0", "#" + "x" * (agent.MAX_AGENT_FILE + 10) + "\n")
        o = self.serve(big)
        with self.assertRaises(agent.UpdateError) as e:
            self.update(o, self.message(big))
        self.assertEqual(e.exception.status, 413)
        self.assertEqual(self.leftovers(), [])

    def test_a_file_that_is_not_valid_python_or_has_another_version_is_refused_even_with_the_right_checksum(self):
        for body, why in ((b'AGENT_VERSION = "0.9.0"\ndef (:\n', "Syntax"), (script_text("0.8.0"), "Version"), (b"print('keine Version')\n", "ohne Version")):
            o = self.serve(body)
            with self.assertRaises(agent.UpdateError, msg=why) as e:
                self.update(o, self.message(body))
            self.assertEqual(e.exception.status, 422)
            self.assertEqual(self.current(), self.old)
            self.assertEqual(self.leftovers(), [])

    def test_already_current_does_not_download_or_replace(self):
        new = script_text("0.3.0")
        o = self.serve(new)
        self.assertEqual(self.update(o, self.message(new, version="0.3.0")), ("0.3.0", False))
        self.assertEqual(self.update(o, self.message(new, version="0.2.0")), ("0.3.0", False))     # nie zurueck auf eine aeltere
        self.assertEqual(o.requests, [])

    def test_a_failing_replace_leaves_the_old_file_and_no_temp_file(self):
        new = script_text("0.9.0")
        o = self.serve(new)
        with mock.patch.object(agent.os, "replace", side_effect=OSError("kein Platz")):
            with self.assertRaises(OSError):
                self.update(o, self.message(new))
        self.assertEqual(self.current(), self.old)
        self.assertEqual(self.leftovers(), [])

    def test_the_shipped_agent_file_is_a_valid_update_candidate(self):
        with open(AGENT_FILE, "rb") as f:
            data = f.read()
        compile(data, "julos-agent.py", "exec")
        self.assertIn(b'AGENT_VERSION = "' + agent.AGENT_VERSION.encode() + b'"', data)
        self.assertGreater(agent.version_tuple(agent.AGENT_VERSION), (0, 3, 0))
        self.assertLess(len(data), agent.MAX_AGENT_FILE)

    def test_version_tuple(self):
        self.assertEqual(agent.version_tuple("0.4.0"), (0, 4, 0))
        self.assertEqual(agent.version_tuple("1.10.2+x"), (1, 10, 2))
        self.assertIsNone(agent.version_tuple("x"))
        self.assertIsNone(agent.version_tuple(None))
        self.assertGreater(agent.version_tuple("0.10.0"), agent.version_tuple("0.9.0"))        # Zahlen, nicht Text


class OperationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        t = self.tmp.name
        self.install = os.path.join(t, "julos")
        os.makedirs(self.install)
        for n in ("julos-agent.py", "julos-clip.py", "install-linux-agent.sh"):
            with open(os.path.join(self.install, n), "w") as f:
                f.write("x")
        self.config = os.path.join(t, "config", "julos", "agent.json")
        os.makedirs(os.path.dirname(self.config))
        with open(self.config, "w") as f:
            f.write('{"server":"https://s.example","token":"jat_x"}')
        self.state = os.path.join(t, "state", "julos")
        os.makedirs(self.state)
        for n in ("agent.log", "agent.log.1", "agent.pid", "agent.status"):
            with open(os.path.join(self.state, n), "w") as f:
                f.write("1")
        self.autostart = os.path.join(t, "config", "autostart")
        os.makedirs(self.autostart)
        for n in ("julos-agent.desktop", "julos-clip.desktop", "fremd.desktop"):
            with open(os.path.join(self.autostart, n), "w") as f:
                f.write("[Desktop Entry]")
        self.patches = [
            mock.patch.object(agent, "SCRIPT", os.path.join(self.install, "julos-agent.py")),
            mock.patch.object(agent, "CONFIG", self.config),
            mock.patch.object(agent, "STATE_DIR", self.state),
            mock.patch.object(agent, "PID_FILE", os.path.join(self.state, "agent.pid")),
            mock.patch.object(agent, "PANEL_STATE", os.path.join(t, "nie-da.json")),
            mock.patch.object(agent, "autostart_dir", lambda: self.autostart),
            mock.patch.object(agent, "unit_file", lambda: os.path.join(t, "nie-da.service")),
            mock.patch.object(agent, "running_pid", lambda: None),
            mock.patch.object(agent, "stop_helper", lambda p: None),
        ]
        for p in self.patches:
            p.start()
            self.addCleanup(p.stop)
        quiet = contextlib.redirect_stdout(io.StringIO())          # die Meldungen der Befehle gehoeren nicht in die Testausgabe
        quiet.__enter__()
        self.addCleanup(quiet.__exit__, None, None, None)

    def test_uninstall_without_confirmation_in_a_script_does_nothing(self):
        with mock.patch.object(agent.sys.stdin, "isatty", return_value=False):
            self.assertEqual(agent.command_uninstall(False, False), 2)
        self.assertTrue(os.path.exists(self.config))
        self.assertTrue(os.path.exists(os.path.join(self.install, "julos-agent.py")))

    def test_uninstall_removes_everything_of_the_agent_and_nothing_else(self):
        self.assertEqual(agent.command_uninstall(True, False), 0)
        self.assertFalse(os.path.exists(self.install))               # Dateien weg, leerer Ordner auch
        self.assertFalse(os.path.exists(self.config))
        self.assertFalse(os.path.exists(os.path.dirname(self.config)))
        self.assertFalse(os.path.exists(self.state))
        self.assertEqual(sorted(os.listdir(self.autostart)), ["fremd.desktop"])        # fremde Eintraege bleiben

    def test_uninstall_can_keep_the_enrolment(self):
        agent.command_uninstall(True, True)
        self.assertTrue(os.path.exists(self.config))
        self.assertFalse(os.path.exists(os.path.join(self.install, "julos-agent.py")))

    def test_answering_no_keeps_everything(self):
        with mock.patch.object(agent.sys.stdin, "isatty", return_value=True), mock.patch("builtins.input", return_value="n"):
            self.assertEqual(agent.command_uninstall(False, False), 1)
        self.assertTrue(os.path.exists(os.path.join(self.install, "julos-agent.py")))

    def test_uninstall_never_deletes_program_files_in_a_git_checkout(self):
        os.makedirs(os.path.join(self.install, ".git"))
        agent.command_uninstall(True, False)
        self.assertTrue(os.path.exists(os.path.join(self.install, "julos-agent.py")))
        self.assertFalse(os.path.exists(self.config))                # Einrichtung und Autostart gehen trotzdem

    def test_requirements_name_what_is_missing(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            problems, _ = agent.missing_requirements()
        self.assertTrue(any("DISPLAY" in p for p in problems))
        with mock.patch.dict(os.environ, {"DISPLAY": ":0"}):
            with mock.patch.dict("sys.modules", {"gi": None}):          # "import gi" schlaegt fehl
                problems, _ = agent.missing_requirements()
        self.assertTrue(any("PyGObject" in p and "apt install" in p for p in problems))

    def test_the_log_rotates_keeps_three_old_files_and_is_private(self):
        path = os.path.join(self.state, "rot.log")
        h = agent._PrivateRotatingHandler(path, maxBytes=2000, backupCount=agent.LOG_KEEP, encoding="utf-8")
        lg = logging.getLogger("julos-agent-rotation-test")
        lg.propagate = False
        lg.addHandler(h)
        lg.setLevel(logging.INFO)
        for i in range(400):
            lg.info("Zeile %d %s", i, "x" * 40)
        h.close()
        names = sorted(n for n in os.listdir(self.state) if n.startswith("rot.log"))
        self.assertEqual(names, ["rot.log", "rot.log.1", "rot.log.2", "rot.log.3"])
        for n in names:
            self.assertEqual(stat.S_IMODE(os.stat(os.path.join(self.state, n)).st_mode), 0o600)

    def test_the_log_lives_in_the_user_folder_not_in_tmp(self):
        with mock.patch.dict(os.environ, {"HOME": "/home/jemand", "XDG_STATE_HOME": ""}):
            os.environ.pop("JULOS_AGENT_STATE", None)
            mod = load_agent()
        self.assertEqual(mod.LOG_FILE, "/home/jemand/.local/state/julos/agent.log")


if __name__ == "__main__":
    unittest.main()
