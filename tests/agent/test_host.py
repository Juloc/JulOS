"""Tests fuer den Host-Modus des Agenten (tools/julos-agent.py): Parser, Systemwerte, Rechte, Anfragen und die Verbindung zum Server.

Ohne Anzeige, ohne PyGObject, ohne Root lauffaehig:  python3 -m unittest discover -s tests/agent
Alle Beispieladressen stammen aus den Dokumentationsbereichen (192.0.2.0/24, 2001:db8::/32); keine echten Adressen oder Namen.
"""
import argparse
import base64
import hashlib
import http.server
import importlib.util
import json
import os
import socket
import socketserver
import stat
import struct
import tempfile
import threading
import time
import unittest
from unittest import mock

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
AGENT_FILE = os.path.join(ROOT, "tools", "julos-agent.py")


def load_agent():
    spec = importlib.util.spec_from_file_location("julos_agent_host_under_test", AGENT_FILE)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


agent = load_agent()

# ---- Fixtures: so sieht die Ausgabe der Programme aus (LC_ALL=C)
APT_SIM = """NOTE: This is only a simulation!
      apt-get needs root privileges for real execution.
      Keep also in mind that locking is deactivated,
      so don't depend on the relevance to the real current situation!
Reading package lists...
Building dependency tree...
Reading state information...
Calculating upgrade...
The following packages have been kept back:
  linux-image-amd64 python3-held
The following NEW packages will be installed:
  linux-image-6.12.0-2-amd64
The following packages will be upgraded:
  bash libc6 openssl tzdata
4 upgraded, 1 newly installed, 0 to remove and 2 not upgraded.
Inst bash [5.2.15-2+b2] (5.2.15-2+b7 Debian:12.5/stable [amd64])
Inst libc6 [2.36-9+deb12u3] (2.36-9+deb12u4 Debian-Security:12/stable-security [amd64]) []
Inst openssl [3.0.11-1~deb12u1] (3.0.13-1~deb12u1 Debian:12.5/stable, Debian-Security:12/stable-security [amd64])
Inst tzdata [2024a-0+deb12u1] (2024b-0+deb12u1 Debian:12.5/stable [all])
Inst linux-image-6.12.0-2-amd64 (6.12.5-1 Debian:12.5/stable [amd64])
Conf bash (5.2.15-2+b7 Debian:12.5/stable [amd64])
Conf libc6 (2.36-9+deb12u4 Debian-Security:12/stable-security [amd64])
Remv oldlib [1.0-1]
"""

APT_UBUNTU = """Reading package lists...
Inst libssl3 [3.0.2-0ubuntu1.14] (3.0.2-0ubuntu1.15 Ubuntu:22.04/jammy-updates, Ubuntu:22.04/jammy-security [amd64])
Inst vim [2:8.2.3995-1ubuntu2.15] (2:8.2.3995-1ubuntu2.16 UbuntuESM:22.04/jammy-infra-security [amd64])
Inst nano [6.2-1] (6.2-1ubuntu0.1 Ubuntu:22.04/jammy-updates [amd64])
"""

APT_NOTHING = """Reading package lists...
Building dependency tree...
Reading state information...
Calculating upgrade...
0 upgraded, 0 newly installed, 0 to remove and 0 not upgraded.
"""

IP_ADDR = """1: lo    inet 127.0.0.1/8 scope host lo\\       valid_lft forever preferred_lft forever
2: eth0    inet 192.0.2.10/24 brd 192.0.2.255 scope global dynamic eth0\\       valid_lft 80000sec preferred_lft 80000sec
2: eth0    inet6 2001:db8::10/64 scope global dynamic \\       valid_lft 80000sec preferred_lft 14000sec
2: eth0    inet6 fe80::1/64 scope link \\       valid_lft forever preferred_lft forever
5: veth1234@if4    inet 198.51.100.9/24 scope global veth1234\\       valid_lft forever preferred_lft forever
"""

FIB_TRIE = """Main:
  +-- 0.0.0.0/0 3 0 5
     |-- 0.0.0.0
        /0 universe UNICAST
     +-- 127.0.0.0/8 2 0 2
        +-- 127.0.0.0/31 1 0 0
           |-- 127.0.0.0
              /32 link BROADCAST
              /8 host LOCAL
           |-- 127.0.0.1
              /32 host LOCAL
     +-- 192.0.2.0/24 2 0 1
        |-- 192.0.2.10
           /32 host LOCAL
        |-- 192.0.2.255
           /32 link BROADCAST
"""

MEMINFO = """MemTotal:        2048000 kB
MemFree:           100000 kB
MemAvailable:     1024000 kB
Buffers:            50000 kB
SwapTotal:        512000 kB
SwapFree:         256000 kB
"""

OS_RELEASE = '''PRETTY_NAME="Debian GNU/Linux 12 (bookworm)"
NAME="Debian GNU/Linux"
VERSION_ID="12"
ID=debian
HOME_URL="https://www.debian.org/"
'''


NETDEV = """Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo:  500 5 0 0 0 0 0 0  500 5 0 0 0 0 0 0
  eth0: 1000 10 0 0 0 0 0 0 2000 20 0 0 0 0 0 0
veth12: 7 1 0 0 0 0 0 0 7 1 0 0 0 0 0 0
  eth1: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
"""

