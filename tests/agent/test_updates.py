"""Tests fuer robuste Host-Updates: Fehlerzusammenfassung, Reparieren, Proxmox (dist-upgrade), Kernel-Neustart, ZFS und SMART (docs/HOST-AGENT.md).

Hier laeuft NIE ein echtes apt, dpkg, smartctl, lsblk oder zpool: der Helfer bekommt einen einsetzbaren Runner, der Agent einen Helfer-Recorder (test_actions.py).
    python3 -m unittest discover -s tests/agent
"""
import unittest
from unittest import mock

from test_actions import AgentActionBase, JOB, boom, helper, ALL_RIGHTS
from test_host import ZPOOL_BAD, agent

# Das echte Fehlerbild eines halb aktualisierten Proxmox-Hosts (apt-get upgrade statt dist-upgrade)
PVE_FAILURE = [
    "Setting up proxmox-kernel-6.17.2-1-pve-signed (6.17.2-1) ...",
    "Setting up libpve-common-perl (9.0.11) ...",
    "dpkg: error processing package pve-manager (--configure):",
    " dependency problems - leaving unconfigured",
    "Errors were encountered while processing:",
    " proxmox-mail-forward",
    " pve-manager",
    "E: Sub-process /usr/bin/dpkg returned an error code (1)",
]
AUDIT_BROKEN = [
    "The following packages are only half configured, probably due to problems",
    "configuring them the first time. The configuration should be retried using",
    "dpkg --configure <package> or the configure menu option in dselect:",
    " pve-manager          Proxmox Virtual Environment Management Tools",
    " proxmox-mail-forward Proxmox mail forward helper",
]


class ZpoolTests(unittest.TestCase):
    def test_healthy_and_no_pools_are_not_problems(self):
        self.assertEqual(agent.parse_zpool_status("all pools are healthy\n"), {"state": "healthy", "healthy": True, "pools": []})
        none = agent.parse_zpool_status("no pools available\n")
        self.assertEqual((none["state"], none["healthy"], none["pools"]), ("none", True, []))

    def test_a_listed_pool_shows_name_state_scan_and_errors(self):
        text = ZPOOL_BAD.replace("config:", "  scan: scrub repaired 0B in 00:10:01 with 0 errors on Sun Nov 15 00:34:02 2026\nerrors: 3 data errors, use '-v' for a list\nconfig:")
        r = agent.parse_zpool_status(text)
        self.assertEqual((r["state"], r["healthy"]), ("problem", False))
        self.assertEqual(r["pools"][0]["name"], "tank")
        self.assertEqual(r["pools"][0]["state"], "DEGRADED")
        self.assertIn("scrub repaired", r["pools"][0]["scan"])
        self.assertIn("3 data errors", r["pools"][0]["errors"])
        self.assertIn("could not be opened", r["pools"][0]["status"])

    def test_two_pools_and_an_online_pool_with_errors(self):
        text = "  pool: a\n state: FAULTED\n\n  pool: b\n state: ONLINE\nerrors: 1 data errors\n"
        r = agent.parse_zpool_status(text)
        self.assertEqual([(p["name"], p["state"]) for p in r["pools"]], [("a", "FAULTED"), ("b", "ONLINE")])

    def test_unreadable_zfs_is_unknown_never_a_pool_named_question_mark(self):
        for text, rc in (("The ZFS modules are not loaded.\nTry running '/sbin/modprobe zfs' as root to load them.\n", 1), ("failed to initialize ZFS library\n", 1),
                         ("cannot open '/dev/zfs': Permission denied\n", 1), ("something nobody has seen before\n", 0), ("", 1)):
            with self.subTest(text=text):
                r = agent.parse_zpool_status(text, rc)
                self.assertEqual((r["state"], r["pools"]), ("unknown", []))
                self.assertTrue(0 < len(r["message"]) <= 120)
        # eine Fehlermeldung MIT Rueckgabewert 1, die zufaellig 'pool:' enthaelt, zaehlt nicht als Pool
        self.assertEqual(agent.parse_zpool_status("pool: x\n state: ONLINE\n", 1)["state"], "unknown")

    def test_collect_zfs_uses_return_code_and_stderr(self):
        def fake(rc, out, err):
            return mock.patch.object(agent, "run_cmd", lambda argv, timeout=30: (rc, out, err))
        with mock.patch.object(agent.shutil, "which", lambda t: "/sbin/zpool"):
            with fake(1, "", "The ZFS modules are not loaded.\n"):
                self.assertEqual(agent.collect_zfs()["state"], "unknown")
            with fake(0, "no pools available\n", ""):
                self.assertEqual(agent.collect_zfs()["state"], "none")
            with mock.patch.object(agent, "run_cmd", lambda argv, timeout=30: None):
                self.assertEqual(agent.collect_zfs()["state"], "unknown")
        with mock.patch.object(agent.shutil, "which", lambda t: None):
            self.assertIsNone(agent.collect_zfs())


