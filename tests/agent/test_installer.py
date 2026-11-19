"""Tests fuer tools/install-host-agent.sh im Trockenlauf: ohne Root, ohne Netz, ohne etwas zu veraendern (siehe docs/HOST-AGENT.md).

Der echte Lauf (Benutzer anlegen, Dienst installieren) steht im Handtest im Wegwerf-Container, nicht hier: ein Test darf die Maschine nicht veraendern.
"""
import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SCRIPT = os.path.join(ROOT, "tools", "install-host-agent.sh")
CODE = "ABCDE-FGHIJ"
SERVER = "https://julos.example.org"


def run(*args, env=None):
    with tempfile.TemporaryDirectory() as cwd:
        p = subprocess.run(["sh", SCRIPT, *args], capture_output=True, text=True, cwd=cwd, timeout=60, env=env or dict(os.environ))
        return p.returncode, p.stdout, p.stderr


class DryRunTests(unittest.TestCase):
    def test_a_dry_run_lists_every_step_and_the_unit_and_needs_no_root(self):
        rc, out, err = run("--dry-run", "--server", SERVER, "--code", CODE)
        self.assertEqual(rc, 0, err)
        for step in ("create the system user julos-agent", "/opt/julos-agent", "/var/lib/julos-agent", "SHA-256", "/etc/julos-agent/permissions", "enrol this machine", "Dry run finished"):
            self.assertIn(step, out)
        self.assertIn(SERVER + "/agent-files/julos-agent.py", out)
        self.assertIn(SERVER + "/agent-files/SHA256SUMS", out)
        self.assertNotIn(CODE, out + err)                      # der Code steht nirgends in der Ausgabe

    def test_the_unit_is_locked_down_and_starts_the_agent_in_host_mode(self):
        rc, out, _ = run("--dry-run", "--server", SERVER, "--code", CODE)
        if "julos-agent.service:" not in out:
            self.skipTest("no systemd on this machine: the unit is not shown")
        for line in ("User=julos-agent", "NoNewPrivileges=yes", "ProtectSystem=strict", "ProtectHome=read-only", "PrivateTmp=yes", "CapabilityBoundingSet=", "Restart=on-failure",
                     "ReadWritePaths=/var/lib/julos-agent /opt/julos-agent", "--host"):
            self.assertIn(line, out)
        self.assertNotIn("SupplementaryGroups", out)             # kein docker-Zugriff ohne ausdrueckliche Wahl
        self.assertNotIn("--allow-http", out)

    def test_options_show_up(self):
        rc, out, _ = run("--dry-run", "--server", SERVER, "--code", CODE, "--allow-updates", "--docker-group", "--user", "jagent")
        self.assertEqual(rc, 0)
        self.assertIn('with the line "updates"', out)
        self.assertIn("jagent", out)
        if "julos-agent.service:" in out:
            self.assertIn("SupplementaryGroups=docker", out)

    def test_uninstall_dry_run_changes_nothing_and_reminds_to_remove_the_agent_in_the_app(self):
        rc, out, _ = run("--dry-run", "--uninstall")
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
            (("--server", SERVER, "--user", "-x"), "--user"),
            (("--bogus",), "unknown option"),
        ]
        for args, text in bad:
            rc, out, err = run("--dry-run", *args)
            self.assertNotEqual(rc, 0, args)
            self.assertIn(text, err, args)
            self.assertNotIn("[would]", out, args)

    def test_plain_http_is_only_accepted_for_localhost_or_on_request(self):
        self.assertEqual(run("--dry-run", "--server", "http://localhost:18191", "--code", CODE)[0], 0)
        self.assertEqual(run("--dry-run", "--server", "http://127.0.0.1:8080", "--code", CODE)[0], 0)
        self.assertEqual(run("--dry-run", "--server", "http://julos.example.org", "--code", CODE, "--allow-http")[0], 0)

    @unittest.skipIf(os.geteuid() == 0, "als root laeuft der echte Pfad")
    def test_without_root_a_real_run_is_refused(self):
        rc, _, err = run("--server", SERVER, "--code", CODE)
        self.assertNotEqual(rc, 0)
        self.assertIn("needs root", err)

    def test_help_lists_the_options(self):
        rc, out, _ = run("--help")
        self.assertEqual(rc, 0)
        for opt in ("--server", "--code", "--user", "--allow-updates", "--dry-run", "--uninstall", "--docker-group"):
            self.assertIn(opt, out)

    @unittest.skipUnless(shutil.which("shellcheck"), "shellcheck fehlt")
    def test_shellcheck_is_clean(self):
        p = subprocess.run(["shellcheck", "-s", "sh", SCRIPT], capture_output=True, text=True)
        self.assertEqual(p.returncode, 0, p.stdout)


if __name__ == "__main__":
    unittest.main()