MDSTAT = """Personalities : [raid1] [raid6]
md0 : active raid1 sdb1[1] sda1[0]
      1048512 blocks super 1.2 [2/2] [UU]

md1 : active raid1 sdc1[0]
      2096064 blocks super 1.2 [2/1] [U_]

md2 : active raid1 sde1[2] sdd1[0]
      2096064 blocks super 1.2 [2/1] [U_]
      [==>..................]  recovery = 12.6% (264192/2096064) finish=1.1min speed=26419K/sec

md3 : inactive sdf1[0](S)
      1048512 blocks super 1.2

unused devices: <none>
"""

ZPOOL_BAD = """  pool: tank
 state: DEGRADED
status: One or more devices could not be opened.
config:

	NAME        STATE     READ WRITE CKSUM
	tank        DEGRADED     0     0     0
"""


class Fake:
    """Ersetzt run_cmd: antwortet je nach Programm mit festen Ausgaben (None = Programm nicht vorhanden)."""

    def __init__(self, answers):
        self.answers = answers
        self.calls = []

    def __call__(self, argv, timeout=30):
        self.calls.append(list(argv))
        a = self.answers.get(argv[0])
        return a(argv) if callable(a) else a


class AptParserTests(unittest.TestCase):
    def test_security_updates_new_packages_removals_and_kept_back_packages(self):
        r = agent.parse_apt_simulation(APT_SIM)
        names = {p["name"]: p for p in r["packages"]}
        self.assertEqual(sorted(names), ["bash", "libc6", "linux-image-6.12.0-2-amd64", "openssl", "tzdata"])
        self.assertEqual((names["libc6"]["from"], names["libc6"]["to"], names["libc6"]["security"]), ("2.36-9+deb12u3", "2.36-9+deb12u4", True))
        self.assertTrue(names["openssl"]["security"])            # eine von zwei Herkuenften ist Security
        self.assertFalse(names["bash"]["security"])
        self.assertFalse(names["tzdata"]["security"])
        self.assertTrue(names["linux-image-6.12.0-2-amd64"]["new"])
        self.assertIsNone(names["linux-image-6.12.0-2-amd64"]["from"])
        self.assertFalse(names["bash"]["new"])
        self.assertEqual(r["keptBack"], ["linux-image-amd64", "python3-held"])
        self.assertEqual(r["removals"], 1)

    def test_ubuntu_security_and_esm_origins_count_as_security(self):
        names = {p["name"]: p["security"] for p in agent.parse_apt_simulation(APT_UBUNTU)["packages"]}
        self.assertEqual(names, {"libssl3": True, "vim": True, "nano": False})

    def test_an_empty_list_is_empty(self):
        r = agent.parse_apt_simulation(APT_NOTHING)
        self.assertEqual((r["packages"], r["keptBack"], r["removals"]), ([], [], 0))

    def test_garbage_is_ignored_and_text_is_cleaned(self):
        r = agent.parse_apt_simulation("Inst broken line without parens\nInst x [1] (2 Debian:12/stable [amd64])\n")
        self.assertEqual([p["name"] for p in r["packages"]], ["x"])
        self.assertEqual(agent.clean("a\x00b\nc\x1b[31m"), "abc[31m")
        self.assertEqual(len(agent.clean("x" * 500, 50)), 50)

    def test_update_result_puts_security_first_counts_and_truncates(self):
        pk = [{"name": "b", "from": "1", "to": "2", "security": False, "new": False}, {"name": "a", "from": "1", "to": "2", "security": True, "new": False}]
        r = agent.finish_updates("apt", pk, ["k"], ["h"], int(time.time()) - 7200, 0)
        self.assertEqual([p["name"] for p in r["packages"]], ["a", "b"])
        self.assertEqual((r["count"], r["security"], r["keptBack"], r["held"]), (2, 1, ["k"], ["h"]))
        self.assertTrue(7100 < r["listsAgeSeconds"] < 7400)
        many = [{"name": "p%04d" % i, "from": "1", "to": "2", "security": False, "new": False} for i in range(agent.MAX_PACKAGES + 25)]
        r = agent.finish_updates("apt", many, [], [], None)
        self.assertEqual((r["count"], len(r["packages"]), r["truncated"], r["listsAgeSeconds"]), (agent.MAX_PACKAGES + 25, agent.MAX_PACKAGES, True, None))

    def test_other_package_managers_have_no_security_flag(self):
        dnf = "Last metadata expiration check: 0:10:00 ago.\nkernel.x86_64   6.1.0-1.fc39   updates\nbash.x86_64  5.2.26-1.fc39  updates\n\nObsoleting Packages\nfoo.noarch 1.0 updates\n"
        self.assertEqual([p["name"] for p in agent.parse_dnf_check_update(dnf)], ["kernel", "bash", "foo"])
        apk = "busybox-1.36.1-r3 x86_64 {busybox} (GPL-2.0-only) [upgradable from: busybox-1.36.1-r2]\nmusl-1.2.4-r2 x86_64 {musl} (MIT) [upgradable from: musl-1.2.4-r1]\n"
        r = agent.parse_apk_upgradable(apk)
        self.assertEqual([(p["name"], p["from"], p["to"]) for p in r], [("busybox", "1.36.1-r2", "1.36.1-r3"), ("musl", "1.2.4-r1", "1.2.4-r2")])
        pm = agent.parse_checkupdates("linux 6.6.1-1 -> 6.6.2-1\nbash 5.2-1 -> 5.2-2\n")
        self.assertEqual([(p["name"], p["to"]) for p in pm], [("linux", "6.6.2-1"), ("bash", "5.2-2")])
        res = agent.finish_updates("dnf", agent.parse_dnf_check_update(dnf), [], [], None)
        self.assertIsNone(res["security"])
        self.assertFalse(res["securityKnown"])

    def test_collect_updates_runs_the_simulation_without_root_and_reads_holds(self):
        fake = Fake({"apt-get": (0, APT_SIM, ""), "apt-mark": (0, "linux-image-amd64\n", "")})
        with mock.patch.object(agent, "run_cmd", fake), mock.patch.object(agent, "update_manager", lambda which=None: "apt"), \
                mock.patch.object(agent, "apt_lists_checked", lambda *a: 1700000000):
            status, body = agent.collect_updates()
        self.assertEqual(status, 200)
        self.assertEqual((body["manager"], body["count"], body["security"], body["held"], body["listsCheckedAt"]), ("apt", 5, 2, ["linux-image-amd64"], 1700000000))
        argv = fake.calls[0]
        self.assertEqual(argv[:2], ["apt-get", "-s"])                # nur Simulation: nie ein echtes Installieren
        self.assertIn("dist-upgrade", argv)
        self.assertNotIn("update", argv)                              # die Paketlisten aktualisiert nur root, nicht der Agent
        self.assertNotIn("install", argv)

    def test_a_failing_apt_gives_a_short_error_not_a_traceback(self):
        fake = Fake({"apt-get": (100, "", "E: Could not open lock file\n")})
        with mock.patch.object(agent, "run_cmd", fake), mock.patch.object(agent, "update_manager", lambda which=None: "apt"):
            status, body = agent.collect_updates()
        self.assertEqual((status, body["error"]), (500, "apt_failed"))
        self.assertIn("lock", body["detail"])
        with mock.patch.object(agent, "run_cmd", Fake({})), mock.patch.object(agent, "update_manager", lambda which=None: "apt"):
            self.assertEqual(agent.collect_updates()[0], 500)
        with mock.patch.object(agent, "update_manager", lambda which=None: None):
            self.assertEqual(agent.collect_updates(), (404, {"error": "unsupported", "detail": "kein Paketmanager gefunden"}))

    def test_the_age_of_the_package_lists(self):
        with tempfile.TemporaryDirectory() as d:
            lists = os.path.join(d, "lists")
            os.makedirs(os.path.join(lists, "partial"))
            stamp = os.path.join(d, "stamp")
            self.assertIsNone(agent.apt_lists_checked(lists, stamp))
            old = os.path.join(lists, "deb.example_Packages")
            open(old, "w").close(); os.utime(old, (1000, 1000))
            self.assertEqual(agent.apt_lists_checked(lists, stamp), 1000)
            open(stamp, "w").close(); os.utime(stamp, (5000, 5000))
            self.assertEqual(agent.apt_lists_checked(lists, stamp), 5000)


class SysinfoTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.proc = os.path.join(self.dir.name, "proc")
        self.etc = os.path.join(self.dir.name, "etc")
        self.run = os.path.join(self.dir.name, "run")
        for d in (self.proc, self.etc, self.run, os.path.join(self.proc, "net")):
            os.makedirs(d, exist_ok=True)

    def write(self, base, name, text):
        with open(os.path.join(base, name), "w") as f:
            f.write(text)

    def test_memory_uptime_load_and_os_come_from_the_files(self):
        self.write(self.proc, "meminfo", MEMINFO)
        self.write(self.proc, "uptime", "93784.50 180000.00\n")
        self.write(self.proc, "loadavg", "0.52 0.40 0.31 1/200 12345\n")
        self.write(self.etc, "os-release", OS_RELEASE)
        mem = agent.read_meminfo(self.proc)
        self.assertEqual((mem["totalBytes"], mem["availableBytes"], mem["swapTotalBytes"], mem["swapFreeBytes"]), (2048000 * 1024, 1024000 * 1024, 512000 * 1024, 256000 * 1024))
        self.assertEqual(agent.read_uptime(self.proc), 93784)
        self.assertEqual(agent.read_load(self.proc), [0.52, 0.40, 0.31])
        self.assertEqual(agent.read_os_release(os.path.join(self.etc, "os-release"))["PRETTY_NAME"], "Debian GNU/Linux 12 (bookworm)")

    def test_missing_or_broken_files_give_empty_values_not_errors(self):
        self.assertEqual(agent.read_meminfo(self.proc)["totalBytes"], 0)
        self.assertIsNone(agent.read_uptime(self.proc))
        self.assertIsNone(agent.read_load(self.proc))
        self.write(self.proc, "uptime", "kaputt")
        self.assertIsNone(agent.read_uptime(self.proc))

    def test_disks_list_real_filesystems_once_with_percent_like_df(self):
        mounts = "\n".join([
            "/dev/vda1 / ext4 rw 0 0",
            "/dev/vda1 /var/lib/docker/overlay2/x ext4 rw 0 0",
            "tmpfs /run tmpfs rw 0 0",
            "proc /proc proc rw 0 0",
            "/dev/vda2 /boot/efi vfat rw 0 0",
            "/dev/vdb1 /data\\040disk xfs rw 0 0",
            "server:/share /mnt/nfs nfs4 rw 0 0",
            "/dev/loop1 /snap/core/1 squashfs ro 0 0",
            "/dev/vdc1 /etc/hosts ext4 rw 0 0",                  # eine Datei als Mount (Container): keine Platte
            "/dev/vda1 /home ext4 rw 0 0",                       # dasselbe Geraet noch einmal (ProtectHome): zaehlt nicht doppelt
        ])
        self.write(self.proc, "mounts", mounts)
        size = {"/": (1000, 400, 300), "/boot/efi": (100, 90, 90), "/data disk": (500, 100, 100)}

        def fake_statvfs(path):
            blocks, free, avail = size[path]
            return os.statvfs_result((4096, 4096, blocks, free, avail, 0, 0, 0, 0, 255))
        disks = agent.read_disks(self.proc, fake_statvfs, lambda p: p != "/etc/hosts")
        self.assertEqual([d["mount"] for d in disks], ["/", "/boot/efi", "/data disk"])
        root = disks[0]
        self.assertEqual((root["totalBytes"], root["usedBytes"], root["freeBytes"]), (1000 * 4096, 600 * 4096, 300 * 4096))
        self.assertEqual(root["percent"], round(100 * 600 / 900, 1))
        self.assertEqual(disks[1]["percent"], 10.0)

    def test_addresses_from_ip_and_the_fallback_without_ip(self):
        found = agent.parse_ip_addr(IP_ADDR)
        self.assertEqual([(a["iface"], a["family"], a["address"], a["prefix"]) for a in found], [("eth0", 4, "192.0.2.10", 24), ("eth0", 6, "2001:db8::10", 64)])
        self.assertEqual([a["address"] for a in agent.parse_fib_trie(FIB_TRIE)], ["192.0.2.10"])
        self.write(self.proc + "/net", "fib_trie", FIB_TRIE)
        with mock.patch.object(agent, "run_cmd", Fake({})):          # kein ip-Programm
            self.assertEqual([a["address"] for a in agent.read_addresses(self.proc)], ["192.0.2.10"])
        with mock.patch.object(agent, "run_cmd", Fake({"ip": (0, IP_ADDR, "")})):
            self.assertEqual(len(agent.read_addresses(self.proc)), 2)

    def test_reboot_required_and_its_packages(self):
        with mock.patch.object(agent, "run_cmd", Fake({})), mock.patch.object(agent, "health_snapshot", lambda: {"pending": True}):
            plain = agent.collect_sysinfo(self.proc, self.etc, self.run)
            self.assertFalse(plain["rebootRequired"])
            self.write(self.run, "reboot-required", "*** System restart required ***\n")
            self.write(self.run, "reboot-required.pkgs", "linux-image-6.1.0-20-amd64\nlibc6\n")
            need = agent.collect_sysinfo(self.proc, self.etc, self.run)
        self.assertTrue(need["rebootRequired"])
        self.assertEqual(need["rebootPackages"], ["linux-image-6.1.0-20-amd64", "libc6"])

    def test_virtualisation_and_proxmox_detection(self):
        with mock.patch.object(agent, "run_cmd", Fake({"systemd-detect-virt": (0, "kvm\n", "")})):
            self.assertEqual(agent.detect_virt(self.proc), {"kind": "vm", "name": "kvm"})
        with mock.patch.object(agent, "run_cmd", Fake({"systemd-detect-virt": (1, "none\n", "")})):
            self.assertEqual(agent.detect_virt(self.proc), {"kind": "none", "name": "none"})
        with mock.patch.object(agent, "run_cmd", Fake({"systemd-detect-virt": (0, "lxc\n", "")})):
            self.assertEqual(agent.detect_virt(self.proc)["kind"], "container")
        self.write(self.proc, "cpuinfo", "processor : 0\nflags : fpu vme hypervisor lm\n")
        with mock.patch.object(agent, "run_cmd", Fake({})):          # ohne systemd-detect-virt: das hypervisor-Flag der CPU
            self.assertEqual(agent.detect_virt(self.proc, lambda p: False)["kind"], "vm")
        self.assertIsNone(agent.detect_proxmox(lambda p: False))
        with mock.patch.object(agent, "run_cmd", Fake({"pveversion": (0, "pve-manager/8.2.4/faa83925c9641325 (running kernel: 6.8.12-1-pve)\n", "")})):
            self.assertEqual(agent.detect_proxmox(lambda p: p == "/etc/pve"), {"product": "ve", "version": "8.2.4"})
        self.assertEqual(agent.detect_proxmox(lambda p: p == "/etc/proxmox-backup"), {"product": "pbs", "version": ""})

    def test_the_whole_report_is_json_with_the_expected_shape(self):
        self.write(self.proc, "meminfo", MEMINFO)
        self.write(self.proc, "uptime", "10.0 5.0\n")
        self.write(self.proc, "loadavg", "1.00 2.00 3.00 1/2 3\n")
        self.write(self.proc, "mounts", "")
        self.write(self.etc, "os-release", OS_RELEASE)
        self.write(self.proc + "/net", "dev", NETDEV)
        with mock.patch.object(agent, "run_cmd", Fake({})), mock.patch.object(agent, "health_snapshot", lambda: {"pending": True}):
            info = agent.collect_sysinfo(self.proc, self.etc, self.run)
        json.dumps(info)
        self.assertEqual(info["network"], [{"iface": "eth0", "rxBytes": 1000, "txBytes": 2000}])
        for key in ("hostname", "os", "kernel", "uptimeSeconds", "load", "cpuCount", "memory", "disks", "addresses", "network", "health", "rebootRequired", "virt", "proxmox", "collectedAt"):
            self.assertIn(key, info)
        self.assertEqual(info["os"], "Debian GNU/Linux 12 (bookworm)")