class SummaryTests(unittest.TestCase):
    def test_the_real_proxmox_failure(self):
        s = agent.summarize_apt(PVE_FAILURE)
        self.assertEqual(s["failedPackages"], ["pve-manager", "proxmox-mail-forward"])
        self.assertEqual(s["errors"], ["E: Sub-process /usr/bin/dpkg returned an error code (1)"])
        self.assertEqual((s["installed"], s["locked"], s["configQuestion"], s["keptBack"]), (2, False, False, []))

    def test_the_failure_as_one_flat_line_from_a_terminal_excerpt(self):
        s = agent.summarize_apt(["Errors were encountered while processing: proxmox-mail-forward pve-manager", "E: Sub-process /usr/bin/dpkg returned an error code (1)"])
        self.assertEqual(s["failedPackages"], ["proxmox-mail-forward", "pve-manager"])

    def test_kept_back_lists_every_name_on_the_indented_lines(self):
        s = agent.summarize_apt(["The following packages have been kept back:", "  linux-image-amd64 proxmox-ve", "  python3-held", "0 upgraded, 0 newly installed"])
        self.assertEqual(s["keptBack"], ["linux-image-amd64", "proxmox-ve", "python3-held"])

    def test_dpkg_audit_output_names_the_half_configured_packages(self):
        s = agent.summarize_apt(AUDIT_BROKEN)
        self.assertEqual(s["failedPackages"], ["pve-manager", "proxmox-mail-forward"])

    def test_lock_and_conffile_question(self):
        s = agent.summarize_apt(["E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 123 (apt)", "E: Unable to acquire the dpkg frontend lock"])
        self.assertTrue(s["locked"])
        self.assertEqual(len(s["errors"]), 2)
        self.assertTrue(agent.summarize_apt(["Configuration file '/etc/ssh/sshd_config'", " ==> Modified (by you or by a script) since installation."])["configQuestion"])

    def test_hostile_text_stays_text_and_package_names_are_validated(self):
        s = agent.summarize_apt(["Errors were encountered while processing:", " x; rm -rf /", " $(id)", " ok-pkg", "E: " + "A" * 500] + ["E: n%d" % i for i in range(30)])
        self.assertEqual(s["failedPackages"], ["ok-pkg"])
        self.assertLessEqual(len(s["errors"]), 8)
        self.assertTrue(all(len(e) <= 200 for e in s["errors"]))

    def test_log_is_bounded_and_keeps_indentation(self):
        r = {"ok": False, "code": "dpkg_failed", "log": [" x" * 300] * 3000, "output": "o"}
        status, out = agent.from_helper(r)
        self.assertEqual(status, 500)
        self.assertLessEqual(len(out["log"]), agent.LOG_KEEP_LINES)
        self.assertTrue(all(len(l) <= agent.LOG_KEEP_WIDTH for l in out["log"]))
        self.assertTrue(out["log"][0].startswith(" x"))
        status, out = agent.from_helper({"ok": False, "code": "dpkg_failed", "log": PVE_FAILURE})
        self.assertTrue(out["needsRepair"])
        status, out = agent.from_helper({"ok": False, "code": "apt_failed", "log": ["E: Unable to locate package x"]})
        self.assertNotIn("needsRepair", out)
        status, out = agent.from_helper({"ok": True, "log": ["Setting up a ..."]})
        self.assertEqual((status, out["summary"]["installed"]), (200, 1))


