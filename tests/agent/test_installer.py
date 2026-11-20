"""Tests fuer tools/install-agent.sh (der EINE Installer: Desktop und Host) im Trockenlauf: ohne Root, ohne Netz, ohne etwas zu veraendern (siehe docs/HOST-AGENT.md).

Der echte Lauf (Benutzer anlegen, Dienst installieren) steht im Handtest im Wegwerf-Container, nicht hier: ein Test darf die Maschine nicht veraendern.
Die Erkennung (Desktop oder Host) wird ueber JULOS_INSTALL_FAKE_DESKTOP=1|0 (Unterbau und Sitzung), JULOS_INSTALL_FAKE_HOME und JULOS_INSTALL_FAKE_HOST gesteuert, die der Installer nur
im Trockenlauf beachtet; so haengt kein Test davon ab, was auf dem Entwicklungsrechner installiert ist.
"""
import hashlib
import http.server
import os
import shutil
import subprocess
import tempfile
import threading
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TOOLS = os.path.join(ROOT, "tools")
SCRIPT = os.path.join(TOOLS, "install-agent.sh")
CODE = "ABCDE-FGHIJ"
SERVER = "https://julos.example.org"


def run(*args, env=None, script=SCRIPT, extra_env=None):
    """Der Installer im Trockenlauf (der Aufrufer gibt --dry-run mit). Ohne Angabe ist die Erkennung auf 'kein Desktop' gestellt, damit die Host-Tests unabhaengig vom Rechner sind."""
    with tempfile.TemporaryDirectory() as cwd:
        e = dict(env or os.environ)
        e.setdefault("JULOS_INSTALL_FAKE_DESKTOP", "0")
        e["JULOS_INSTALL_FAKE_HOME"] = os.path.join(cwd, "home")
        e.update(extra_env or {})
        p = subprocess.run(["sh", script, *args], capture_output=True, text=True, cwd=cwd, timeout=60, env=e)
        return p.returncode, p.stdout, p.stderr