class HealthTests(unittest.TestCase):
    def test_mdstat_tells_healthy_degraded_recovering_and_inactive_arrays(self):
        arrays = {a["name"]: a for a in agent.parse_mdstat(MDSTAT)}
        self.assertEqual({k: v["state"] for k, v in arrays.items()}, {"md0": "ok", "md1": "degraded", "md2": "recovering", "md3": "inactive"})
        self.assertEqual((arrays["md0"]["level"], arrays["md0"]["active"], arrays["md0"]["total"]), ("raid1", 2, 2))
        self.assertEqual(arrays["md2"]["syncPercent"], 12.6)
        self.assertEqual(agent.parse_mdstat(""), [])

    def test_a_scrub_of_a_healthy_array_stays_ok(self):
        text = "md0 : active raid1 sdb1[1] sda1[0]\n      1 blocks [2/2] [UU]\n      [=>...]  check = 5.0% (1/2) finish=1min\n"
        self.assertEqual(agent.parse_mdstat(text)[0]["state"], "ok")

    def test_zpool_status(self):
        self.assertEqual(agent.parse_zpool_status("all pools are healthy\n"), {"healthy": True, "pools": []})
        self.assertEqual(agent.parse_zpool_status(ZPOOL_BAD), {"healthy": False, "pools": [{"name": "tank", "state": "DEGRADED"}]})

    def test_smart_summary_per_disk(self):
        self.assertEqual(agent.parse_smart_health("SMART overall-health self-assessment test result: PASSED"), "ok")
        self.assertEqual(agent.parse_smart_health("SMART Health Status: OK"), "ok")
        self.assertEqual(agent.parse_smart_health("SMART overall-health self-assessment test result: FAILED!"), "failing")
        self.assertEqual(agent.parse_smart_health("Smartctl open device: /dev/sda failed: Permission denied"), "no_access")
        self.assertEqual(agent.parse_smart_health("garbage"), "unknown")

    def test_smart_is_not_available_without_the_program_or_without_access(self):
        with mock.patch.object(agent.shutil, "which", lambda t: None):
            self.assertEqual(agent.collect_smart(), {"available": False, "reason": "no_smartctl"})
        scan = "/dev/sda -d scsi # /dev/sda, SCSI device\n/dev/nvme0 -d nvme # /dev/nvme0, NVMe device\n"
        denied = Fake({"smartctl": lambda argv: (0, scan, "") if "--scan" in argv else (4, "", "Permission denied")})
        with mock.patch.object(agent.shutil, "which", lambda t: "/usr/sbin/smartctl"), mock.patch.object(agent, "run_cmd", denied):
            self.assertEqual(agent.collect_smart(), {"available": False, "reason": "no_access"})
        mixed = Fake({"smartctl": lambda argv: (0, scan, "") if "--scan" in argv else (0, "SMART overall-health self-assessment test result: PASSED", "") if argv[-1] == "/dev/sda" else (4, "", "FAILED health")})
        with mock.patch.object(agent.shutil, "which", lambda t: "/usr/sbin/smartctl"), mock.patch.object(agent, "run_cmd", mixed):
            r = agent.collect_smart()
        self.assertEqual((r["available"], [(d["name"], d["health"]) for d in r["devices"]]), (True, [("/dev/sda", "ok"), ("/dev/nvme0", "unknown")]))

    def test_the_snapshot_is_computed_in_the_background_and_cached(self):
        calls = []
        done = threading.Event()

        release = threading.Event()

        def compute():
            calls.append(1)
            release.wait(5)
            done.set()
            return {"md": [], "checkedAt": 1}
        saved = dict(agent.HEALTH)
        self.addCleanup(lambda: agent.HEALTH.update(saved))
        agent.HEALTH.update(t=0, data=None, busy=False)
        self.assertEqual(agent.health_snapshot(compute), {"pending": True})            # die erste Antwort wartet nicht
        release.set()
        self.assertTrue(done.wait(5))
        for _ in range(100):
            if not agent.HEALTH["busy"]:
                break
            time.sleep(0.02)
        self.assertEqual(agent.health_snapshot(compute)["checkedAt"], 1)
        self.assertEqual(len(calls), 1)                                                  # frisch genug: nicht noch einmal