class KernelAndProxmoxTests(unittest.TestCase):
    def test_a_newer_installed_kernel_of_the_same_kind_needs_a_reboot(self):
        self.assertEqual(agent.kernel_pending("6.14.11-4-pve", ["6.14.11-4-pve", "6.17.2-1-pve"]), "6.17.2-1-pve")
        self.assertIsNone(agent.kernel_pending("6.17.2-1-pve", ["6.14.11-4-pve", "6.17.2-1-pve"]))
        self.assertEqual(agent.kernel_pending("6.12.57+deb13-amd64", ["6.12.57+deb13-amd64", "6.12.63+deb13-amd64"]), "6.12.63+deb13-amd64")
        self.assertIsNone(agent.kernel_pending("6.12.57+deb13-amd64", ["6.12.57+deb13-amd64", "6.17.2-1-pve"]))       # andere Art: nicht vergleichbar
        self.assertEqual(agent.kernel_pending("6.9.0-2-pve", ["6.10.0-1-pve", "6.17.2-1-pve"]), "6.17.2-1-pve")        # numerisch, nicht als Text
        self.assertIsNone(agent.kernel_pending("6.9.0-2-pve", []))

    def test_installed_kernels_come_from_file_names_in_boot(self):
        import os
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            for n in ("vmlinuz-6.17.2-1-pve", "vmlinuz-6.14.11-4-pve", "initrd.img-6.17.2-1-pve", "vmlinuz-../../etc"):
                open(os.path.join(d, n.replace("/", "_")), "w").close()
            self.assertEqual(sorted(agent.read_installed_kernels(d)), ["6.14.11-4-pve", "6.17.2-1-pve"])
        self.assertEqual(agent.read_installed_kernels("/nonexistent"), [])

    def test_sysinfo_reboot_also_for_a_new_kernel_without_the_flag_file(self):
        with mock.patch.object(agent, "read_installed_kernels", lambda *a: ["99.0.0-1-" + agent.os.uname().release.rsplit("-", 1)[-1]]), \
                mock.patch.object(agent, "health_snapshot", lambda: {"pending": True}), mock.patch.object(agent, "detect_proxmox", lambda e=None: None):
            info = agent.collect_sysinfo(exists=lambda p: False)
        self.assertTrue(info["rebootRequired"])
        self.assertTrue(info["newKernel"].startswith("99.0.0"))

    def test_proxmox_is_the_package_or_etc_pve(self):
        self.assertTrue(agent.is_proxmox(exists=lambda p: p == "/etc/pve", run=boom))
        yes = lambda argv, timeout=30: (0, "install ok installed", "")
        no = lambda argv, timeout=30: (1, "", "no packages found")
        self.assertTrue(agent.is_proxmox(exists=lambda p: False, run=yes))
        self.assertFalse(agent.is_proxmox(exists=lambda p: False, run=no))
        self.assertFalse(agent.is_proxmox(exists=lambda p: False, run=lambda *a, **k: None))

    def test_kept_back_by_upgrade_is_read_from_a_second_simulation(self):
        dist = "Inst linux-image-amd64 [1] (2 Debian-Security:13/stable-security [amd64])\nInst bash [1] (2 Debian:13/stable [amd64])\nInst linux-image-6.12 (2 Debian [amd64])\n"
        up = "The following packages have been kept back:\n  linux-image-amd64\n0 upgraded, 0 newly installed, 0 to remove and 1 not upgraded.\n"

        def fake(argv, timeout=30):
            if "dist-upgrade" in argv:
                return 0, dist, ""
            if "upgrade" in argv:
                return 0, up, ""
            if argv[0] == "apt-mark":
                return 0, "", ""
        with mock.patch.object(agent, "run_cmd", fake), mock.patch.object(agent, "is_proxmox", lambda *a, **k: True):
            status, r = agent.collect_updates_apt()
        self.assertEqual((status, r["keptBack"], r["proxmox"], r["newPackages"]), (200, ["linux-image-amd64"], True, 1))


class HelperRunner:
    """Ein einsetzbarer Runner fuer den Helfer: merkt jeden Argumentvektor, antwortet aus einer Tabelle (Praefix -> (rc, Zeilen)); sonst (0, [])."""

    def __init__(self, table=None):
        self.calls, self.table = [], table or {}

    def __call__(self, argv, on_line, timeout):
        self.calls.append(list(argv))
        for prefix, (rc, lines) in self.table.items():
            if list(argv[:len(prefix)]) == list(prefix):
                for l in lines:
                    on_line(l)
                return rc, list(lines)
        return 0, []