class DryRunTests(unittest.TestCase):          # der Host-Zweig (--host): unveraendert gegenueber dem frueheren install-host-agent.sh
    def test_a_dry_run_lists_every_step_and_the_unit_and_needs_no_root(self):
        rc, out, err = run("--dry-run", "--host", "--server", SERVER, "--code", CODE)
        self.assertEqual(rc, 0, err)
        for step in ("create the system user julos-agent", "/opt/julos-agent", "/var/lib/julos-agent", "SHA-256", "/etc/julos-agent/permissions", "enrol this machine", "Dry run finished"):
            self.assertIn(step, out)
        self.assertIn(SERVER + "/agent-files/julos-agent.py", out)
        self.assertIn(SERVER + "/agent-files/SHA256SUMS", out)
        self.assertNotIn(CODE, out + err)                      # der Code steht nirgends in der Ausgabe

    def test_the_unit_is_locked_down_and_starts_the_agent_in_host_mode(self):
        rc, out, _ = run("--dry-run", "--host", "--server", SERVER, "--code", CODE)
        if "julos-agent.service:" not in out:
            self.skipTest("no systemd on this machine: the unit is not shown")
        for line in ("User=julos-agent", "NoNewPrivileges=yes", "ProtectSystem=strict", "ProtectHome=read-only", "PrivateTmp=yes", "CapabilityBoundingSet=", "Restart=on-failure",
                     "ReadWritePaths=/var/lib/julos-agent /opt/julos-agent", "--host"):
            self.assertIn(line, out)
        self.assertNotIn("SupplementaryGroups", out)             # kein docker-Zugriff ohne ausdrueckliche Wahl
        self.assertNotIn("--allow-http", out)

    def test_options_show_up(self):
        rc, out, _ = run("--dry-run", "--host", "--server", SERVER, "--code", CODE, "--allow-updates", "--docker-group", "--user", "jagent")
        self.assertEqual(rc, 0)
        self.assertIn("add the words: updates", out)
        self.assertIn("jagent", out)
        if "julos-agent.service:" in out:
            self.assertIn("SupplementaryGroups=docker", out)

    def test_no_action_word_is_ever_a_default(self):
        rc, out, err = run("--dry-run", "--host", "--server", SERVER, "--code", CODE)
        self.assertEqual(rc, 0, err)
        self.assertIn("with no word: the agent can only read", out)
        self.assertIn("No action words", out)
        self.assertNotIn("add the words", out)
        self.assertNotIn("julos-agent-helper", out)              # ohne Wort kein root-Helfer

    def test_allowed_words_install_the_root_helper_only_when_something_needs_root(self):
        rc, out, err = run("--dry-run", "--host", "--server", SERVER, "--code", CODE, "--allow", "updates,logs", "--allow", "logs")
        self.assertEqual(rc, 0, err)
        self.assertIn("add the words: updates logs", out)         # jedes Wort einmal
        if "julos-agent.service:" in out:                         # nur mit systemd
            self.assertIn(SERVER + "/agent-files/julos-agent-helper.py", out)
            for line in ("ListenStream=/run/julos-agent-helper.sock", "SocketUser=root", "SocketGroup=julos-agent", "SocketMode=0660", "Accept=yes", "StandardInput=socket", "root-owned"):
                self.assertIn(line, out)
            self.assertNotIn("SocketMode=0666", out)
        rc, out, err = run("--dry-run", "--host", "--server", SERVER, "--code", CODE, "--allow", "docker")
        self.assertEqual(rc, 0, err)
        self.assertIn("add the words: docker", out)
        self.assertNotIn("julos-agent-helper", out)               # Docker geht ueber den Socket, nicht ueber root
        self.assertIn("no access to the Docker socket", err)      # und der Installer sagt, was noch fehlt

    def test_files_root_is_a_known_word_that_needs_the_helper_and_opens_its_sandbox(self):
        rc, out, err = run("--dry-run", "--host", "--server", SERVER, "--code", CODE, "--allow", "files-root")
        self.assertEqual(rc, 0, err)
        self.assertIn("add the words: files-root", out)
        if "julos-agent.service:" in out:                         # nur mit systemd
            self.assertIn(SERVER + "/agent-files/julos-agent-helper.py", out)       # das Wort braucht den root-Helfer
            # Mit dem Wort sieht der Helfer das echte Dateisystem (sonst waere /home leer und /tmp ein anderes); die anderen Grenzen bleiben
            self.assertNotIn("ProtectHome=yes", out)
            self.assertNotIn("PrivateTmp=yes\nProtectKernelModules", out)
            self.assertIn("ProtectKernelModules=yes", out)
            self.assertIn("SocketMode=0660", out)
        rc, out, err = run("--dry-run", "--host", "--server", SERVER, "--code", CODE, "--allow", "updates")
        if "julos-agent.service:" in out:
            self.assertIn("ProtectHome=yes", out)                  # ohne das Wort bleibt die engere Sicht
        rc, out, _ = run("--help")
        self.assertIn("files-root", out)

    def test_unknown_words_are_refused_before_anything_happens(self):
        for bad in ("shell", "updates,root", "updates;id", "UPDATES", "--user"):
            rc, out, err = run("--dry-run", "--host", "--server", SERVER, "--code", CODE, "--allow", bad)
            self.assertNotEqual(rc, 0, bad)
            self.assertNotIn("[would]", out)
        self.assertNotEqual(run("--dry-run", "--host", "--server", SERVER, "--allow")[0], 0)

    def test_uninstall_dry_run_changes_nothing_and_reminds_to_remove_the_agent_in_the_app(self):
        rc, out, _ = run("--dry-run", "--host", "--uninstall")
        self.assertEqual(rc, 0)
        self.assertIn("Agent entfernen", out)
        self.assertNotIn("[do]", out)

    def test_the_server_address_and_the_code_are_checked_before_anything_happens(self):
        bad = [
            (("--server", "http://julos.example.org", "--code", CODE), "without TLS"),
            (("--server", "ftp://julos.example.org", "--code", CODE), "https://"),
            (("--server", "https://user:pw@julos.example.org", "--code", CODE), "do not belong"),
            (("--server", "https://julos.example.org/?x=1", "--code", CODE), "do not belong"),
            (("--server", "https://julos.example.org;rm -rf /", "--code", CODE), "do not belong"),
            (("--server", SERVER, "--code", "short"), "--code"),
            (("--server", SERVER, "--code", "ABCDE-FGH!J"), "--code"),
            (("--code", CODE), "--server is required"),
            (("--server", SERVER, "--user", "Bad User"), "--user"),
            (("--server", SERVER, "--user", "a;b"), "--user"),
            (("--server", SERVER, "--user", "-x"), "--user"),
            (("--bogus",), "unknown option"),
        ]
        for args, text in bad:
            rc, out, err = run("--dry-run", "--host", *args)
            self.assertNotEqual(rc, 0, args)
            self.assertIn(text, err, args)
            self.assertNotIn("[would]", out, args)

    def test_plain_http_is_only_accepted_for_localhost_or_on_request(self):
        self.assertEqual(run("--dry-run", "--host", "--server", "http://localhost:18191", "--code", CODE)[0], 0)
        self.assertEqual(run("--dry-run", "--host", "--server", "http://127.0.0.1:8080", "--code", CODE)[0], 0)
        self.assertEqual(run("--dry-run", "--host", "--server", "http://julos.example.org", "--code", CODE, "--allow-http")[0], 0)

    @unittest.skipIf(os.geteuid() == 0, "als root laeuft der echte Pfad")
    def test_without_root_a_real_run_is_refused(self):
        rc, _, err = run("--host", "--server", SERVER, "--code", CODE)
        self.assertNotEqual(rc, 0)
        self.assertIn("needs root", err)

    def test_help_lists_the_options(self):
        rc, out, _ = run("--help")
        self.assertEqual(rc, 0)
        for opt in ("--server", "--code", "--user", "--allow-updates", "--dry-run", "--uninstall", "--docker-group", "--desktop", "--host", "--systemd"):
            self.assertIn(opt, out)

    @unittest.skipUnless(shutil.which("shellcheck"), "shellcheck fehlt")
    def test_shellcheck_is_clean(self):
        for name in ("install-agent.sh", "install-host-agent.sh", "install-linux-agent.sh"):
            p = subprocess.run(["shellcheck", "-s", "sh", os.path.join(TOOLS, name)], capture_output=True, text=True)
            self.assertEqual(p.returncode, 0, p.stdout)


