"""Tests fuer den EINEN Agenten (0.7.0): der Desktop-Agent beantwortet zusaetzlich die Host-Anfragen (docs/HOST-AGENT.md, "One agent per machine").

Ohne Anzeige, ohne PyGObject, ohne Root:  python3 -m unittest discover -s tests/agent
Hier laeuft NIE ein echtes apt, systemctl, journalctl, docker oder reboot: die Aktionen laufen gegen den Recorder aus test_actions.py, jedes echte Programm bricht den Test ab.
"""
import argparse
import hashlib
import http.server
import os
import stat
import tempfile
import threading
import unittest
from unittest import mock

from test_actions import JOB, AgentActionBase
from test_host import MiniServer, agent


def desktop_link(server, **kw):
    """Der Link, wie ihn der Desktop-Agent beim Start baut (siehe das Ende von julos-agent.py)."""
    cfg = {"server": "http://127.0.0.1:%d" % server.port, "token": "jat_test", "allow_http": True}
    return agent.Link(cfg, handler=agent.agent_api, hello=agent.desktop_hello, progress=True, restart=lambda: None, **kw)


class DesktopHelloTests(unittest.TestCase):
    def test_hello_names_desktop_host_and_what_the_machine_can(self):
        with mock.patch.object(agent, "host_caps", lambda *a, **k: ["sysinfo", "updates", "services", "docker"]), mock.patch.object(agent, "permissions", lambda args=None: {"logs", "updates"}):
            hello = agent.desktop_hello()
        self.assertEqual(hello["caps"], ["desktop", "host", "sysinfo", "updates", "services", "docker"])
        self.assertEqual(hello["allow"], ["logs", "updates"])
        self.assertNotIn("platform", hello)                                   # die Plattform bleibt linux-x11 (Link.session)
        self.assertEqual(agent.PLATFORM, "linux-x11")

    def test_a_machine_that_cannot_answer_host_requests_says_desktop_only(self):
        with mock.patch.object(agent, "host_caps", lambda *a, **k: []), mock.patch.object(agent, "permissions", lambda args=None: set()):
            self.assertEqual(agent.desktop_hello(), {"caps": ["desktop"], "allow": []})

    def test_the_host_agent_says_host_but_not_desktop(self):
        with mock.patch.object(agent, "host_caps", lambda *a, **k: ["sysinfo"]), mock.patch.object(agent, "permissions", lambda args=None: set()):
            hello = agent.host_hello()
        self.assertEqual((hello["platform"], hello["caps"]), ("linux-host", ["host", "sysinfo"]))

    def test_without_any_permission_file_nothing_is_allowed(self):
        # ein Selbst-Update eines alten Desktop-Agenten legt nie eine Datei an: das neue hello nennt keine Woerter
        with tempfile.TemporaryDirectory() as d, mock.patch.object(agent, "PERMISSIONS_FILE", os.path.join(d, "permissions")):
            self.assertEqual(agent.permissions(argparse.Namespace(allow_updates=False, allow_reboot=False)), set())


class RoutingTests(unittest.TestCase):
    def test_host_paths_go_to_the_host_part_and_everything_else_to_the_desktop_api(self):
        with mock.patch.object(agent, "host_api", lambda m, p, b, emit=None: (200, {"via": "host", "path": p})), mock.patch.object(agent, "api", lambda m, p, b: (200, {"via": "desktop", "path": p})):
            self.assertEqual(agent.agent_api("GET", "/v1/host/sysinfo", None)[1]["via"], "host")
            self.assertEqual(agent.agent_api("POST", "/v1/host/reboot", {})[1]["via"], "host")
            self.assertEqual(agent.agent_api("GET", "/v1/host/updates?x=1", None)[1]["via"], "host")
            for p in ("/v1/windows", "/v1/apps", "/v1/health", "/v1/mode", "/v1/hostile", "/v1/host"):
                self.assertEqual(agent.agent_api("GET", p, None)[1]["via"], "desktop", p)

    def test_the_local_socket_does_not_know_the_host_paths(self):
        # api() ist das, was der Unix-Socket bedient: dort gibt es /v1/host/* nicht
        self.assertEqual(agent.api("GET", "/v1/host/sysinfo", {})[0], 404)
        self.assertEqual(agent.api("POST", "/v1/host/reboot", {"job": JOB})[0], 404)