class HelperUpdateTests(unittest.TestCase):
    def make(self, table=None, rights=ALL_RIGHTS | {"disks"}):
        self.runner = HelperRunner(table)
        self.sent = []
        return helper.Helper(run=self.runner, sleep=lambda s: None, permissions=lambda: set(rights))

    def test_argument_vectors_for_upgrade_dist_upgrade_and_repair(self):
        h = self.make()
        common = ["-y", "-o", "Dpkg::Options::=--force-confold", "-o", "Dpkg::Options::=--force-confdef", "-o", "DPkg::Lock::Timeout=60"]
        self.assertTrue(h.handle({"verb": "apt.upgrade"}, self.sent.append)["ok"])
        self.assertEqual(self.runner.calls, [["dpkg", "--audit"], ["apt-get", "upgrade"] + common])
        self.runner.calls.clear()
        self.assertTrue(h.handle({"verb": "apt.upgrade", "full": True}, self.sent.append)["ok"])
        self.assertEqual(self.runner.calls[-1], ["apt-get", "dist-upgrade"] + common)
        self.runner.calls.clear()
        self.assertTrue(h.handle({"verb": "apt.repair"}, self.sent.append)["ok"])
        self.assertEqual(self.runner.calls[:2], [["dpkg", "--configure", "-a", "--force-confold", "--force-confdef"],
                                                 ["apt-get", "-f", "install", "-y", "-o", "Dpkg::Options::=--force-confold", "-o", "Dpkg::Options::=--force-confdef", "-o", "DPkg::Lock::Timeout=60"]])
        self.assertEqual(self.runner.calls[2], ["dpkg", "--audit"])               # danach prueft er noch einmal, ob alles in Ordnung ist
        self.assertTrue(all(isinstance(a, str) for c in self.runner.calls for a in c))

    def test_full_must_be_a_boolean_and_never_goes_with_single_packages(self):
        h = self.make()
        for req in ({"verb": "apt.upgrade", "full": "yes"}, {"verb": "apt.upgrade", "full": 1}, {"verb": "apt.upgrade", "full": True, "packages": ["bash"]}):
            self.assertEqual(h.handle(req, self.sent.append), {"ok": False, "code": "invalid_request"}, req)
        self.assertEqual(self.runner.calls, [])

    def test_a_broken_package_database_refuses_every_upgrade_and_offers_repair(self):
        h = self.make({("dpkg", "--audit"): (0, AUDIT_BROKEN)})
        for req in ({"verb": "apt.upgrade"}, {"verb": "apt.upgrade", "full": True}, {"verb": "apt.upgrade", "packages": ["bash"]}):
            r = h.handle(req, self.sent.append)
            self.assertEqual((r["ok"], r["code"]), (False, "dpkg_broken"))
            self.assertIn("pve-manager", r["output"])
            self.assertTrue(r["log"][3].startswith(" pve-manager"))                  # Einrueckung bleibt
        self.assertTrue(all(c == ["dpkg", "--audit"] for c in self.runner.calls))     # kein apt-get lief
        # die Reparatur selbst wird nicht abgelehnt
        h.handle({"verb": "apt.repair"}, self.sent.append)
        self.assertEqual(self.runner.calls[-3][:3], ["dpkg", "--configure", "-a"])

    def test_the_package_lists_can_be_refreshed_even_when_dpkg_is_broken(self):
        h = self.make({("dpkg", "--audit"): (0, AUDIT_BROKEN)})
        self.assertTrue(h.handle({"verb": "apt.refresh"}, self.sent.append)["ok"])
        self.assertEqual(self.runner.calls[0][:2], ["apt-get", "update"])

    def test_the_real_failure_gets_dpkg_failed_with_the_whole_log(self):
        h = self.make({("apt-get", "dist-upgrade"): (100, PVE_FAILURE)})
        r = h.handle({"verb": "apt.upgrade", "full": True}, self.sent.append)
        self.assertEqual((r["ok"], r["code"], r["exit"]), (False, "dpkg_failed", 100))
        self.assertEqual(r["log"], PVE_FAILURE)
        self.assertIn("Errors were encountered", r["output"])
        self.assertEqual(self.sent[0], {"p": PVE_FAILURE[0]})                           # jede Zeile geht auch als Fortschritt hinaus

    def test_the_other_failure_codes(self):
        lock = self.make({("apt-get", "upgrade"): (100, ["E: Could not get lock /var/lib/dpkg/lock-frontend"])})
        self.assertEqual(lock.handle({"verb": "apt.upgrade"}, self.sent.append)["code"], "apt_locked")
        plain = self.make({("apt-get", "upgrade"): (100, ["E: Unable to correct problems, you have held broken packages."])})
        self.assertEqual(plain.handle({"verb": "apt.upgrade"}, self.sent.append)["code"], "apt_failed")
        dpkg = self.make({("dpkg", "--configure"): (1, ["dpkg: error processing package pve-manager (--configure):"])})
        r = dpkg.handle({"verb": "apt.repair"}, self.sent.append)
        self.assertEqual((r["ok"], r["code"]), (False, "dpkg_failed"))
        self.assertEqual(self.runner.calls[1][:2], ["apt-get", "-f"])                  # die zweite Stufe lief trotzdem
        missing = helper.Helper(run=lambda a, o, t: (None, []), sleep=lambda s: None, permissions=lambda: ALL_RIGHTS)
        self.assertEqual(missing.handle({"verb": "apt.upgrade"}, lambda x: None)["code"], "not_installed")
        timeout = self.make({("apt-get", "upgrade"): (-1, ["working"])})
        self.assertEqual(timeout.handle({"verb": "apt.upgrade"}, self.sent.append)["code"], "timeout")

    def test_repair_that_leaves_dpkg_broken_says_so(self):
        h = self.make({("dpkg", "--audit"): (0, AUDIT_BROKEN)})
        r = h.handle({"verb": "apt.repair"}, self.sent.append)
        self.assertEqual((r["ok"], r["code"]), (False, "dpkg_broken"))

    def test_the_log_is_bounded_in_lines_width_and_bytes(self):
        lines = ["x" * 1000] * 5000
        out = helper.limit_log(lines)
        self.assertLessEqual(len(out), helper.LOG_LINES)
        self.assertTrue(all(len(l) <= helper.LOG_WIDTH for l in out))
        self.assertLessEqual(sum(len(l) + 1 for l in out), helper.LOG_BYTES)
        self.assertEqual(helper.limit_log([" a", "b\x07c "]), [" a", "bc"])

    def test_no_word_no_repair_and_no_smart(self):
        for verb, need in (("apt.repair", "updates"), ("smart.health", "disks")):
            h = self.make(rights=(ALL_RIGHTS | {"disks"}) - {need})
            self.assertEqual(h.handle({"verb": verb}, self.sent.append), {"ok": False, "code": "not_permitted"})
        self.assertEqual(self.runner.calls, [])
        self.assertEqual(helper.VERB_RIGHT["smart.health"], "disks")
        self.assertEqual(helper.VERB_RIGHT["apt.repair"], "updates")
        self.assertIn("disks", helper.RIGHTS)

    def test_smart_health_asks_only_for_disks_the_helper_found_itself(self):
        lsblk = ["sda disk", "sda1 part", "nvme0n1 disk", "sr0 rom", "evil;id disk", "-x disk", "../x disk", "dm-0 lvm"]
        smart = {"sda": {"smart_status": {"passed": True}, "temperature": {"current": 34}}, "nvme0n1": {"smart_status": {"passed": False}}}
        import json

        def run(argv, on_line, timeout):
            self.calls.append(list(argv))
            if argv[0] == "lsblk":
                for l in lsblk:
                    on_line(l)
                return 0, lsblk
            data = json.dumps(smart[argv[-1].split("/")[-1]], indent=2)
            for l in data.splitlines():
                on_line(l)
            return 4, []                                         # smartctl: Bitmaske, nicht null, sagt nichts ueber die Gesundheit
        self.calls = []
        h = helper.Helper(run=run, sleep=lambda s: None, permissions=lambda: {"disks"})
        r = h.handle({"verb": "smart.health", "devices": ["/dev/../../etc/passwd"], "argv": ["id"]}, lambda x: None)
        self.assertEqual(r, {"ok": True, "devices": [{"name": "sda", "health": "ok", "temp": 34}, {"name": "nvme0n1", "health": "failing"}]})
        self.assertEqual(self.calls, [["lsblk", "-dn", "-o", "NAME,TYPE"], ["smartctl", "-H", "-A", "-j", "/dev/sda"], ["smartctl", "-H", "-A", "-j", "/dev/nvme0n1"]])

    def test_smart_health_without_smartctl_or_with_garbage(self):
        h = helper.Helper(run=lambda a, o, t: (None, []), sleep=lambda s: None, permissions=lambda: {"disks"})
        self.assertEqual(h.handle({"verb": "smart.health"}, lambda x: None), {"ok": False, "code": "not_installed"})

        def run(argv, on_line, timeout):
            if argv[0] == "lsblk":
                on_line("sda disk")
                return 0, []
            on_line("not json")
            return 0, []
        h = helper.Helper(run=run, sleep=lambda s: None, permissions=lambda: {"disks"})
        self.assertEqual(h.handle({"verb": "smart.health"}, lambda x: None), {"ok": True, "devices": [{"name": "sda", "health": "unknown"}]})
        self.assertEqual(helper.parse_smart_json("[1]", "sda"), {"name": "sda", "health": "unknown"})
        self.assertEqual(helper.parse_smart_json('{"temperature": {"current": 9999}, "smart_status": {"passed": "yes"}}', "sda"), {"name": "sda", "health": "unknown"})


