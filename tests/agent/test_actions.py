"""Tests fuer die Schreib-Aktionen des Host-Agenten (Stufe 3, docs/HOST-AGENT.md) und den root-Helfer tools/julos-agent-helper.py.

Ohne Root, ohne systemd, ohne apt und Docker lauffaehig:  python3 -m unittest discover -s tests/agent
Hier laeuft NIE ein echtes apt, systemctl, journalctl, docker oder reboot: der Agent bekommt einen Helfer-Recorder und einen Docker-Socket-Recorder, der Helfer einen
einsetzbaren Runner; jedes echte Programm (subprocess) bricht den Test ab. Die Recorder beweisen, dass genau die erwarteten Anfragen und nichts sonst gesendet wurden.
"""
import importlib.util
import io
import json
import os
import re
import shutil
import socketserver
import http.server
import socket
import struct
import tempfile
import threading
import unittest
from unittest import mock

from test_host import MiniServer, agent

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def load_helper():
    spec = importlib.util.spec_from_file_location("julos_agent_helper_under_test", os.path.join(ROOT, "tools", "julos-agent-helper.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


helper = load_helper()
ORIGINAL_PERMISSIONS = agent.permissions

JOB = "0123456789abcdef0123456789abcdef"
JOB2 = "fedcba9876543210fedcba9876543210"
UNITS = "nginx.service loaded active running A web server\nbackup.service loaded inactive dead Nightly backup\njulos-agent.service loaded active running JulOS agent\nghost.service not-found inactive dead ghost.service\n"
CONTAINERS = {"a" * 12: {"id": "a" * 12, "name": "web", "image": "nginx:1.27", "state": "running", "status": "Up"},
              "b" * 12: {"id": "b" * 12, "name": "pinned", "image": "alpine@sha256:" + "0" * 64, "state": "exited", "status": "Exited"},
              "c" * 12: {"id": "c" * 12, "name": "bare", "image": "0123456789ab", "state": "running", "status": "Up"}}
ALL_RIGHTS = {"updates", "reboot", "services", "docker", "logs"}


def boom(*a, **k):
    raise AssertionError("ein echtes Programm wurde gestartet: %r" % (a,))


class Recorder:
    """Merkt jede Anfrage an den Helfer und an Docker und antwortet aus vorbereiteten Antworten."""

    def __init__(self):
        self.helper, self.docker = [], []
        self.helper_reply = {"ok": True, "output": "fertig"}
        self.helper_lines = []
        self.helper_gate = None
        self.docker_replies = {}

    def helper_call(self, request, on_line=None, timeout=3600, path=None):
        self.helper.append(request)
        for line in self.helper_lines:
            on_line and on_line(line)
        if self.helper_gate:
            self.helper_gate.wait(5)
        return self.helper_reply

    def docker_call(self, method, path, timeout=30, limit=0, socket_path=None):
        self.docker.append((method, path))
        for (m, prefix), reply in self.docker_replies.items():
            if m == method and path.startswith(prefix):
                return reply
        return (204, b"")


class AgentActionBase(unittest.TestCase):
    rights = ALL_RIGHTS

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.rec = Recorder()
        self.emitted = []
        stack = [
            mock.patch.object(agent, "JOB_FILE", os.path.join(self.tmp, "jobs.json")),
            mock.patch.object(agent, "_JOBS", None),
            mock.patch.object(agent, "WRITE_LOCK", threading.Lock()),
            mock.patch.object(agent, "permissions", lambda args=None: set(self.rights)),
            mock.patch.object(agent, "update_manager", lambda which=None: "apt"),
            mock.patch.object(agent, "systemd_running", lambda e=None: True),
            mock.patch.object(agent, "collect_updates", lambda: (200, {"packages": [{"name": "libc6"}, {"name": "bash"}, {"name": "libstdc++6"}], "count": 3})),
            mock.patch.object(agent, "collect_docker", lambda socket_path=None: (200, {"available": True, "containers": list(CONTAINERS.values())})),
            mock.patch.object(agent, "reboot_required", lambda *a: True),
            mock.patch.object(agent, "run_cmd", self.fake_cmd),
            mock.patch.object(agent, "helper_call", self.rec.helper_call),
            mock.patch.object(agent, "docker_call", self.rec.docker_call),
            mock.patch.object(agent.subprocess, "run", boom),
            mock.patch.object(agent.subprocess, "Popen", boom),
            mock.patch.object(agent.threading, "Timer", mock.MagicMock()),
        ]
        for p in stack:
            p.start()
            self.addCleanup(p.stop)

    def fake_cmd(self, argv, timeout=30):
        if argv[:2] == ["systemctl", "list-units"]:
            return 0, UNITS, ""
        raise AssertionError("unerwartetes Programm: %r" % (argv,))

    def post(self, path, body=None, **extra):
        body = {"job": JOB, **(body or {}), **extra}
        return agent.host_api("POST", path, body, lambda *a: self.emitted.append(a))

    def nothing_ran(self):
        self.assertEqual((self.rec.helper, self.rec.docker), ([], []))


class WriteActionTests(AgentActionBase):
    def test_every_action_sends_exactly_its_request_and_nothing_else(self):
        cases = [
            ("/v1/host/updates/refresh", {}, [{"verb": "apt.refresh"}], []),
            ("/v1/host/updates/install", {"all": True}, [{"verb": "apt.upgrade"}], []),
            ("/v1/host/updates/install", {"packages": ["libc6", "libstdc++6", "libc6"]}, [{"verb": "apt.upgrade", "packages": ["libc6", "libstdc++6"]}], []),
            ("/v1/host/reboot", {}, [{"verb": "power", "op": "reboot", "delay": agent.POWER_DELAY}], []),
            ("/v1/host/poweroff", {}, [{"verb": "power", "op": "poweroff", "delay": agent.POWER_DELAY}], []),
            ("/v1/host/service/start", {"unit": "backup.service"}, [{"verb": "service", "op": "start", "unit": "backup.service"}], []),
            ("/v1/host/service/stop", {"unit": "nginx.service"}, [{"verb": "service", "op": "stop", "unit": "nginx.service"}], []),
            ("/v1/host/service/restart", {"unit": "nginx.service"}, [{"verb": "service", "op": "restart", "unit": "nginx.service"}], []),
            ("/v1/host/logs/unit", {"unit": "nginx.service", "lines": 50}, [{"verb": "journal", "unit": "nginx.service", "lines": 50}], []),
            ("/v1/host/docker/start", {"id": "a" * 12}, [], [("POST", "/containers/%s/start" % ("a" * 12))]),
            ("/v1/host/docker/stop", {"id": "a" * 12}, [], [("POST", "/containers/%s/stop?t=10" % ("a" * 12))]),
            ("/v1/host/docker/restart", {"id": "a" * 12}, [], [("POST", "/containers/%s/restart?t=10" % ("a" * 12))]),
        ]
        for n, (path, body, helper_calls, docker_calls) in enumerate(cases):
            with self.subTest(path=path, body=body):
                self.setUp() if n else None
                self.rec.helper_reply = {"ok": True, "output": "fertig", "lines": ["a", "b"]} if "logs" in path else {"ok": True, "output": "fertig"}
                if "reboot" in path or "poweroff" in path:
                    self.rec.helper_reply = {"ok": True, "accepted": True}
                status, res = self.post(path, body, job=JOB)
                self.assertEqual(status, 200, res)
                self.assertTrue(res["ok"])
                self.assertEqual((res["job"], self.rec.helper, self.rec.docker), (JOB, helper_calls, docker_calls))

    def test_logs_of_a_container_demultiplex_and_stay_bounded(self):
        frames = b"".join(struct.pack(">BxxxI", 1, len(l)) + l for l in (b"eins\n", b"zwei\x1b[31m\n", b"drei\n"))
        self.rec.docker_replies = {("GET", "/containers/" + "a" * 12 + "/json"): (200, b'{"Config":{"Tty":false}}'), ("GET", "/containers/" + "a" * 12 + "/logs"): (200, frames)}
        status, res = self.post("/v1/host/logs/container", {"id": "a" * 12, "lines": 5000})
        self.assertEqual(status, 200)
        self.assertEqual(res["lines"], ["eins", "zwei[31m", "drei"])                        # Steuerzeichen entfernt
        self.assertIn(("GET", "/containers/%s/logs?stdout=1&stderr=1&tail=%d" % ("a" * 12, agent.LOG_LINES_MAX)), self.rec.docker)    # nach oben begrenzt
        self.assertTrue(all(m == "GET" for m, _ in self.rec.docker))                         # Logs lesen schreibt nichts

    def test_install_reports_progress_and_the_reboot_hint(self):
        self.rec.helper_lines = ["Get:1 http://deb.example/ libc6 2.36", "Setting up libc6 (2.36-10) ...", "Setting up bash (5.2-2) ..."]
        status, res = self.post("/v1/host/updates/install", {"all": True})
        self.assertEqual((status, res["rebootRequired"], res["count"]), (200, True, 3))
        self.assertTrue(self.emitted)                                                       # der Fortschritt ging an den Server
        done = [e for e in self.emitted if e[1] is not None]
        self.assertTrue(done and done[0][2] == 3)
        job = agent.public_jobs()["jobs"][0]
        self.assertEqual((job["state"], job["action"], job["target"]), ("done", "updates.install", "all"))

    def test_failures_come_back_as_short_codes_not_free_text(self):
        self.rec.helper_reply = {"ok": False, "code": "apt_locked", "output": "E: Could not get lock\x00"}
        status, res = self.post("/v1/host/updates/install", {"all": True})
        self.assertEqual((status, res["error"]), (500, "apt_locked"))
        self.assertNotIn("\x00", res["output"])
        self.rec.helper_reply = {"ok": False, "code": "Rm -rf /; evil"}
        self.assertEqual(self.post("/v1/host/updates/refresh", job=JOB2)[1]["error"], "action_failed")
        self.rec.helper_reply = None                                                         # Helfer nicht da
        self.assertEqual(self.post("/v1/host/service/restart", {"unit": "nginx.service"}, job="1" * 32), (503, {"error": "helper_unavailable", "job": "1" * 32, "action": "service.restart"}))
        self.rec.helper_reply = {"ok": False, "code": "not_permitted"}                       # der Helfer prueft selbst noch einmal
        self.assertEqual(self.post("/v1/host/updates/refresh", job="2" * 32)[0], 403)

    def test_other_package_managers_are_not_implemented(self):
        with mock.patch.object(agent, "update_manager", lambda which=None: "dnf"):
            self.assertEqual(self.post("/v1/host/updates/install", {"all": True})[0], 501)
            self.assertEqual(self.post("/v1/host/updates/refresh", job=JOB2)[0], 501)
        self.nothing_ran()


class RefusalTests(AgentActionBase):
    def test_every_route_is_refused_without_its_local_permission(self):
        bodies = {"/v1/host/updates/install": {"all": True}, "/v1/host/service/start": {"unit": "nginx.service"}, "/v1/host/service/stop": {"unit": "nginx.service"},
                  "/v1/host/service/restart": {"unit": "nginx.service"}, "/v1/host/logs/unit": {"unit": "nginx.service"}, "/v1/host/logs/container": {"id": "a" * 12},
                  "/v1/host/docker/start": {"id": "a" * 12}, "/v1/host/docker/stop": {"id": "a" * 12}, "/v1/host/docker/restart": {"id": "a" * 12}, "/v1/host/docker/pull": {"id": "a" * 12}}
        for path, (need, _action) in agent.WRITE_ROUTES.items():
            with self.subTest(path=path):
                for other in sorted(ALL_RIGHTS - {need}):
                    self.rights = {other}                                                    # ein anderes Wort schaltet nichts frei
                    self.assertEqual(self.post(path, bodies.get(path, {})), (403, {"error": "not_permitted", "need": need}))
                self.rights = set()
                self.assertEqual(self.post(path, bodies.get(path, {}))[0], 403)
        self.nothing_ran()
        self.assertEqual(agent.public_jobs()["jobs"], [])                                    # eine Ablehnung hinterlaesst keinen Auftrag

    def test_listing_the_units_needs_the_services_word_too(self):
        self.rights = {"updates"}
        self.assertEqual(agent.host_api("GET", "/v1/host/units", None), (403, {"error": "not_permitted", "need": "services"}))
        self.rights = {"services"}
        status, body = agent.host_api("GET", "/v1/host/units", None)
        self.assertEqual((status, [u["unit"] for u in body["units"]]), (200, ["backup.service", "julos-agent.service", "nginx.service"]))   # nicht geladene (not-found) fehlen

    def test_no_permission_by_default(self):
        with mock.patch.object(agent, "read_permissions", lambda *a, **k: set()), mock.patch.object(agent, "permissions", ORIGINAL_PERMISSIONS):
            self.assertEqual(agent.permissions(agent.argparse.Namespace(allow_updates=False, allow_reboot=False)), set())
        self.assertEqual(agent.parse_permissions("# updates\n  # reboot\n"), set())

    def test_only_get_for_reads_and_only_post_for_actions(self):
        self.assertEqual(agent.host_api("GET", "/v1/host/updates/install", None)[0], 404)
        self.assertEqual(agent.host_api("POST", "/v1/host/updates", {"job": JOB})[0], 404)
        self.assertEqual(agent.host_api("POST", "/v1/host/exec", {"job": JOB, "cmd": "id"})[0], 404)
        self.assertEqual(agent.host_api("POST", "/v1/host/../reboot", {"job": JOB})[0], 404)
        self.nothing_ran()


class InjectionTests(AgentActionBase):
    BAD_PACKAGES = ["x; rm -rf /", "$(id)", "`id`", "libc6 bash", "libc6\nbash", "-o", "--allow-unauthenticated", "pkg-", "pkg=1.0", "pkg/stable", "../etc/passwd", "", "A-B", "a" * 200 + "!", 5, None, ["x"]]

    def test_package_names_must_look_like_packages_and_come_from_the_update_list(self):
        for bad in self.BAD_PACKAGES:
            with self.subTest(package=bad):
                self.assertEqual(self.post("/v1/host/updates/install", {"packages": [bad]}, job=os.urandom(16).hex())[0], 400)
        self.assertEqual(self.post("/v1/host/updates/install", {"packages": ["openssl"]}, job="3" * 32), (404, {"error": "unknown_target", "job": "3" * 32, "action": "updates.install"}))   # sauber, aber nicht in der Liste
        self.assertEqual(self.post("/v1/host/updates/install", {"packages": []}, job="4" * 32)[0], 400)
        self.assertEqual(self.post("/v1/host/updates/install", {"packages": ["libc6"] * 21}, job="5" * 32)[0], 400)
        self.assertEqual(self.post("/v1/host/updates/install", {"all": True, "packages": ["libc6"]}, job="6" * 32)[0], 400)    # entweder alles oder Namen
        self.assertEqual(self.post("/v1/host/updates/install", {}, job="7" * 32)[0], 400)
        self.nothing_ran()

    def test_unit_names_must_look_like_units_and_come_from_the_reported_list(self):
        bad = ["nginx.service; reboot", "nginx", "../nginx.service", "-nginx.service", "ng inx.service", "nginx.service\n", "ghost.service", "unknown.service", "nginx.timer", 7, None, ""]
        for path in ("/v1/host/service/start", "/v1/host/service/stop", "/v1/host/service/restart", "/v1/host/logs/unit"):
            for unit in bad:
                with self.subTest(path=path, unit=unit):
                    status, res = self.post(path, {"unit": unit}, job=os.urandom(16).hex())
                    self.assertIn(status, (400, 404), res)
        self.nothing_ran()

    def test_container_ids_must_be_twelve_hex_chars_from_the_container_list(self):
        for path in ("/v1/host/docker/start", "/v1/host/docker/stop", "/v1/host/docker/restart", "/v1/host/docker/pull", "/v1/host/logs/container"):
            for cid in ["abc", "../../etc/passwd", "A" * 12, "g" * 12, "a" * 12 + "/exec", "a" * 13, "d" * 12, "a" * 12 + "?x=1", 5, None]:
                with self.subTest(path=path, id=cid):
                    self.assertIn(self.post(path, {"id": cid}, job=os.urandom(16).hex())[0], (400, 404))
        self.nothing_ran()

    def test_job_ids_are_checked_and_cannot_be_replayed(self):
        self.assertEqual(self.post("/v1/host/updates/refresh", job="x; id")[0], 400)
        self.assertEqual(agent.host_api("POST", "/v1/host/updates/refresh", {})[0], 400)
        self.assertEqual(agent.host_api("POST", "/v1/host/updates/refresh", [1])[0], 400)
        self.assertEqual(self.post("/v1/host/updates/refresh")[0], 200)
        self.assertEqual(self.post("/v1/host/updates/refresh"), (409, {"error": "duplicate_job"}))
        self.assertEqual(len(self.rec.helper), 1)                                            # die Wiederholung tat nichts

    def test_nothing_in_the_agent_or_helper_uses_a_shell(self):
        for name in ("julos-agent.py", "julos-agent-helper.py"):
            text = open(os.path.join(ROOT, "tools", name), encoding="utf-8").read()
            self.assertNotRegex(text, r"shell\s*=\s*True|os\.system|os\.popen|\bsudo\b\s+-|commands\.getoutput", name)

    def test_the_package_pattern_never_lets_apt_syntax_through(self):
        for pattern in (agent.PKG_RE, helper.PKG_RE):
            for ok in ("libc6", "libstdc++6", "g++", "libc6:amd64", "x", "python3.11-dev"):
                self.assertTrue(pattern.match(ok), ok)
            for bad in ("pkg-", "pkg=1", "pkg/x", "-x", "a b", "a;b", "a:", "a:b:c", "A", "pkg-:amd64", "x\n", "libc6\n"):
                self.assertFalse(pattern.match(bad), bad)


class BusyAndWayBackTests(AgentActionBase):
    def test_only_one_write_action_at_a_time(self):
        self.rec.helper_gate = threading.Event()
        first = []
        t = threading.Thread(target=lambda: first.append(self.post("/v1/host/updates/install", {"all": True})))
        t.start()
        deadline = 50
        while not self.rec.helper and deadline:
            threading.Event().wait(0.05)
            deadline -= 1
        status, res = self.post("/v1/host/service/restart", {"unit": "nginx.service"}, job=JOB2)
        self.assertEqual((status, res["error"], res["action"]), (409, "busy", "updates.install"))
        self.rec.helper_gate.set()
        t.join(5)
        self.assertEqual(first[0][0], 200)
        self.assertEqual(self.post("/v1/host/service/restart", {"unit": "nginx.service"}, job=JOB2)[0], 200)     # danach geht es wieder
        self.assertEqual(len(self.rec.helper), 2)                                            # die abgewiesene Aktion hat nichts gestartet

    def test_the_result_can_be_fetched_later_and_survives_a_restart_of_the_agent(self):
        self.post("/v1/host/updates/refresh")
        status, body = agent.host_api("GET", "/v1/host/jobs", None)
        self.assertEqual(status, 200)
        job = body["jobs"][0]
        self.assertEqual((job["job"], job["state"], job["status"], job["result"]["ok"]), (JOB, "done", 200, True))
        self.assertTrue(os.path.exists(agent.JOB_FILE))
        self.assertEqual(os.stat(agent.JOB_FILE).st_mode & 0o777, 0o600)
        again = agent.JobStore(agent.JOB_FILE)                                               # neuer Prozess liest die Datei
        self.assertEqual([j["job"] for j in again.snapshot()], [JOB])

    def test_a_job_that_was_running_when_the_agent_died_is_marked_interrupted(self):
        store = agent.JobStore(agent.JOB_FILE)
        store.start(JOB2, "updates.install", "all")
        again = agent.JobStore(agent.JOB_FILE)
        j = again.snapshot()[0]
        self.assertEqual((j["state"], j["result"]), ("interrupted", {"error": "interrupted"}))

    def test_only_the_last_jobs_are_kept_and_log_content_is_never_stored(self):
        self.rec.helper_reply = {"ok": True, "lines": ["geheim 1"]}
        self.post("/v1/host/logs/unit", {"unit": "nginx.service"})
        self.assertNotIn("geheim", open(agent.JOB_FILE).read())
        self.assertNotIn("lines", json.dumps(agent.public_jobs()))
        store = agent.JobStore(os.path.join(self.tmp, "x.json"), keep=3)
        for i in range(6):
            store.start("%032x" % i, "updates.refresh", "")
        self.assertEqual(len(store.snapshot()), 3)

    def test_a_reboot_is_answered_as_accepted_and_keeps_the_agent_busy(self):
        self.rec.helper_reply = {"ok": True, "accepted": True}
        status, res = self.post("/v1/host/reboot")
        self.assertEqual((status, res["accepted"], res["delay"]), (200, True, agent.POWER_DELAY))
        self.assertEqual(agent.public_jobs()["jobs"][0]["state"], "accepted")
        self.assertEqual(self.post("/v1/host/updates/refresh", job=JOB2)[1]["error"], "busy")  # waehrend des Herunterfahrens keine weitere Aktion


class ServiceAndDockerRuleTests(AgentActionBase):
    def test_the_agent_and_its_helper_cannot_be_stopped_or_restarted_but_their_logs_can_be_read(self):
        for op in ("stop", "restart"):
            self.assertEqual(self.post("/v1/host/service/" + op, {"unit": "julos-agent.service"}, job=os.urandom(16).hex())[1]["error"], "protected_unit")
        self.nothing_ran()
        self.assertEqual(self.post("/v1/host/logs/unit", {"unit": "julos-agent.service"}, job="9" * 32)[0], 200)

    def test_image_references(self):
        self.assertEqual(agent.image_ref("nginx:1.27"), ("nginx", "1.27"))
        self.assertEqual(agent.image_ref("nginx"), ("nginx", "latest"))
        self.assertEqual(agent.image_ref("registry.example:5000/team/app"), ("registry.example:5000/team/app", "latest"))
        self.assertEqual(agent.image_ref("registry.example:5000/team/app:2"), ("registry.example:5000/team/app", "2"))
        for none in ("alpine@sha256:" + "0" * 64, "0123456789ab", "sha256:" + "a" * 64, "x; id", "", None, "UPPER case", "-x"):
            self.assertIsNone(agent.image_ref(none), none)

    def test_pull_reports_whether_the_image_changed(self):
        ids = iter([b'{"Id":"sha256:old"}', b'{"Id":"sha256:new"}'])
        self.rec.docker_replies = {("POST", "/images/create"): (200, b'{"status":"Pulling"}\n{"status":"Downloaded"}\n')}
        real = self.rec.docker_call

        def call(method, path, *a, **k):
            if method == "GET" and path.startswith("/images/"):
                self.rec.docker.append((method, path))
                return 200, next(ids)
            return real(method, path, *a, **k)
        with mock.patch.object(agent, "docker_call", call):
            status, res = self.post("/v1/host/docker/pull", {"id": "a" * 12})
        self.assertEqual((status, res["image"], res["updated"]), (200, "nginx:1.27", True))
        self.assertEqual([c for c in self.rec.docker if c[0] == "POST"], [("POST", "/images/create?fromImage=nginx&tag=1.27")])

    def test_pull_of_a_digest_pinned_or_id_only_image_is_refused_and_pull_errors_are_noticed(self):
        self.assertEqual(self.post("/v1/host/docker/pull", {"id": "b" * 12}, job=JOB2)[1]["error"], "no_registry_image")
        self.assertEqual(self.post("/v1/host/docker/pull", {"id": "c" * 12}, job="8" * 32)[1]["error"], "no_registry_image")
        self.assertEqual([c for c in self.rec.docker if c[0] == "POST"], [])
        self.rec.docker_replies = {("POST", "/images/create"): (200, b'{"status":"Pulling"}\n{"error":"denied: nope"}\n')}
        self.assertEqual(self.post("/v1/host/docker/pull", {"id": "a" * 12}, job="7" * 32)[1]["error"], "pull_failed")

    def test_docker_answers_map_to_short_codes(self):
        cid = {"id": "a" * 12}
        self.rec.docker_replies = {("POST", "/containers/"): (304, b"")}
        self.assertEqual(self.post("/v1/host/docker/start", cid, job="1" * 32)[1]["already"], True)
        self.rec.docker_replies = {("POST", "/containers/"): (403, b"")}
        self.assertEqual(self.post("/v1/host/docker/stop", cid, job="2" * 32)[1]["error"], "docker_denied")
        self.rec.docker_replies = {("POST", "/containers/"): ("denied", b"")}
        self.assertEqual(self.post("/v1/host/docker/stop", cid, job="3" * 32)[1]["error"], "docker_denied")
        self.rec.docker_replies = {("POST", "/containers/"): (500, b"")}
        self.assertEqual(self.post("/v1/host/docker/stop", cid, job="4" * 32)[1]["error"], "docker_failed")
        with mock.patch.object(agent, "collect_docker", lambda socket_path=None: (200, {"available": False, "reason": "no_access"})):
            self.assertEqual(self.post("/v1/host/docker/stop", cid, job="5" * 32)[1]["error"], "docker_unavailable")

    def test_container_summary_carries_the_compose_project(self):
        r = agent.summarize_containers([{"Id": "a" * 64, "Names": ["/web"], "Image": "x", "State": "running", "Status": "Up", "Labels": {"com.docker.compose.project": "julos"}},
                                        {"Id": "b" * 64, "Names": ["/db"], "Image": "y", "State": "running", "Status": "Up", "Labels": None}])
        self.assertEqual([c.get("project") for c in r["containers"]], [None, "julos"])


class RealDockerSocketTests(unittest.TestCase):
    """docker_call gegen einen echten Unix-Socket: die Methode und der Pfad sind genau die erwarteten (kein Docker noetig)."""

    def test_the_request_goes_through_the_socket_with_method_and_path(self):
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, True)
        path, seen = os.path.join(d, "docker.sock"), []

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def address_string(self):
                return "unix"

            def do_POST(self):
                seen.append(("POST", self.path))
                self.send_response(204)
                self.send_header("Content-Length", "0")
                self.end_headers()

        class S(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
            daemon_threads = True
        srv = S(path, H)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(srv.server_close)
        self.addCleanup(srv.shutdown)
        self.assertEqual(agent.docker_call("POST", "/containers/%s/start" % ("a" * 12), socket_path=path), (204, b""))
        self.assertEqual(seen, [("POST", "/containers/%s/start" % ("a" * 12))])
        self.assertIsNone(agent.docker_call("POST", "/containers/x/start", socket_path=os.path.join(d, "none.sock")))


class LinkProgressTests(AgentActionBase):
    def test_progress_messages_carry_the_request_id_and_the_result_follows(self):
        self.rec.helper_lines = ["Setting up libc6 (2.36-10) ..."]
        server = MiniServer()
        link = agent.Link({"server": "http://127.0.0.1:%d" % server.port, "token": "jat_t", "allow_http": True}, handler=agent.host_api, hello=lambda: {"platform": "linux-host", "caps": [], "allow": []},
                          restart=lambda: None, transfers=False)
        with mock.patch.object(agent, "write_status", lambda *a, **k: None):
            threading.Thread(target=link.run, daemon=True).start()
            self.assertIsNotNone(server.wait(lambda m: m.get("t") == "hello"))
            server.send({"t": "req", "id": 41, "method": "POST", "path": "/v1/host/updates/install", "body": {"job": JOB, "all": True}})
            res = server.wait(lambda m: m.get("t") == "res" and m.get("id") == 41)
            self.assertEqual((res["status"], res["body"]["ok"]), (200, True))
            prog = [m for m in server.messages if m.get("t") == "progress"]
            self.assertTrue(prog and all(m["id"] == 41 for m in prog))
            self.assertEqual(prog[0]["text"], "apt-get upgrade")
            self.assertIn("libc6", agent.public_jobs()["jobs"][0]["progress"])                   # der letzte Stand steht im Auftrag (die Meldungen selbst sind gedrosselt)
            server.send({"t": "req", "id": 42, "method": "POST", "path": "/v1/host/updates/install", "body": {"job": JOB2, "packages": ["x; id"]}})
            self.assertEqual(server.wait(lambda m: m.get("t") == "res" and m.get("id") == 42)["status"], 400)
            server.send({"t": "req", "id": 43, "method": "GET", "path": "/v1/host/jobs"})
            jobs = server.wait(lambda m: m.get("t") == "res" and m.get("id") == 43)["body"]["jobs"]
            self.assertEqual([j["job"] for j in jobs][-1], JOB)


# ---------------------------------------------------------------- Der Helfer
class HelperTests(unittest.TestCase):
    def make(self, rights=ALL_RIGHTS, results=None):
        self.calls, self.slept, self.sent = [], [], []
        results = results or {}

        def run(argv, on_line, timeout):
            self.calls.append(argv)
            for key, (rc, lines) in results.items():
                if key in argv:
                    for l in lines:
                        on_line(l)
                    return rc, lines
            return 0, ["ok"]
        return helper.Helper(run=run, sleep=self.slept.append, permissions=lambda: set(rights))

    def handle(self, h, req):
        return h.handle(req, self.sent.append)

    def test_each_verb_runs_exactly_one_fixed_program_without_a_shell(self):
        h = self.make()
        self.assertTrue(self.handle(h, {"verb": "apt.refresh"})["ok"])
        self.assertEqual(self.calls[-1][:2], ["apt-get", "update"])
        self.assertTrue(self.handle(h, {"verb": "apt.upgrade"})["ok"])
        self.assertEqual(self.calls[-1][:2], ["apt-get", "upgrade"])
        self.assertTrue(self.handle(h, {"verb": "apt.upgrade", "packages": ["libc6", "bash", "libc6"]})["ok"])
        self.assertEqual(self.calls[-1][:4], ["apt-get", "install", "--only-upgrade", "--no-remove"])
        self.assertEqual(self.calls[-1][-2:], ["libc6", "bash"])
        self.assertTrue(self.handle(h, {"verb": "service", "op": "restart", "unit": "nginx.service"})["ok"])
        self.assertIn(["systemctl", "restart", "--no-pager", "--", "nginx.service"], self.calls)
        self.assertEqual(self.calls[-1], ["systemctl", "is-active", "--", "nginx.service"])
        self.assertTrue(self.handle(h, {"verb": "journal", "unit": "nginx.service", "lines": 20})["ok"])
        self.assertEqual(self.calls[-1], ["journalctl", "--unit=nginx.service", "-n", "20", "--no-pager", "-o", "short-iso", "--quiet"])
        self.assertTrue(all(isinstance(a, str) for c in self.calls for a in c))

    def test_without_the_word_in_the_file_nothing_runs(self):
        for verb, need in helper.VERB_RIGHT.items():
            h = self.make(rights=ALL_RIGHTS - {need})
            req = {"verb": verb, "op": "reboot", "unit": "nginx.service", "packages": None}
            self.assertEqual(self.handle(h, req), {"ok": False, "code": "not_permitted"}, verb)
        self.assertEqual(self.calls, [])
        self.assertEqual(self.make(rights=set()).handle({"verb": "apt.refresh"}, lambda x: None), {"ok": False, "code": "not_permitted"})

    def test_unknown_verbs_and_bad_values_are_refused_before_any_program(self):
        h = self.make()
        for req in ({"verb": "exec", "argv": ["id"]}, {"verb": None}, {}, {"verb": "apt.upgrade", "packages": ["x; id"]}, {"verb": "apt.upgrade", "packages": ["pkg-"]},
                    {"verb": "apt.upgrade", "packages": []}, {"verb": "apt.upgrade", "packages": "libc6"}, {"verb": "apt.upgrade", "packages": ["-o"]},
                    {"verb": "service", "op": "mask", "unit": "nginx.service"}, {"verb": "service", "op": "stop", "unit": "nginx.service; id"}, {"verb": "service", "op": "stop", "unit": "-x.service"},
                    {"verb": "journal", "unit": "../x.service"}, {"verb": "power", "op": "kexec"}):
            r = self.handle(h, req)
            self.assertFalse(r["ok"], req)
        self.assertEqual(self.calls, [])
        self.assertEqual(self.handle(h, {"verb": "service", "op": "stop", "unit": "julos-agent.service"})["code"], "protected_unit")
        self.assertEqual(self.handle(h, {"verb": "service", "op": "restart", "unit": "julos-agent-helper@1.service"})["code"], "protected_unit")
        self.assertEqual(self.calls, [])

    def test_power_says_accepted_first_then_waits_then_acts(self):
        h = self.make()
        order = []
        h.sleep = lambda s: order.append(("sleep", s, list(self.sent)))
        self.handle(h, {"verb": "power", "op": "reboot", "delay": 1})
        self.assertEqual(self.sent[0], {"a": True})
        self.assertEqual(order[0][:2], ("sleep", 2))                                           # mindestens zwei Sekunden
        self.assertEqual(order[0][2], [{"a": True}])                                           # "angenommen" war schon raus
        self.assertEqual(self.calls, [["systemctl", "reboot"]])
        self.handle(h, {"verb": "power", "op": "poweroff", "delay": 999})
        self.assertEqual(order[1][1], 30)                                                      # hoechstens dreissig
        self.assertEqual(self.calls[-1], ["systemctl", "poweroff"])

    def test_failure_codes(self):
        h = self.make(results={"apt-get": (100, ["E: Could not get lock /var/lib/dpkg/lock-frontend"])})
        self.assertEqual(self.handle(h, {"verb": "apt.refresh"})["code"], "apt_locked")
        h = self.make(results={"apt-get": (100, ["E: Broken packages"])})
        self.assertEqual(self.handle(h, {"verb": "apt.refresh"})["code"], "apt_failed")
        h = self.make(results={"restart": (5, ["Failed"])})
        self.assertEqual(self.handle(h, {"verb": "service", "op": "restart", "unit": "x.service"})["code"], "systemctl_failed")
        h = helper.Helper(run=lambda a, o, t: (None, []), sleep=lambda s: None, permissions=lambda: ALL_RIGHTS)
        self.assertEqual(h.handle({"verb": "apt.refresh"}, lambda x: None)["code"], "not_installed")

    def test_the_permission_file_counts_only_when_root_owns_it_and_nobody_else_can_write(self):
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, True)
        p = os.path.join(d, "permissions")
        open(p, "w").write("updates\nreboot # ja\nformat\n")
        os.chmod(p, 0o644)
        me = os.getuid()
        self.assertEqual(helper.read_permissions(p, trusted_uids=(me,)), {"updates", "reboot"})
        self.assertEqual(helper.read_permissions(p, trusted_uids=(me + 1,)), set())
        os.chmod(p, 0o666)
        self.assertEqual(helper.read_permissions(p, trusted_uids=(me,)), set())
        self.assertEqual(helper.PERMISSIONS_FILE, "/etc/julos-agent/permissions")

    def test_serve_reads_one_line_answers_in_lines_and_survives_a_closed_reader(self):
        h = self.make()
        out = io.BytesIO()
        helper.serve(io.BytesIO(b'{"verb":"apt.refresh"}\n'), out, h)
        lines = [json.loads(l) for l in out.getvalue().splitlines()]
        self.assertEqual(lines[-1]["r"]["ok"], True)
        out = io.BytesIO()
        helper.serve(io.BytesIO(b"kein json\n"), out, self.make())
        self.assertEqual(json.loads(out.getvalue())["r"]["code"], "invalid_request")

        class Broken:
            def write(self, b):
                raise BrokenPipeError

            def flush(self):
                pass
        h = self.make()
        helper.serve(io.BytesIO(b'{"verb":"apt.upgrade"}\n'), Broken(), h)                    # der Agent hat aufgelegt: die Aktion lief trotzdem
        self.assertEqual(h and self.calls[-1][:2], ["apt-get", "upgrade"])


class AgentToHelperTests(unittest.TestCase):
    """Der Agent spricht ueber einen echten Unix-Socket mit dem Helfer (Fake-Runner): Fortschritt, Ergebnis, angenommen, fehlender Helfer."""

    def serve(self, h):
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, True)
        path = os.path.join(d, "helper.sock")

        class H(socketserver.StreamRequestHandler):
            def handle(self):
                helper.serve(self.rfile, self.wfile, h)

        class S(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
            daemon_threads = True
        srv = S(path, H)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(srv.server_close)
        self.addCleanup(srv.shutdown)
        return path

    def test_progress_and_result_travel_over_the_socket(self):
        ran = []

        def run(argv, on_line, timeout):
            ran.append(argv)
            on_line("Setting up libc6 ...")
            return 0, ["Setting up libc6 ..."]
        path = self.serve(helper.Helper(run=run, sleep=lambda s: None, permissions=lambda: {"updates"}))
        seen = []
        r = agent.helper_call({"verb": "apt.upgrade", "packages": ["libc6"]}, seen.append, 10, path)
        self.assertEqual((r["ok"], seen), (True, ["Setting up libc6 ..."]))
        self.assertEqual(ran[0][:3], ["apt-get", "install", "--only-upgrade"])
        r = agent.helper_call({"verb": "service", "op": "stop", "unit": "nginx.service"}, None, 10, path)         # Wort fehlt: der Helfer lehnt ab
        self.assertEqual(r, {"ok": False, "code": "not_permitted"})
        self.assertEqual(len(ran), 1)

    def test_accepted_comes_back_before_the_delay_runs_out(self):
        gate = threading.Event()
        ran = []
        h = helper.Helper(run=lambda a, o, t: (ran.append(a), (0, []))[1], sleep=lambda s: gate.wait(5), permissions=lambda: {"reboot"})
        path = self.serve(h)
        r = agent.helper_call({"verb": "power", "op": "reboot", "delay": 5}, None, 10, path)
        self.assertEqual(r, {"ok": True, "accepted": True})
        self.assertEqual(ran, [])                                                              # noch nicht ausgefuehrt: erst nach der Verzoegerung
        gate.set()

    def test_no_helper_means_none(self):
        self.assertIsNone(agent.helper_call({"verb": "apt.refresh"}, None, 5, "/nonexistent/helper.sock"))


if __name__ == "__main__":
    unittest.main()