class DesktopAnswersHostRequestsTests(AgentActionBase):
    """Der Desktop-Agent gegen den Recorder: dieselben Pruefungen wie der Host-Agent, ueber den echten Link."""

    def setUp(self):
        super().setUp()
        for target, value in (("host_caps", lambda *a, **k: ["sysinfo", "updates", "services", "docker"]), ("write_status", lambda *a, **k: None),
                              ("collect_sysinfo", lambda *a, **k: {"hostname": "werkbank-01", "load": [0.1, 0.2, 0.3]}),
                              ("collect_services", lambda: (200, {"state": "running", "count": 0, "failed": []}))):
            p = mock.patch.object(agent, target, value)
            p.start()
            self.addCleanup(p.stop)
        self.api_calls = []
        p = mock.patch.object(agent, "api", lambda m, path, b: (self.api_calls.append((m, path)) or (200, {"windows": []})))
        p.start()
        self.addCleanup(p.stop)

    def start(self):
        server = MiniServer()
        link = desktop_link(server)
        threading.Thread(target=link.run, daemon=True).start()
        self.assertIsNotNone(server.wait(lambda m: m.get("t") == "hello"))
        return server

    def ask(self, server, id, method, path, body=None):
        server.send({"t": "req", "id": id, "method": method, "path": path, **({"body": body} if body is not None else {})})
        return server.wait(lambda m: m.get("t") == "res" and m.get("id") == id)

    def test_hello_and_every_kind_of_request_over_one_connection(self):
        self.rights = {"updates"}
        server = self.start()
        hello = server.wait(lambda m: m.get("t") == "hello")
        self.assertEqual((hello["platform"], hello["agent"]), ("linux-x11", agent.AGENT_VERSION))
        self.assertEqual(hello["caps"], ["desktop", "host", "sysinfo", "updates", "services", "docker"])
        self.assertEqual(hello["allow"], ["updates"])
        res = self.ask(server, 1, "GET", "/v1/host/sysinfo")
        self.assertEqual((res["status"], res["body"]["hostname"]), (200, "werkbank-01"))
        res = self.ask(server, 2, "GET", "/v1/host/services")
        self.assertEqual((res["status"], res["body"]["state"]), (200, "running"))
        res = self.ask(server, 3, "GET", "/v1/windows")                                        # die Desktop-API geht weiter an die Desktop-Funktion
        self.assertEqual((res["status"], self.api_calls), (200, [("GET", "/v1/windows")]))
        res = self.ask(server, 4, "GET", "/v1/host/jobs")
        self.assertEqual((res["status"], res["body"]), (200, {"jobs": []}))
        self.assertEqual(self.ask(server, 5, "GET", "/etc/passwd")["status"], 400)             # kein /v1/-Pfad
        self.assertEqual(self.ask(server, 6, "GET", "/v1/host/nonsense")["status"], 404)
        self.nothing_ran()

    def test_an_action_without_its_word_is_refused_and_nothing_runs(self):
        self.rights = {"updates"}                                                              # nur updates: alles andere 403
        server = self.start()
        for n, (path, body) in enumerate([("/v1/host/reboot", {}), ("/v1/host/poweroff", {}), ("/v1/host/service/stop", {"unit": "nginx.service"}),
                                           ("/v1/host/docker/stop", {"id": "a" * 12}), ("/v1/host/logs/unit", {"unit": "nginx.service"})]):
            res = self.ask(server, 20 + n, "POST", path, {"job": JOB, **body})
            self.assertEqual((res["status"], res["body"]["error"]), (403, "not_permitted"), path)
        res = self.ask(server, 30, "GET", "/v1/host/units")
        self.assertEqual((res["status"], res["body"]["error"]), (403, "not_permitted"))
        self.nothing_ran()                                                                     # weder Helfer noch Docker noch ein Programm

    def test_an_allowed_action_sends_exactly_its_request_with_progress(self):
        self.rights = {"updates"}
        self.rec.helper_lines = ["Setting up libc6 (2.36-10) ..."]
        server = self.start()
        res = self.ask(server, 40, "POST", "/v1/host/updates/install", {"job": JOB, "all": True})
        self.assertEqual((res["status"], res["body"]["ok"]), (200, True))
        self.assertEqual(self.rec.helper, [{"verb": "apt.upgrade"}])
        self.assertEqual(self.rec.docker, [])
        self.assertTrue([m for m in server.messages if m.get("t") == "progress" and m["id"] == 40])

    def test_foto_and_file_transfers_still_belong_to_the_desktop_agent(self):
        server = self.start()
        server.send({"t": "put", "id": 50, "kind": "bogus", "size": 3})
        self.assertEqual(server.wait(lambda m: m.get("t") == "res" and m.get("id") == 50)["status"], 413)       # verstanden und geprueft, nicht "gibt es nicht" (404)

    def test_the_host_reads_work_over_the_same_code_as_the_host_agent(self):
        # keine Kopie: die Antworten des Desktop-Agenten sind genau die des Host-Teils
        for path in ("/v1/host/sysinfo", "/v1/host/services", "/v1/host/jobs"):
            self.assertEqual(agent.agent_api("GET", path, None), agent.host_api("GET", path, None), path)
        self.rights = set()
        self.assertEqual(agent.agent_api("POST", "/v1/host/reboot", {"job": JOB}), agent.host_api("POST", "/v1/host/reboot", {"job": JOB}))