class SmartViaHelperTests(AgentActionBase):
    def test_with_the_word_disks_the_agent_asks_the_helper_and_without_it_nothing_changes(self):
        self.rights = {"disks"}
        self.rec.helper_reply = {"ok": True, "devices": [{"name": "sda", "health": "ok", "temp": 31}, {"name": "../x", "health": "ok"}, {"name": "sdb", "health": "weird"}]}
        r = agent.collect_smart()
        self.assertEqual(self.rec.helper, [{"verb": "smart.health"}])
        self.assertEqual(r, {"available": True, "devices": [{"name": "/dev/sda", "health": "ok", "temp": 31}, {"name": "/dev/sdb", "health": "unknown"}]})
        self.rec.helper_reply = {"ok": False, "code": "not_installed"}
        self.assertEqual(agent.collect_smart(), {"available": False, "reason": "no_smartctl"})
        self.rec.helper_reply = {"ok": False, "code": "not_permitted"}
        self.assertEqual(agent.collect_smart(), {"available": False, "reason": "read_failed"})
        self.rec.helper_reply = {"ok": True, "devices": []}
        self.assertEqual(agent.collect_smart(), {"available": False, "reason": "no_devices"})
        with mock.patch.object(agent, "helper_call", lambda *a, **k: None):
            self.assertEqual(agent.collect_smart(), {"available": False, "reason": "helper_unavailable"})
        # ohne das Wort: der Helfer wird nie gefragt
        self.rec.helper.clear()
        self.rights = set()
        with mock.patch.object(agent.shutil, "which", lambda t: None):
            self.assertEqual(agent.collect_smart(), {"available": False, "reason": "no_smartctl"})
        self.assertEqual(self.rec.helper, [])