DESKTOP = {"JULOS_INSTALL_FAKE_DESKTOP": "1"}


class DesktopBranchTests(unittest.TestCase):
    """Der Desktop-Zweig: Agent als Benutzerprozess in der Sitzung, kein Root, kein sudo im Agenten."""

    def test_the_decision_prefers_a_desktop_when_stack_and_session_are_there(self):
        rc, out, err = run("--dry-run", "--server", SERVER, "--code", CODE, extra_env=DESKTOP)
        self.assertEqual(rc, 0, err)
        self.assertIn("Desktop detected", out)
        self.assertIn("not as root", out)
        self.assertIn("julos-agent.py --desktop", out)
        self.assertNotIn("create the system user", out)
        self.assertNotIn("julos-agent.service:", out)                    # kein Systemdienst
        self.assertIn("julos-clip.py", out)                              # der Zwischenablage-Helfer gehoert zum Desktop
        self.assertNotIn(CODE, out + err)

    def test_without_a_desktop_the_host_agent_is_chosen(self):
        rc, out, err = run("--dry-run", "--server", SERVER, "--code", CODE, extra_env={"JULOS_INSTALL_FAKE_DESKTOP": "0"})
        self.assertEqual(rc, 0, err)
        self.assertIn("host agent", out)
        self.assertIn("create the system user julos-agent", out)
        self.assertNotIn("Desktop detected", out)

    def test_forcing_the_kind_beats_the_detection(self):
        rc, out, _ = run("--dry-run", "--host", "--server", SERVER, extra_env=DESKTOP)
        self.assertEqual(rc, 0)
        self.assertIn("create the system user julos-agent", out)
        rc, out, _ = run("--dry-run", "--desktop", "--server", SERVER, extra_env={"JULOS_INSTALL_FAKE_DESKTOP": "0"})
        self.assertEqual(rc, 0)                                          # ohne Unterbau nur eine Warnung im Trockenlauf
        self.assertIn("Desktop detected", out)
        rc, _, err = run("--dry-run", "--desktop", "--host", "--server", SERVER)
        self.assertNotEqual(rc, 0)
        self.assertIn("exclude each other", err)

    def test_the_desktop_never_gets_a_service_account_a_system_unit_or_sudo(self):
        rc, out, _ = run("--dry-run", "--server", SERVER, "--code", CODE, "--allow", "updates,logs", extra_env=DESKTOP)
        self.assertEqual(rc, 0)
        for text in ("create the system user", "ProtectSystem", "User=julos-agent", "NoNewPrivileges", "SupplementaryGroups"):
            self.assertNotIn(text, out, text)
        self.assertNotIn("sudo ", out.replace("run it again without --dry-run (with sudo)", ""))     # der Agent bekommt nie sudo-Regeln oder einen sudo-Aufruf
        self.assertNotIn("/opt/julos-agent", out)
        self.assertIn("/.local/share/julos", out)                        # Dateien im Home des Benutzers
        self.assertIn("autostart", out)

    def test_words_and_the_root_helper_work_like_the_host_branch_and_the_socket_belongs_to_the_user(self):
        rc, out, err = run("--dry-run", "--server", SERVER, "--code", CODE, "--allow", "updates,logs", extra_env=DESKTOP)
        self.assertEqual(rc, 0, err)
        self.assertIn("add the words: updates logs", out)
        if "systemctl daemon-reload" in out:
            for line in ("ListenStream=/run/julos-agent-helper.sock", "SocketMode=0600", "Accept=yes", "StandardInput=socket", "root-owned"):
                self.assertIn(line, out)
            user = out.split("JulOS desktop agent for ")[1].split(":")[0]
            self.assertIn("SocketUser=" + user, out)
            self.assertIn("SocketGroup=", out)
            self.assertNotIn("SocketMode=0666", out)
            self.assertNotIn("SocketUser=root", out)
        rc, out, _ = run("--dry-run", "--server", SERVER, "--code", CODE, extra_env=DESKTOP)
        self.assertIn("with no word: the agent can only read", out)
        self.assertNotIn("add the words", out)
        self.assertNotIn("julos-agent-helper", out)                      # ohne Wort kein root-Helfer

    def test_docker_for_a_desktop_only_checks_the_group_and_never_changes_it(self):
        rc, out, err = run("--dry-run", "--server", SERVER, "--docker-group", "--allow", "docker", extra_env=DESKTOP)
        self.assertEqual(rc, 0, err)
        self.assertNotIn("usermod -aG docker\n", out)
        self.assertNotIn("SupplementaryGroups", out)
        self.assertNotIn("julos-agent-helper", out)                      # docker braucht keinen root-Helfer

    def test_the_user_service_is_an_option_and_an_existing_one_stays(self):
        rc, out, _ = run("--dry-run", "--server", SERVER, "--systemd", extra_env=DESKTOP)
        self.assertEqual(rc, 0)
        self.assertIn("systemd user service", out)
        self.assertIn("ExecStart=", out)
        self.assertIn("--desktop", out)
        rc, out, _ = run("--dry-run", "--server", SERVER, extra_env=DESKTOP)
        self.assertIn("julos-agent.desktop", out)
        self.assertNotIn("ExecStart=", out)

    def test_an_existing_desktop_installation_is_updated_in_place_without_a_new_code(self):
        with tempfile.TemporaryDirectory() as home:
            old = os.path.join(home, "oldplace")
            os.makedirs(old)
            auto = os.path.join(home, ".config", "autostart")
            os.makedirs(auto)
            with open(os.path.join(auto, "julos-agent.desktop"), "w") as f:
                f.write('[Desktop Entry]\nType=Application\nExec=/usr/bin/python3 "%s/julos-agent.py"\n' % old)
            env = {"JULOS_INSTALL_FAKE_HOME": home}
            with tempfile.TemporaryDirectory() as cwd:
                e = dict(os.environ, JULOS_INSTALL_FAKE_DESKTOP="0", **env)           # selbst ohne erkennbaren Desktop entscheidet der vorhandene Stand
                p = subprocess.run(["sh", SCRIPT, "--dry-run", "--server", SERVER], capture_output=True, text=True, cwd=cwd, env=e, timeout=60)
            self.assertEqual(p.returncode, 0, p.stderr)
            self.assertIn("a desktop agent is installed for", p.stdout)
            self.assertIn("files in " + old, p.stdout)                   # dort, wo er schon liegt
            self.assertIn("already enrolled" if os.path.exists(os.path.join(home, ".config", "julos", "agent.json")) else "no --code given and not enrolled yet", p.stdout + p.stderr)

    def test_an_existing_host_agent_decides_for_host_and_blocks_a_forced_desktop(self):
        rc, out, _ = run("--dry-run", "--server", SERVER, extra_env={**DESKTOP, "JULOS_INSTALL_FAKE_HOST": "1"})
        self.assertEqual(rc, 0)
        self.assertIn("a host agent is installed here already", out)
        self.assertIn("create the system user julos-agent", out)
        rc, out, err = run("--dry-run", "--desktop", "--server", SERVER, extra_env={**DESKTOP, "JULOS_INSTALL_FAKE_HOST": "1"})
        self.assertNotEqual(rc, 0)
        self.assertIn("One machine, one agent", err)
        self.assertNotIn("[would]", out)

    def test_a_server_is_needed_unless_the_files_lie_next_to_the_script(self):
        rc, _, err = run("--dry-run", "--desktop", extra_env=DESKTOP)            # die Dateien liegen im Checkout neben dem Skript: Weg ueber den lokalen Socket
        self.assertEqual(rc, 0, err)
        with tempfile.TemporaryDirectory() as d:
            lone = os.path.join(d, "install-agent.sh")
            shutil.copy(SCRIPT, lone)
            rc, _, err = run("--dry-run", "--desktop", script=lone, extra_env=DESKTOP)
            self.assertNotEqual(rc, 0)
            self.assertIn("--server is required", err)

    def test_the_same_checks_apply(self):
        for args, text in [(("--server", "http://julos.example.org"), "without TLS"), (("--server", SERVER, "--code", "short"), "--code"), (("--server", SERVER, "--allow", "shell"), "unknown word")]:
            rc, out, err = run("--dry-run", "--desktop", *args, extra_env=DESKTOP)
            self.assertNotEqual(rc, 0, args)
            self.assertIn(text, err)
            self.assertNotIn("[would]", out)

    def test_uninstall_dry_run_finds_what_is_installed_and_changes_nothing(self):
        rc, out, _ = run("--dry-run", "--uninstall", extra_env={**DESKTOP, "JULOS_INSTALL_FAKE_HOST": "1"})
        self.assertEqual(rc, 0)
        self.assertIn("Removing the JulOS host agent", out)
        self.assertIn("Agent entfernen", out)
        self.assertNotIn("[do]", out)
        with tempfile.TemporaryDirectory() as home:
            auto = os.path.join(home, ".config", "autostart")
            os.makedirs(auto)
            os.makedirs(os.path.join(home, "x"))
            open(os.path.join(home, "x", "julos-agent.py"), "w").close()
            with open(os.path.join(auto, "julos-agent.desktop"), "w") as f:
                f.write('Exec=/usr/bin/python3 "%s/x/julos-agent.py"\n' % home)
            rc, out, _ = run("--dry-run", "--uninstall", "--desktop", extra_env={"JULOS_INSTALL_FAKE_HOME": home})
            self.assertEqual(rc, 0)
            self.assertIn("Removing the JulOS desktop agent", out)
            self.assertIn("julos-agent.py uninstall", out)