class SelfUpdateTests(unittest.TestCase):
    """Ein Desktop-Agent 0.5.x/0.6.0 aktualisiert sich auf 0.7.0, ohne dass dabei ein Recht entsteht."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.script = os.path.join(self.tmp.name, "julos-agent.py")
        with open(self.script, "wb") as f:
            f.write(b'AGENT_VERSION = "0.6.0"\n')
        self.perm = os.path.join(self.tmp.name, "permissions")

    def serve(self, body):
        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
        httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        self.addCleanup(httpd.server_close)
        self.addCleanup(httpd.shutdown)
        return "http://127.0.0.1:%d" % httpd.server_address[1]

    def test_the_update_replaces_the_agent_file_and_never_touches_the_permission_file(self):
        for start in ("0.5.0", "0.5.2", "0.6.0"):
            with open(self.script, "wb") as f:
                f.write(b'AGENT_VERSION = "%s"\n' % start.encode())
            with open(self.perm, "w") as f:
                f.write("# nur dieser Test\nlogs\n")
            os.chmod(self.perm, 0o644)
            before = os.stat(self.perm)
            with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "julos-agent.py"), "rb") as f:
                shipped = f.read()
            url = self.serve(shipped)
            msg = {"path": agent.UPDATE_PATH, "sha256": hashlib.sha256(shipped).hexdigest(), "version": agent.AGENT_VERSION}
            self.assertEqual(agent.apply_update(url, msg, script=self.script, allow_http=True, current=start), (agent.AGENT_VERSION, True))
            with open(self.perm) as f:
                self.assertEqual(agent.parse_permissions(f.read()), {"logs"})                 # unveraendert: kein Wort kam hinzu, keines fiel weg
            after = os.stat(self.perm)
            self.assertEqual((before.st_mtime_ns, before.st_size, stat.S_IMODE(after.st_mode)), (after.st_mtime_ns, after.st_size, 0o644))
            self.assertEqual(sorted(os.listdir(self.tmp.name)), ["julos-agent.py", "permissions"])

    def test_the_update_code_has_no_path_to_the_permission_file(self):
        # der Selbst-Update kennt nur die Agent-Datei: die Rechte-Datei wird dort nie genannt
        import inspect
        for fn in (agent.apply_update, agent.fetch_verified, agent.update_url):
            self.assertNotIn("PERMISSIONS_FILE", inspect.getsource(fn))
            self.assertNotIn("permissions", inspect.getsource(fn))

    def test_the_shipped_version_is_the_one_the_server_expects(self):
        self.assertEqual(agent.version_tuple(agent.AGENT_VERSION), (0, 8, 0))
        self.assertTrue(agent.version_tuple(agent.AGENT_VERSION) > agent.version_tuple("0.6.0"))


if __name__ == "__main__":
    unittest.main()