class ServicesAndDockerTests(unittest.TestCase):
    def test_failed_units_are_parsed(self):
        text = "● nginx.service loaded failed failed A high performance web server\n  backup.timer  loaded failed failed Nightly backup\n"
        r = agent.parse_failed_units(text)
        self.assertEqual([(u["unit"], u["sub"], u["description"]) for u in r], [("nginx.service", "failed", "A high performance web server"), ("backup.timer", "failed", "Nightly backup")])
        self.assertEqual(agent.parse_failed_units(""), [])

    def test_services_report_state_and_count(self):
        fake = Fake({"systemctl": lambda argv: (0, "x.service loaded failed failed X\n", "") if "--failed" in argv else (1, "degraded\n", "")})
        with mock.patch.object(agent, "run_cmd", fake), mock.patch.object(agent, "systemd_running", lambda e=None: True):
            status, body = agent.collect_services()
        self.assertEqual((status, body["state"], body["count"]), (200, "degraded", 1))
        with mock.patch.object(agent, "systemd_running", lambda e=None: False):
            self.assertEqual(agent.collect_services()[0], 404)

    def _docker(self, handler_status, payload, perm_denied=False):
        d = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(d, ignore_errors=True))
        path = os.path.join(d, "docker.sock")
        seen = []

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                seen.append(self.path)
                data = json.dumps(payload).encode()
                self.send_response(handler_status)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def address_string(self):
                return "unix"

        class S(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
            daemon_threads = True
        srv = S(path, H)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(srv.server_close)
        self.addCleanup(srv.shutdown)
        return path, seen

    def test_docker_lists_containers_read_only_through_the_socket(self):
        path, seen = self._docker(200, [
            {"Id": "a" * 64, "Names": ["/web"], "Image": "nginx:1", "State": "running", "Status": "Up 2 hours"},
            {"Id": "b" * 64, "Names": ["/old"], "Image": "alpine", "State": "exited", "Status": "Exited (0) 3 days ago"},
            "kaputt",
        ])
        status, body = agent.collect_docker(path)
        self.assertEqual(status, 200)
        self.assertEqual((body["available"], body["running"], body["restarting"], body["total"]), (True, 1, 0, 2))
        self.assertEqual([c["name"] for c in body["containers"]], ["web", "old"])
        self.assertEqual(body["containers"][0]["id"], "a" * 12)
        self.assertEqual(seen, ["/containers/json?all=1"])            # genau diese eine, lesende Anfrage

    def test_docker_without_socket_or_access_is_not_available_not_an_error(self):
        self.assertEqual(agent.collect_docker("/nonexistent/docker.sock"), (200, {"available": False, "reason": "no_socket"}))
        path, _ = self._docker(500, {})
        self.assertEqual(agent.collect_docker(path), (200, {"available": False, "reason": "error"}))
        d = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(d, ignore_errors=True))
        with mock.patch.object(agent.os.path, "exists", lambda p: True), mock.patch.object(agent._UnixHTTP, "connect", side_effect=PermissionError):
            self.assertEqual(agent.collect_docker("/var/run/none.sock"), (200, {"available": False, "reason": "no_access"}))

    def test_many_containers_are_cut_off(self):
        items = [{"Id": "%064d" % i, "Names": ["/c%03d" % i], "Image": "i", "State": "running", "Status": "Up"} for i in range(agent.MAX_CONTAINERS + 10)]
        r = agent.summarize_containers(items)
        self.assertEqual((len(r["containers"]), r["total"], r["truncated"]), (agent.MAX_CONTAINERS, agent.MAX_CONTAINERS + 10, True))


class PermissionTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.path = os.path.join(self.dir.name, "permissions")

    def put(self, text, mode=0o644):
        with open(self.path, "w") as f:
            f.write(text)
        os.chmod(self.path, mode)

    def test_parsing_ignores_comments_and_unknown_words(self):
        self.assertEqual(agent.parse_permissions("# nichts\nupdates   # ja\nUPDATES\nformat-disk\n\nreboot\n"), {"updates", "reboot"})
        self.assertEqual(agent.parse_permissions(""), set())

    def test_the_file_counts_only_if_a_trusted_user_owns_it_and_nobody_else_can_write(self):
        me = os.getuid()
        self.put("updates\n")
        self.assertEqual(agent.read_permissions(self.path, trusted_uids=(me,)), {"updates"})
        self.assertEqual(agent.read_permissions(self.path, trusted_uids=(me + 1,)), set())      # der Agent-Benutzer gibt sich nichts selbst
        self.put("updates\n", 0o664)
        self.assertEqual(agent.read_permissions(self.path, trusted_uids=(me,)), set())          # fuer die Gruppe beschreibbar
        self.put("updates\n", 0o666)
        self.assertEqual(agent.read_permissions(self.path, trusted_uids=(me,)), set())
        os.unlink(self.path)
        self.assertEqual(agent.read_permissions(self.path, trusted_uids=(me,)), set())          # keine Datei: nichts erlaubt

    def test_parameters_and_file_add_up(self):
        none = argparse.Namespace(allow_updates=False, allow_reboot=False)
        with mock.patch.object(agent, "read_permissions", lambda *a, **k: set()):
            self.assertEqual(agent.permissions(none), set())
            self.assertEqual(agent.permissions(argparse.Namespace(allow_updates=True, allow_reboot=False)), {"updates"})
        with mock.patch.object(agent, "read_permissions", lambda *a, **k: {"reboot"}):
            self.assertEqual(agent.permissions(argparse.Namespace(allow_updates=True, allow_reboot=False)), {"updates", "reboot"})

    def test_write_actions_are_refused_without_permission_and_not_built_with_it(self):
        with mock.patch.object(agent, "permissions", lambda args=None: set()):
            self.assertEqual(agent.host_api("POST", "/v1/host/updates/install", {}), (403, {"error": "not_permitted", "need": "updates"}))
            self.assertEqual(agent.host_api("POST", "/v1/host/reboot", {}), (403, {"error": "not_permitted", "need": "reboot"}))
        with mock.patch.object(agent, "permissions", lambda args=None: {"updates"}):
            self.assertEqual(agent.host_api("POST", "/v1/host/updates/install", {}), (501, {"error": "not_implemented"}))
            self.assertEqual(agent.host_api("POST", "/v1/host/reboot", {})[0], 403)           # nur das erlaubte Recht gilt