class UpdateActionTests(AgentActionBase):
    def updates(self, proxmox=False, **extra):
        return mock.patch.object(agent, "collect_updates", lambda: (200, {"packages": [{"name": "libc6"}, {"name": "pve-manager"}], "count": 2, "proxmox": proxmox, **extra}))

    def test_on_proxmox_all_updates_is_a_dist_upgrade_without_asking(self):
        with self.updates(proxmox=True):
            status, res = self.post("/v1/host/updates/install", all=True)
        self.assertEqual(status, 200)
        self.assertEqual(self.rec.helper, [{"verb": "apt.upgrade", "full": True}])
        self.assertTrue(res["full"])

    def test_on_proxmox_single_packages_are_refused_before_anything_runs(self):
        with self.updates(proxmox=True):
            status, res = self.post("/v1/host/updates/install", packages=["libc6"])
        self.assertEqual((status, res["error"]), (409, "proxmox_whole"))
        self.nothing_ran()

    def test_elsewhere_full_upgrade_only_on_request_and_only_for_all(self):
        with self.updates():
            self.post("/v1/host/updates/install", all=True)
            self.assertEqual(self.rec.helper[-1], {"verb": "apt.upgrade"})
        self.setUp()
        with self.updates():
            self.post("/v1/host/updates/install", all=True, full=True)
            self.assertEqual(self.rec.helper[-1], {"verb": "apt.upgrade", "full": True})
        self.setUp()
        with self.updates():
            for n, body in enumerate(({"packages": ["libc6"], "full": True}, {"all": True, "full": "yes"})):
                status, res = self.post("/v1/host/updates/install", job="%032x" % (n + 1), **body)
                self.assertEqual((status, res["error"]), (400, "invalid_request"))
        self.nothing_ran()

    def test_the_failure_comes_back_with_summary_log_and_the_offer_to_repair(self):
        self.rec.helper_reply = {"ok": False, "code": "dpkg_failed", "exit": 100, "output": "\n".join(PVE_FAILURE[-4:]), "log": PVE_FAILURE}
        with self.updates(proxmox=True):
            status, res = self.post("/v1/host/updates/install", all=True)
        self.assertEqual((status, res["error"], res["needsRepair"]), (500, "dpkg_failed", True))
        self.assertEqual(res["summary"]["failedPackages"], ["pve-manager", "proxmox-mail-forward"])
        self.assertEqual(res["log"], PVE_FAILURE)
        stored = agent.job_store().snapshot()[0]["result"]
        self.assertEqual(stored["summary"]["failedPackages"], ["pve-manager", "proxmox-mail-forward"])

    def test_the_stored_job_keeps_only_the_end_of_a_long_log(self):
        self.rec.helper_reply = {"ok": True, "log": ["line %d" % i for i in range(1500)]}
        with self.updates():
            self.post("/v1/host/updates/install", all=True)
        stored = agent.job_store().snapshot()[0]["result"]
        self.assertEqual(len(stored["log"]), agent.LOG_STORE_LINES)
        self.assertEqual(stored["log"][-1], "line 1499")

    def test_repair_sends_exactly_one_fixed_verb_and_needs_the_updates_word(self):
        status, res = self.post("/v1/host/updates/repair")
        self.assertEqual((status, res["ok"]), (200, True))
        self.assertEqual(self.rec.helper, [{"verb": "apt.repair"}])
        self.setUp()
        self.rights = ALL_RIGHTS - {"updates"}
        status, res = self.post("/v1/host/updates/repair")
        self.assertEqual((status, res["error"]), (403, "not_permitted"))
        self.nothing_ran()

    def test_repair_is_apt_only_and_a_job_like_any_other(self):
        with mock.patch.object(agent, "update_manager", lambda which=None: "dnf"):
            status, res = self.post("/v1/host/updates/repair")
        self.assertEqual((status, res["error"]), (501, "not_implemented"))
        self.nothing_ran()

    def test_progress_carries_the_lines_since_the_last_message(self):
        sent, clock = [], iter([100.0, 100.1, 100.2, 101.0, 101.1])
        ctx = agent.Ctx(JOB, lambda text, done=None, total=None, lines=None: sent.append((text, lines)))
        agent.job_store().start(JOB, "updates.install", "all")
        with mock.patch.object(agent.time, "time", lambda: next(clock)):
            for line in ("one", "two", "three", "four"):
                ctx.line(line)
        self.assertEqual(sent, [("one", ["one"]), ("four", ["two", "three", "four"])])        # zweite Meldung buendelt alles seit der ersten

    def test_reboot_needed_after_the_action_includes_a_new_kernel(self):
        with mock.patch.object(agent, "reboot_required", lambda *a: False), mock.patch.object(agent, "read_installed_kernels", lambda *a: ["99.0.0-1-" + agent.os.uname().release.rsplit("-", 1)[-1]]):
            self.assertTrue(agent.reboot_needed())
        with mock.patch.object(agent, "reboot_required", lambda *a: False), mock.patch.object(agent, "read_installed_kernels", lambda *a: []):
            self.assertFalse(agent.reboot_needed())


if __name__ == "__main__":
    unittest.main()