class OldInstallersTests(unittest.TestCase):
    """install-host-agent.sh und install-linux-agent.sh sind duenne Aufrufer: bestehende Befehle brechen nicht."""

    def test_the_host_installer_forwards_with_host(self):
        rc, out, err = run("--dry-run", "--server", SERVER, "--code", CODE, "--allow", "updates", script=os.path.join(TOOLS, "install-host-agent.sh"), extra_env=DESKTOP)
        self.assertEqual(rc, 0, err)
        self.assertIn("now install-agent.sh", err)
        self.assertIn("create the system user julos-agent", out)         # trotz erkanntem Desktop: der alte Host-Befehl bleibt Host
        self.assertIn("add the words: updates", out)

    def test_the_linux_installer_forwards_with_desktop(self):
        rc, out, err = run("--dry-run", "--server", SERVER, "--code", CODE, "--allow-http", "--no-start", script=os.path.join(TOOLS, "install-linux-agent.sh"), extra_env=DESKTOP)
        self.assertEqual(rc, 0, err)
        self.assertIn("now install-agent.sh", err)
        self.assertIn("Desktop detected", out)
        self.assertNotIn("create the system user", out)

    def test_the_old_installers_pass_help_and_uninstall_on(self):
        for name in ("install-host-agent.sh", "install-linux-agent.sh"):
            rc, out, _ = run("--help", script=os.path.join(TOOLS, name))
            self.assertEqual(rc, 0)
            self.assertIn("--desktop", out)
        rc, out, _ = run("--dry-run", "--uninstall", script=os.path.join(TOOLS, "install-host-agent.sh"))
        self.assertEqual(rc, 0)
        self.assertNotIn("[do]", out)

    def test_alone_it_fetches_the_installer_from_the_server_and_checks_the_checksum(self):
        with open(SCRIPT, "rb") as f:
            body = f.read()
        served = {"/agent-files/install-agent.sh": body, "/agent-files/SHA256SUMS": ("%s  install-agent.sh\n" % hashlib.sha256(body).hexdigest()).encode()}
        asked = []

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                asked.append(self.path)
                data = served.get(self.path)
                if data is None:
                    self.send_response(404)
                    self.end_headers()
                    return
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
        httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        self.addCleanup(httpd.server_close)
        self.addCleanup(httpd.shutdown)
        url = "http://127.0.0.1:%d" % httpd.server_address[1]
        with tempfile.TemporaryDirectory() as d:
            lone = os.path.join(d, "install-host-agent.sh")                 # allein, ohne install-agent.sh daneben
            shutil.copy(os.path.join(TOOLS, "install-host-agent.sh"), lone)
            rc, out, err = run("--dry-run", "--server", url, "--code", CODE, script=lone)
            self.assertEqual(rc, 0, err)
            self.assertIn("create the system user julos-agent", out)
            self.assertEqual(sorted(asked), ["/agent-files/SHA256SUMS", "/agent-files/install-agent.sh"])
            served["/agent-files/install-agent.sh"] = body + b"\n# changed\n"          # Summe passt nicht mehr: nichts wird ausgefuehrt
            rc, out, err = run("--dry-run", "--server", url, script=lone)
            self.assertNotEqual(rc, 0)
            self.assertIn("checksum mismatch", err)
            self.assertNotIn("[would]", out)

    def test_alone_it_refuses_a_plain_http_server_and_needs_a_server(self):
        with tempfile.TemporaryDirectory() as d:
            lone = os.path.join(d, "install-linux-agent.sh")
            shutil.copy(os.path.join(TOOLS, "install-linux-agent.sh"), lone)
            rc, _, err = run("--dry-run", "--server", "http://julos.example.org", script=lone)
            self.assertNotEqual(rc, 0)
            self.assertIn("without TLS", err)
            rc, _, err = run("--dry-run", script=lone)
            self.assertNotEqual(rc, 0)
            self.assertIn("give --server", err)


if __name__ == "__main__":
    unittest.main()
