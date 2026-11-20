"""Tests fuer den Dateizugriff als root (Wort files-root, docs/FILES.md): die Verben des root-Helfers (tools/julos-agent-helper.py, FileOps) gegen ein einsetzbares Wurzelverzeichnis
und die Weiterleitung im Agenten (files_root, /v1/host/files/*). Ohne Root lauffaehig: jeder Pfad liegt unter einem Wegwerfordner, der Agent bekommt einen Helfer-Recorder.
"""
import base64
import io
import json
import os
import shutil
import stat
import tempfile
import unittest
from unittest import mock

from test_actions import helper
from test_host import agent


def b64(data):
    return base64.b64encode(data).decode()


class FileOpsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.ops = helper.FileOps(self.tmp)

    def p(self, rel):
        return os.path.join(self.tmp, rel)

    def put(self, rel, data=b"x", mode=None):
        os.makedirs(os.path.dirname(self.p(rel)), exist_ok=True)
        with open(self.p(rel), "wb") as f:
            f.write(data)
        if mode is not None:
            os.chmod(self.p(rel), mode)

    # ---- Pfade
    def test_paths_are_absolute_and_plain(self):
        n = helper.norm_path
        self.assertEqual(n("/a//b/./c/"), "/a/b/c")
        self.assertEqual(n("/"), "/")
        for bad in ("a/b", "", "/a/../b", "/..", "/a\nb", "/a\x00b", "/" + "x" * 5000, None, 5, "/" + "/".join("a" * 129)):
            self.assertIsNone(n(bad), repr(bad)[:30])
        self.assertEqual(self.ops.list({"path": "relativ"}), {"ok": False, "code": "invalid_path"})
        self.assertEqual(self.ops.stat({"path": "/a/../b"}), {"ok": False, "code": "invalid_path"})

    # ---- list und stat
    def test_list_shows_kinds_sizes_links_and_hides_names_with_control_characters(self):
        self.put("d/file.txt", b"12345")
        os.makedirs(self.p("d/sub"))
        os.symlink("file.txt", self.p("d/zur-datei"))
        os.symlink("sub", self.p("d/zum-ordner"))
        os.symlink("nirgends", self.p("d/kaputt"))
        self.put("d/bad\nname")
        r = self.ops.list({"path": "/d"})
        self.assertTrue(r["ok"])
        by = {e["name"]: e for e in r["entries"]}
        self.assertEqual((by["file.txt"]["kind"], by["file.txt"]["size"]), ("file", 5))
        self.assertEqual(by["sub"]["kind"], "directory")
        self.assertEqual((by["zur-datei"]["kind"], by["zur-datei"]["resolved"]), ("link", "file"))
        self.assertEqual((by["zum-ordner"]["kind"], by["zum-ordner"]["resolved"]), ("link", "directory"))
        self.assertEqual((by["kaputt"]["kind"], "resolved" in by["kaputt"]), ("link", False))
        self.assertEqual((r["hidden"], r["truncated"], "bad\nname" in by), (1, False, False))
        self.assertRegex(by["file.txt"]["mode"], r"^[r-][w-][x-][r-][w-][x-][r-][w-][x-]\Z")

    def test_list_is_cut_at_the_limit_and_errors_have_codes(self):
        for i in range(30):
            self.put("big/f%d" % i)
        r = self.ops.list({"path": "/big", "limit": 10})
        self.assertEqual((len(r["entries"]), r["truncated"]), (10, True))
        self.assertEqual(self.ops.list({"path": "/nirgends"})["code"], "not_found")
        self.assertEqual(self.ops.list({"path": "/big/f1"})["code"], "not_a_directory")
        self.assertEqual(len(self.ops.list({"path": "/big", "limit": 10**9})["entries"]), 30)

    def test_stat_is_lstat_without_follow_and_stat_with_it(self):
        self.put("s/f", b"abc")
        os.symlink("f", self.p("s/l"))
        os.symlink("weg", self.p("s/kaputt"))
        lst = self.ops.stat({"path": "/s/l"})["entry"]
        self.assertEqual((lst["kind"], lst["resolved"]), ("link", "file"))
        self.assertEqual(self.ops.stat({"path": "/s/l", "follow": True})["entry"]["kind"], "file")
        self.assertEqual(self.ops.stat({"path": "/s/kaputt"})["entry"]["kind"], "link")
        self.assertEqual(self.ops.stat({"path": "/s/kaputt", "follow": True}), {"ok": True, "exists": False})
        self.assertEqual(self.ops.stat({"path": "/s/gibts-nicht"}), {"ok": True, "exists": False})
        self.assertEqual(self.ops.stat({"path": "/"})["entry"]["kind"], "directory")

    # ---- Lesen in Bloecken
    def test_read_returns_blocks_with_eof_and_follows_a_link_to_a_file(self):
        data = bytes(range(256)) * 400                                     # 102 400 Byte
        self.put("r/big.bin", data)
        os.symlink("big.bin", self.p("r/l"))
        got, off = b"", 0
        while True:
            r = self.ops.read({"path": "/r/l", "offset": off, "length": helper.FILE_BLOCK})
            self.assertTrue(r["ok"])
            chunk = base64.b64decode(r["data"])
            self.assertEqual(r["n"], len(chunk))
            got += chunk
            off += len(chunk)
            if r["eof"]:
                break
        self.assertEqual(got, data)
        self.assertEqual(r["size"], len(data))

    def test_read_refuses_directories_fifos_bad_ranges_and_missing_files_without_blocking(self):
        os.makedirs(self.p("x/dir"))
        os.mkfifo(self.p("x/fifo"))
        self.assertEqual(self.ops.read({"path": "/x/dir", "offset": 0, "length": 10})["code"], "not_a_file")
        self.assertEqual(self.ops.read({"path": "/x/fifo", "offset": 0, "length": 10})["code"], "not_a_file")
        self.assertEqual(self.ops.read({"path": "/x/nein", "offset": 0, "length": 10})["code"], "not_found")
        for bad in ({"offset": -1, "length": 10}, {"offset": 0, "length": 0}, {"offset": 0, "length": helper.FILE_BLOCK + 1}, {"offset": "0", "length": 10}, {"offset": True, "length": 10}):
            self.assertEqual(self.ops.read({"path": "/x/dir", **bad})["code"], "invalid_request", bad)

    # ---- Schreiben nur in eine neue temporaere Datei, dann Umbenennen
    def test_write_goes_into_a_new_temporary_file_in_order_and_a_rename_commits_it(self):
        os.makedirs(self.p("w"))
        tmpname = "/w/.julos-0123456789ab.part"
        self.assertEqual(self.ops.create({"path": tmpname}), {"ok": True})
        self.assertEqual(self.ops.create({"path": tmpname})["code"], "exists")                        # nie ueberschreiben
        self.assertEqual(self.ops.write({"path": tmpname, "offset": 0, "data": b64(b"hallo ")}), {"ok": True, "size": 6})
        self.assertEqual(self.ops.write({"path": tmpname, "offset": 6, "data": b64("weltä".encode())})["size"], 12)
        self.assertEqual(self.ops.write({"path": tmpname, "offset": 3, "data": b64(b"X")})["code"], "bad_offset")     # kein Ueberschreiben, keine Luecke
        self.assertEqual(self.ops.write({"path": tmpname, "offset": 99, "data": b64(b"X")})["code"], "bad_offset")
        self.assertEqual(self.ops.rename({"from": tmpname, "to": "/w/neu.txt"}), {"ok": True})
        with open(self.p("w/neu.txt"), "rb") as f:
            self.assertEqual(f.read(), "hallo weltä".encode())
        self.assertEqual(stat.S_IMODE(os.stat(self.p("w/neu.txt")).st_mode), 0o644)
        self.assertFalse(os.path.exists(self.p("w/.julos-0123456789ab.part")))

    def test_only_julos_temporary_names_can_be_created_or_written(self):
        os.makedirs(self.p("w"))
        self.put("w/vorhanden.txt", b"alt", 0o600)
        for name in ("/w/vorhanden.txt", "/w/neu.txt", "/w/.julos-xyz.part", "/w/.julos-0123456789ab.part.x"):
            self.assertEqual(self.ops.create({"path": name})["code"], "invalid_name", name)
            self.assertEqual(self.ops.write({"path": name, "offset": 3, "data": b64(b"X")})["code"], "invalid_name", name)
        with open(self.p("w/vorhanden.txt"), "rb") as f:
            self.assertEqual(f.read(), b"alt")                                                       # eine vorhandene Datei aendert dieser Helfer nie durch Schreiben

    def test_write_never_goes_through_a_link_and_rejects_bad_data(self):
        os.makedirs(self.p("w"))
        self.put("w/ziel", b"wichtig")
        os.symlink("ziel", self.p("w/.julos-0123456789ab.part"))
        self.assertEqual(self.ops.create({"path": "/w/.julos-0123456789ab.part"})["code"], "exists")
        self.assertIn(self.ops.write({"path": "/w/.julos-0123456789ab.part", "offset": 7, "data": b64(b"X")})["code"], ("is_link", "failed"))
        with open(self.p("w/ziel"), "rb") as f:
            self.assertEqual(f.read(), b"wichtig")
        self.assertEqual(self.ops.create({"path": "/w/.julos-aaaaaaaaaaaa.part"}), {"ok": True})
        for bad in ("!!!", "Zm9v\n", b64(b"x" * (helper.FILE_BLOCK + 1)), 5, None):
            self.assertEqual(self.ops.write({"path": "/w/.julos-aaaaaaaaaaaa.part", "offset": 0, "data": bad})["code"], "invalid_request", repr(bad)[:20])

    def test_a_rename_replaces_only_with_overwrite_keeps_owner_and_mode_and_never_replaces_links_or_folders(self):
        os.makedirs(self.p("m/dir"))
        self.put("m/alt.txt", b"alt", 0o640)
        self.ops.create({"path": "/m/.julos-0123456789ab.part"})
        self.ops.write({"path": "/m/.julos-0123456789ab.part", "offset": 0, "data": b64(b"neu")})
        self.assertEqual(self.ops.rename({"from": "/m/.julos-0123456789ab.part", "to": "/m/alt.txt"})["code"], "exists")          # ohne overwrite nie
        self.assertEqual(self.ops.rename({"from": "/m/.julos-0123456789ab.part", "to": "/m/alt.txt", "overwrite": True}), {"ok": True})
        with open(self.p("m/alt.txt"), "rb") as f:
            self.assertEqual(f.read(), b"neu")
        self.assertEqual(stat.S_IMODE(os.stat(self.p("m/alt.txt")).st_mode), 0o640)                # Rechte der ersetzten Datei bleiben
        os.symlink("alt.txt", self.p("m/link"))
        self.put("m/quelle", b"q")
        self.assertEqual(self.ops.rename({"from": "/m/quelle", "to": "/m/link", "overwrite": True})["code"], "type_mismatch")
        self.assertEqual(self.ops.rename({"from": "/m/quelle", "to": "/m/dir", "overwrite": True})["code"], "type_mismatch")
        self.assertEqual(self.ops.rename({"from": "/m/link", "to": "/m/alt.txt", "overwrite": True})["code"], "type_mismatch")
        self.assertEqual(self.ops.rename({"from": "/m/nirgends", "to": "/m/x"})["code"], "not_found")
        # Umbenennen eines Links benennt den Link um, nie sein Ziel
        self.assertEqual(self.ops.rename({"from": "/m/link", "to": "/m/link2"}), {"ok": True})
        self.assertEqual((os.path.islink(self.p("m/link2")), os.path.exists(self.p("m/alt.txt"))), (True, True))
        self.assertEqual(os.readlink(self.p("m/link2")), "alt.txt")

    # ---- Loeschen und Ordner
    def test_delete_removes_a_file_or_a_link_never_the_target_and_a_folder_only_empty(self):
        self.put("x/f", b"1")
        os.symlink("f", self.p("x/l"))
        os.makedirs(self.p("x/leer"))
        os.makedirs(self.p("x/voll"))
        self.put("x/voll/in", b"2")
        self.assertEqual(self.ops.delete({"path": "/x/l"}), {"ok": True})
        self.assertEqual((os.path.lexists(self.p("x/l")), os.path.exists(self.p("x/f"))), (False, True))
        self.assertEqual(self.ops.delete({"path": "/x/leer"})["code"], "is_directory")
        self.assertEqual(self.ops.rmdir({"path": "/x/voll"})["code"], "not_empty")
        self.assertEqual(self.ops.rmdir({"path": "/x/leer"}), {"ok": True})
        self.assertEqual(self.ops.delete({"path": "/x/f"}), {"ok": True})
        self.assertEqual(self.ops.delete({"path": "/x/f"})["code"], "not_found")
        self.assertEqual(self.ops.mkdir({"path": "/x/neu"}), {"ok": True})
        self.assertEqual(self.ops.mkdir({"path": "/x/neu"})["code"], "exists")
        self.assertEqual(self.ops.mkdir({"path": "/nirgends/unter"})["code"], "not_found")
        self.assertTrue(os.path.isdir(self.p("x/voll")))

    def test_the_agents_own_files_and_the_root_are_never_changed(self):
        for path in ("/", "/etc/julos-agent", "/etc/julos-agent/permissions", "/usr/local/libexec/julos-agent/julos-agent-helper.py", "/opt/julos-agent/julos-agent.py", "/var/lib/julos-agent/jobs.json"):
            for verb, req in (("delete", {"path": path}), ("rmdir", {"path": path}), ("mkdir", {"path": path}), ("create", {"path": path}), ("rename", {"from": path, "to": "/zz"}), ("rename", {"from": "/zz", "to": path})):
                r = getattr(self.ops, verb)(req)
                self.assertIn(r.get("code"), ("protected_path", "root_forbidden"), (verb, path))
        self.assertEqual(self.ops.delete({"path": "/etc/julos-agentx"})["code"], "not_found")          # nur genau diese Pfade, nicht alles mit gleichem Anfang

    def test_a_link_to_the_agents_files_does_not_get_around_the_protection(self):
        os.makedirs(self.p("etc/julos-agent"))
        with open(self.p("etc/julos-agent/permissions"), "w") as f:
            f.write("files-root\n")
        os.makedirs(self.p("srv"))
        os.symlink(self.p("etc/julos-agent"), self.p("srv/rein"))               # ein Link ausserhalb, der in den geschuetzten Ordner zeigt (absolut)
        os.symlink("../etc", self.p("srv/etc"))                                  # und einer auf den Ordner davor (relativ): der Name julos-agent darunter bleibt geschuetzt
        self.put("zz", b"z")
        def run(verb, req):
            try:
                return getattr(self.ops, verb)(req)
            except helper.ProtectedPath:
                return {"ok": False, "code": "protected_path"}
        for base in ("/srv/rein", "/srv/etc/julos-agent"):
            for verb, req in (("delete", {"path": base + "/permissions"}), ("mkdir", {"path": base + "/x"}), ("create", {"path": base + "/.julos-0123456789ab.part"}),
                              ("write", {"path": base + "/.julos-0123456789ab.part", "offset": 0, "data": "eA=="}), ("rename", {"from": base + "/permissions", "to": "/zz2"}),
                              ("rename", {"from": "/zz", "to": base + "/permissions", "overwrite": True})):
                self.assertEqual(run(verb, req).get("code"), "protected_path", (verb, base))
        self.assertEqual(run("rmdir", {"path": "/srv/rein"})["code"], "not_a_directory")                   # der Link selbst ist kein Ordner; sein Ziel bleibt in jedem Fall
        self.assertTrue(os.path.isdir(self.p("etc/julos-agent")))
        self.assertEqual(open(self.p("etc/julos-agent/permissions")).read(), "files-root\n")
        self.assertEqual(os.listdir(self.p("etc/julos-agent")), ["permissions"])
        self.assertEqual(self.ops.list({"path": "/srv/rein"})["entries"][0]["name"], "permissions")      # ansehen und lesen darf man, nur nicht veraendern
        self.assertTrue(self.ops.read({"path": "/srv/rein/permissions", "offset": 0, "length": 100})["ok"])
        self.assertEqual(run("mkdir", {"path": "/srv/frei"}), {"ok": True})                                # alles andere ist unberuehrt

    def test_an_error_text_of_the_system_never_reaches_the_answer(self):
        self.assertEqual(helper.os_code(PermissionError()), "denied")
        self.assertEqual(helper.os_code(OSError(18, "Invalid cross-device link")), "cross_device")
        self.assertEqual(helper.os_code(OSError(5, "Input/output error at /secret/path")), "failed")


class HelperDispatchTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)

    def make(self, words):
        return helper.Helper(permissions=lambda: set(words), fs_root=self.tmp)

    def test_every_files_verb_needs_the_word_and_unknown_verbs_are_refused(self):
        for verb in ("list", "stat", "read", "create", "write", "rename", "delete", "rmdir", "mkdir"):
            self.assertEqual(self.make({"updates", "logs", "services"}).handle({"verb": "files." + verb, "path": "/"}, lambda x: None), {"ok": False, "code": "not_permitted"}, verb)
            self.assertNotEqual(self.make({"files-root"}).handle({"verb": "files." + verb, "path": "/"}, lambda x: None).get("code"), "not_permitted", verb)
        self.assertEqual(self.make({"files-root"}).handle({"verb": "files.chmod", "path": "/"}, lambda x: None)["code"], "unknown_verb")
        self.assertEqual(self.make({"files-root"}).handle({"verb": "files.", "path": "/"}, lambda x: None)["code"], "unknown_verb")
        self.assertIn("files-root", helper.RIGHTS)
        self.assertIn("files-root", agent.CONTROL_RIGHTS)

    def test_a_full_block_fits_into_one_request_line_and_comes_back_over_serve(self):
        os.makedirs(os.path.join(self.tmp, "d"))
        h = self.make({"files-root"})
        out = io.BytesIO()
        helper.serve(io.BytesIO(json.dumps({"verb": "files.create", "path": "/d/.julos-0123456789ab.part"}).encode() + b"\n"), out, h)
        self.assertEqual(json.loads(out.getvalue().splitlines()[-1]), {"r": {"ok": True}})
        block = b"\x00\xff" * (helper.FILE_BLOCK // 2)
        line = json.dumps({"verb": "files.write", "path": "/d/.julos-0123456789ab.part", "offset": 0, "data": b64(block)}).encode() + b"\n"
        self.assertGreater(len(line), 65536)                                                           # mehr als die frueheren 64 KiB der Anfragezeile
        out = io.BytesIO()
        helper.serve(io.BytesIO(line), out, h)
        self.assertEqual(json.loads(out.getvalue().splitlines()[-1]), {"r": {"ok": True, "size": helper.FILE_BLOCK}})
        out = io.BytesIO()
        helper.serve(io.BytesIO(json.dumps({"verb": "files.read", "path": "/d/.julos-0123456789ab.part", "offset": 0, "length": helper.FILE_BLOCK}).encode() + b"\n"), out, h)
        r = json.loads(out.getvalue().splitlines()[-1])["r"]
        self.assertEqual(base64.b64decode(r["data"]), block)
        self.assertLess(len(out.getvalue()), 262144)                                                  # so liest der Agent die Antwortzeile (readline(262144))


class AgentFilesRootTests(unittest.TestCase):
    def setUp(self):
        self.calls = []
        self.reply = {"ok": True, "entries": []}
        self.rights = {"files-root"}
        stack = [mock.patch.object(agent, "permissions", lambda args=None: set(self.rights)), mock.patch.object(agent, "helper_call", self.helper_call)]
        for m in stack:
            m.start()
            self.addCleanup(m.stop)

    def helper_call(self, request, on_line=None, timeout=3600, path=None):
        self.calls.append(request)
        return self.reply

    def test_the_agent_forwards_only_with_the_word_and_only_known_fields(self):
        status, body = agent.host_api("POST", "/v1/host/files/list", {"path": "/etc", "limit": 5, "verb": "apt.refresh", "extra": "x"})
        self.assertEqual((status, body), (200, {"ok": True, "entries": []}))
        self.assertEqual(self.calls, [{"verb": "files.list", "path": "/etc", "limit": 5}])            # nie ein fremdes Feld oder Verb des Absenders
        self.rights = {"updates", "logs", "services", "docker", "reboot"}
        self.assertEqual(agent.host_api("POST", "/v1/host/files/list", {"path": "/"}), (403, {"error": "not_permitted", "need": "files-root"}))
        self.assertEqual(len(self.calls), 1)                                                             # ohne das Wort erreicht den Helfer nichts

    def test_every_verb_is_forwarded_with_its_own_fields(self):
        for verb, fields in agent.FILE_FIELDS.items():
            body = {k: "/x" if k in ("path", "from", "to") else 1 for k in fields}
            agent.host_api("POST", "/v1/host/files/" + verb, dict(body, sneaky=True))
            self.assertEqual(self.calls[-1], dict({"verb": "files." + verb}, **body), verb)
        self.assertEqual(agent.host_api("POST", "/v1/host/files/chmod", {"path": "/x"}), (400, {"error": "invalid_request"}))
        self.assertEqual(agent.host_api("POST", "/v1/host/files/list", [1]), (400, {"error": "invalid_request"}))
        self.assertEqual(agent.host_api("GET", "/v1/host/files/list", None), (404, {"error": "unbekannt"}))      # nur POST

    def test_helper_codes_become_statuses_and_texts_of_the_helper_never_pass(self):
        for code, status in (("not_found", 404), ("denied", 403), ("exists", 409), ("not_empty", 409), ("invalid_path", 400), ("protected_path", 403), ("bad_offset", 409)):
            self.reply = {"ok": False, "code": code}
            self.assertEqual(agent.host_api("POST", "/v1/host/files/stat", {"path": "/x"}), (status, {"error": code}))
        self.reply = {"ok": False, "code": "Permission denied: /root/.ssh/id_rsa"}
        self.assertEqual(agent.host_api("POST", "/v1/host/files/stat", {"path": "/x"}), (500, {"error": "failed"}))
        self.reply = {"ok": False, "code": "unbekannt_aber_sauber"}
        self.assertEqual(agent.host_api("POST", "/v1/host/files/stat", {"path": "/x"})[0], 400)
        self.reply = None                                                                                  # kein Helfer
        self.assertEqual(agent.host_api("POST", "/v1/host/files/stat", {"path": "/x"}), (503, {"error": "helper_unavailable"}))

    def test_the_version_and_the_word_are_known(self):
        self.assertEqual(agent.AGENT_VERSION, "0.8.0")
        self.assertEqual(agent.parse_permissions("updates\nfiles-root  # Root\nfiles\nFILES-ROOT\n"), {"updates", "files-root"})


if __name__ == "__main__":
    unittest.main()