class HostApiTests(unittest.TestCase):
    def test_only_fixed_read_paths_and_nothing_of_the_desktop_api(self):
        with mock.patch.object(agent, "host_caps", lambda *a, **k: ["sysinfo"]):
            self.assertEqual(agent.host_api("GET", "/v1/windows", {})[0], 404)
            self.assertEqual(agent.host_api("POST", "/v1/apps/launch", {"id": "x"})[0], 404)
            self.assertEqual(agent.host_api("POST", "/v1/host/sysinfo", {})[0], 404)
            self.assertEqual(agent.host_api("GET", "/v1/host/../../etc/passwd", {})[0], 404)
            self.assertEqual(agent.host_api("GET", "/v1/host/updates", {}), (404, {"error": "unsupported"}))     # Faehigkeit nicht gemeldet
            self.assertEqual(agent.host_api("GET", "/v1/host/sysinfo?x=1", {})[0], 200)

    def test_health_names_mode_platform_and_capabilities(self):
        with mock.patch.object(agent, "host_caps", lambda *a, **k: ["sysinfo", "updates"]), mock.patch.object(agent, "permissions", lambda args=None: {"updates"}):
            status, body = agent.host_api("GET", "/v1/health", {})
        self.assertEqual((status, body["mode"], body["platform"], body["caps"], body["allow"]), (200, "host", "linux-host", ["sysinfo", "updates"], ["updates"]))

    def test_an_error_inside_a_collector_becomes_a_500_with_a_short_text(self):
        with mock.patch.object(agent, "host_caps", lambda *a, **k: ["sysinfo"]), mock.patch.object(agent, "collect_sysinfo", side_effect=RuntimeError("boom\nsecret")):
            status, body = agent.host_api("GET", "/v1/host/sysinfo", {})
        self.assertEqual((status, body["error"]), (500, "failed"))
        self.assertNotIn("\n", body["detail"])

    def test_capabilities_follow_what_the_machine_has(self):
        self.assertEqual(agent.host_caps(lambda p: False, lambda t: None), ["sysinfo"])
        self.assertEqual(agent.host_caps(lambda p: True, lambda t: "/usr/bin/" + t), ["sysinfo", "updates", "services", "docker"])
        self.assertEqual(agent.update_manager(lambda t: "/x" if t == "dnf" else None), "dnf")
        self.assertIsNone(agent.update_manager(lambda t: None))


class ModeTests(unittest.TestCase):
    def ns(self, **kw):
        return argparse.Namespace(**{"host": False, "desktop": False, **kw})

    def test_host_flag_wins_and_desktop_flag_forces_the_desktop(self):
        with mock.patch.object(agent, "gtk_stack_problems", lambda: []):
            self.assertEqual(agent.choose_mode(self.ns(host=True)), "host")
            self.assertEqual(agent.choose_mode(self.ns()), "desktop")
        with mock.patch.object(agent, "gtk_stack_problems", lambda: ["Wnck fehlt"]):
            self.assertEqual(agent.choose_mode(self.ns()), "host")               # kein Desktop-Unterbau: automatisch Host
            self.assertEqual(agent.choose_mode(self.ns(desktop=True)), "desktop")

    def test_a_desktop_machine_without_display_does_not_silently_become_a_host(self):
        with mock.patch.object(agent, "gtk_stack_problems", lambda: []), mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("DISPLAY", None)
            self.assertEqual(agent.choose_mode(self.ns()), "desktop")
            problems, _ = agent.missing_requirements("desktop")
            self.assertTrue(any("--host" in p for p in problems))

    def test_the_host_mode_needs_only_python(self):
        self.assertEqual(agent.missing_requirements("host")[0], [])


# ---- Die Verbindung: ein kleiner WebSocket-Server (RFC 6455) wie der JulOS-Server, gegen die echte Link-Klasse
class MiniServer:
    GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

    def __init__(self):
        self.srv = socket.socket()
        self.srv.bind(("127.0.0.1", 0))
        self.srv.listen(1)
        self.port = self.srv.getsockname()[1]
        self.messages = []
        self.auth = None
        self.conn = None
        self.cond = threading.Condition()
        threading.Thread(target=self.run, daemon=True).start()

    def run(self):
        conn, _ = self.srv.accept()
        self.conn = conn
        data = b""
        while b"\r\n\r\n" not in data:
            data += conn.recv(4096)
        head = data.decode("latin-1")
        key = [l.split(":", 1)[1].strip() for l in head.split("\r\n") if l.lower().startswith("sec-websocket-key")][0]
        self.auth = [l.split(":", 1)[1].strip() for l in head.split("\r\n") if l.lower().startswith("authorization")][0]
        accept = base64.b64encode(hashlib.sha1((key + self.GUID).encode()).digest()).decode()
        conn.sendall(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: %s\r\n\r\n" % accept).encode())
        buf = b""
        try:
            while True:
                while len(buf) < 2:
                    c = conn.recv(65536)
                    if not c:
                        return
                    buf += c
                ln = buf[1] & 0x7F
                off = 2
                if ln == 126:
                    while len(buf) < 4:
                        buf += conn.recv(65536)
                    ln, off = struct.unpack(">H", buf[2:4])[0], 4
                while len(buf) < off + 4 + ln:
                    c = conn.recv(65536)
                    if not c:
                        return
                    buf += c
                mask = buf[off:off + 4]
                payload = bytes(b ^ mask[i % 4] for i, b in enumerate(buf[off + 4:off + 4 + ln]))
                buf = buf[off + 4 + ln:]
                with self.cond:
                    self.messages.append(json.loads(payload.decode()))
                    self.cond.notify_all()
        except OSError:
            pass

    def send(self, obj):
        data = json.dumps(obj).encode()
        head = bytes([0x81, len(data)]) if len(data) < 126 else bytes([0x81, 126]) + struct.pack(">H", len(data))
        self.conn.sendall(head + data)

    def wait(self, pred, seconds=8):
        end = time.time() + seconds
        with self.cond:
            while time.time() < end:
                for m in self.messages:
                    if pred(m):
                        return m
                self.cond.wait(0.1)
        return None


class LinkTests(unittest.TestCase):
    def start(self, **kw):
        server = MiniServer()
        cfg = {"server": "http://127.0.0.1:%d" % server.port, "token": "jat_test", "allow_http": True}
        link = agent.Link(cfg, handler=agent.host_api, hello=agent.host_hello, restart=lambda: None, transfers=False, **kw)
        threading.Thread(target=link.run, daemon=True).start()
        return server, link

    def test_the_host_agent_says_hello_with_its_capabilities_and_answers_requests(self):
        with mock.patch.object(agent, "host_caps", lambda *a, **k: ["sysinfo", "services"]), mock.patch.object(agent, "write_status", lambda *a, **k: None), \
                mock.patch.object(agent, "permissions", lambda args=None: set()), \
                mock.patch.object(agent, "collect_services", lambda: (200, {"state": "running", "count": 0, "failed": []})):
            server, link = self.start()
            hello = server.wait(lambda m: m.get("t") == "hello")
            self.assertIsNotNone(hello)
            self.assertEqual(server.auth, "Bearer jat_test")
            self.assertEqual((hello["platform"], hello["caps"], hello["allow"], hello["agent"]), ("linux-host", ["sysinfo", "services"], [], agent.AGENT_VERSION))
            self.assertTrue(hello["hostname"])
            server.send({"t": "welcome", "server": "9.9.9"})
            server.send({"t": "req", "id": 7, "method": "GET", "path": "/v1/host/services"})
            res = server.wait(lambda m: m.get("t") == "res" and m.get("id") == 7)
            self.assertEqual((res["status"], res["body"]["state"]), (200, "running"))
            server.send({"t": "req", "id": 8, "method": "GET", "path": "/v1/windows"})          # Desktop-Anfrage an einen Host: nicht bekannt
            self.assertEqual(server.wait(lambda m: m.get("t") == "res" and m.get("id") == 8)["status"], 404)
            server.send({"t": "put", "id": 9, "kind": "file", "name": "x.txt", "size": 3})        # Datei fuer die Zwischenablage: gibt es hier nicht
            self.assertEqual(server.wait(lambda m: m.get("t") == "res" and m.get("id") == 9)["status"], 404)
            server.send({"t": "pull", "id": 10, "max": 100})
            self.assertEqual(server.wait(lambda m: m.get("t") == "res" and m.get("id") == 10)["status"], 404)
            server.send({"t": "req", "id": 11, "method": "POST", "path": "/v1/host/reboot", "body": {}})
            self.assertEqual(server.wait(lambda m: m.get("t") == "res" and m.get("id") == 11)["status"], 403)
            server.send({"t": "req", "id": 12, "method": "GET", "path": "/etc/passwd"})           # kein /v1/-Pfad
            self.assertEqual(server.wait(lambda m: m.get("t") == "res" and m.get("id") == 12)["status"], 400)

    def test_a_self_update_in_host_mode_restarts_through_the_given_function_not_gtk(self):
        server = MiniServer()
        restarted = threading.Event()
        link = agent.Link({"server": "http://127.0.0.1:%d" % server.port, "token": "jat_t", "allow_http": True}, handler=agent.host_api, hello=agent.host_hello, restart=restarted.set, transfers=False)
        with mock.patch.object(agent, "apply_update", lambda *a, **k: ("9.9.9", True)), mock.patch.object(agent, "write_status", lambda *a, **k: None), \
                mock.patch.object(agent.time, "sleep", lambda s: None):
            threading.Thread(target=link.run, daemon=True).start()
            self.assertIsNotNone(server.wait(lambda m: m.get("t") == "hello"))
            server.send({"t": "update", "id": 3, "path": "/agent-files/julos-agent.py", "sha256": "0" * 64, "version": "9.9.9"})
            res = server.wait(lambda m: m.get("t") == "res" and m.get("id") == 3)
            self.assertEqual((res["status"], res["body"]["restarting"]), (200, True))
            self.assertTrue(restarted.wait(5))


class EnrolTests(unittest.TestCase):
    def test_the_host_agent_enrols_as_linux_host(self):
        seen = {}

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_POST(self):
                seen["body"] = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                data = json.dumps({"token": "jat_abc", "host": "11111111-1111-1111-1111-111111111111"}).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
        srv = http.server.HTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(srv.server_close)
        self.addCleanup(srv.shutdown)
        with tempfile.TemporaryDirectory() as d:
            cfg = os.path.join(d, "cfg", "agent.json")
            with mock.patch.object(agent, "CONFIG", cfg):
                agent.enroll("http://127.0.0.1:%d" % srv.server_address[1], "ABCDE-FGHIJ", False, agent.HOST_PLATFORM)
                self.assertEqual(seen["body"]["platform"], "linux-host")
                self.assertEqual(seen["body"]["code"], "ABCDE-FGHIJ")
                self.assertEqual(stat.S_IMODE(os.stat(cfg).st_mode), 0o600)
                with open(cfg) as f:
                    self.assertEqual(json.load(f)["token"], "jat_abc")


if __name__ == "__main__":
    unittest.main()
