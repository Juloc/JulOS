#!/usr/bin/env python3
"""julos-agent-helper: die kleine root-Gegenstelle des JulOS-Host-Agenten (Stufe 3, docs/HOST-AGENT.md).

Der Agent selbst laeuft als Benutzer ohne Rechte, ohne sudo und ohne neue Privilegien. Was root braucht (apt, systemctl, journalctl, Neustart), erledigt dieser Helfer:
ein systemd-Socket-Dienst (julos-agent-helper.socket, Accept=yes), der je Verbindung genau EINE Anfrage liest, sie prueft und genau ein festes Programm mit Argumentliste
ausfuehrt (keine Shell, kein freier Befehl). Der Helfer gehoert root und steht in einem Ordner, den der Agent nicht beschreiben kann; der Installer legt ihn nur an, wenn man
bewusst Aktionen erlaubt (--allow), und ein erneuter Lauf des Installers aktualisiert ihn (die Selbstaktualisierung des Agenten fasst ihn nicht an).

Anfrage (eine JSON-Zeile auf stdin):   {"verb": "...", ...}
Antwort (JSON-Zeilen auf stdout):      {"p": "Fortschrittszeile"} ...  dann {"r": {"ok": true|false, "code": "...", ...}}   (Neustart: erst {"a": true}, dann Verzoegerung)

Verben (jedes verlangt sein Wort in /etc/julos-agent/permissions, das nur zaehlt, wenn root es besitzt und sonst niemand schreiben kann):
  apt.refresh    updates    apt-get update
  apt.upgrade    updates    apt-get upgrade, mit "full": true apt-get dist-upgrade (Proxmox braucht es), oder fuer genannte Pakete apt-get install --only-upgrade --no-remove;
                            vorher prueft der Helfer `dpkg --audit`: ist die Paketdatenbank kaputt, lehnt er mit "dpkg_broken" ab
  apt.repair     updates    dpkg --configure -a, danach apt-get -f install (repariert halb eingerichtete Pakete)
  power          reboot     systemctl reboot | poweroff, nach "angenommen" und kurzer Verzoegerung
  service        services   systemctl start | stop | restart  <Dienst>  (nicht den Agenten selbst)
  journal        logs       journalctl --unit=<Dienst> -n <N>  (hoechstens 200 Zeilen)
  smart.health   disks      LESEND: smartctl -H -A -j /dev/<name> fuer die Platten, die der Helfer selbst mit lsblk findet (nie ein Name vom Agenten)
Nur die Python-Standardbibliothek.
"""
import collections
import json
import os
import re
import select
import signal
import stat
import subprocess
import sys
import time

PERMISSIONS_FILE = "/etc/julos-agent/permissions"       # absichtlich keine Umgebungsvariable: dieser Helfer laeuft als root
RIGHTS = ("updates", "reboot", "services", "docker", "logs", "disks")
VERB_RIGHT = {"apt.refresh": "updates", "apt.upgrade": "updates", "apt.repair": "updates", "power": "reboot", "service": "services", "journal": "logs", "smart.health": "disks"}
PKG_RE = re.compile(r"^[a-z0-9](?:[a-z0-9+.\-]*[a-z0-9+.])?(?::[a-z0-9]+)?\Z")       # kein '-' am Ende: "paket-" hiesse bei apt "entfernen"
UNIT_RE = re.compile(r"^(?!-)[A-Za-z0-9:_.@\-]{1,100}\.service\Z")
MAX_PACKAGES = 20
MAX_LINE = 300
REQUEST_TIMEOUT = 15                    # Sekunden bis die ganze Anfrage-Zeile da sein muss (sonst haelt ein stummer Client einen der wenigen Plaetze, MaxConnections=4, fuer immer)
TAIL_LINES = 40                         # so viele letzte Zeilen stehen weiterhin in "output" (alte Agenten lesen nur das)
LOG_LINES = 2000                        # das ganze Protokoll eines apt-Laufs, begrenzt: die letzten Zeilen
LOG_WIDTH = 240                         # Zeichen je Zeile im Protokoll
LOG_BYTES = 200 * 1024                  # und hoechstens so viel insgesamt (eine Antwortzeile darf nie zu gross werden)
LOG_MAX = 200
SMART_MAX_DEVICES = 16
DEVICE_RE = re.compile(r"^[a-z][a-z0-9]{1,15}\Z")      # Plattenname fuer /dev/<name>; der Helfer bildet die Liste selbst, nie vom Agenten
ENV = {"PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C", "LANG": "C", "DEBIAN_FRONTEND": "noninteractive",
       "APT_LISTCHANGES_FRONTEND": "none", "NEEDRESTART_MODE": "l", "NEEDRESTART_SUSPEND": "1"}      # needrestart nur melden, nie Dienste von selbst neu starten


def clean(text, limit=MAX_LINE):
    return "".join(c for c in str(text) if c.isprintable()).strip()[:limit]


def clean_log(text, limit=LOG_WIDTH):
    """Eine Protokollzeile: nur druckbare Zeichen, Einrueckung bleibt (apt rueckt Paketlisten ein), hoechstens limit Zeichen."""
    return "".join(c for c in str(text) if c.isprintable()).rstrip()[:limit]


def read_permissions(path=PERMISSIONS_FILE, trusted_uids=(0,)):
    """Die Woerter der Datei; sie zaehlt nur, wenn ein vertrauter Benutzer (root) sie besitzt, sie eine normale Datei ist und niemand sonst schreiben kann."""
    try:
        st = os.stat(path)
        if st.st_uid not in trusted_uids or st.st_mode & 0o022 or not stat.S_ISREG(st.st_mode):
            return set()
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read(4096)
    except OSError:
        return set()
    return {w for w in (line.split("#", 1)[0].strip().lower() for line in text.splitlines()) if w in RIGHTS}


def real_run(argv, on_line, timeout):
    """Ein festes Programm ohne Shell; Ausgabe (stdout und stderr zusammen) Zeile fuer Zeile an on_line. (Rueckgabewert, letzte Zeilen bis LOG_LINES); (None, []) wenn es nicht startet, (-1, ...) bei Zeitueberschreitung."""
    try:
        p = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=dict(ENV), close_fds=True)
    except OSError:
        return None, []
    tail, end = collections.deque(maxlen=LOG_LINES), time.time() + timeout
    try:
        for raw in iter(p.stdout.readline, b""):
            line = clean_log(raw.decode("utf-8", "replace"))
            if line.strip():
                tail.append(line)
                on_line(line)
            if time.time() > end:
                p.kill()
                p.wait()
                return -1, list(tail)
        return p.wait(), list(tail)
    finally:
        try:
            p.stdout.close()
        except OSError:
            pass


def limit_log(lines):
    """Das Protokoll fuer die Antwort: hoechstens LOG_LINES Zeilen zu je LOG_WIDTH Zeichen und LOG_BYTES insgesamt (die aeltesten Zeilen fallen zuerst weg)."""
    out = [clean_log(l) for l in lines[-LOG_LINES:]]
    total = sum(len(l) + 1 for l in out)
    while out and total > LOG_BYTES:
        total -= len(out.pop(0)) + 1
    return out


def parse_smart_json(text, name):
    """Ausgabe von `smartctl -H -A -j`: {name, health: ok|failing|unknown, temp?}. Der Rueckgabewert von smartctl ist eine Bitmaske und sagt hier nichts; nur das JSON zaehlt."""
    entry = {"name": name, "health": "unknown"}
    try:
        data = json.loads(text)
    except ValueError:
        return entry
    if not isinstance(data, dict):
        return entry
    status = data.get("smart_status")
    if isinstance(status, dict) and isinstance(status.get("passed"), bool):
        entry["health"] = "ok" if status["passed"] else "failing"
    temp = data.get("temperature")
    cur = temp.get("current") if isinstance(temp, dict) else None
    if isinstance(cur, int) and not isinstance(cur, bool) and -50 <= cur <= 150:
        entry["temp"] = cur
    return entry


class Helper:
    """run, sleep und permissions sind einsetzbar, damit Tests nie ein echtes Programm starten."""

    def __init__(self, run=real_run, sleep=time.sleep, permissions=read_permissions):
        self.run, self.sleep, self.permissions = run, sleep, permissions

    def handle(self, req, send):
        """Eine Anfrage bearbeiten; send(dict) schreibt eine Antwortzeile. Gibt das Ergebnis (r) zurueck."""
        verb = req.get("verb") if isinstance(req, dict) else None
        if verb not in VERB_RIGHT:
            return {"ok": False, "code": "unknown_verb"}
        if VERB_RIGHT[verb] not in self.permissions():
            return {"ok": False, "code": "not_permitted"}
        handler = getattr(self, "verb_" + verb.replace(".", "_"))
        return handler(req, send)

    def _run(self, argv, send, timeout):
        rc, tail = self.run(argv, lambda line: send({"p": line}), timeout)
        if rc is None:
            return {"ok": False, "code": "not_installed"}, tail
        output = "\n".join(tail[-TAIL_LINES:])
        if rc == -1:
            return {"ok": False, "code": "timeout", "output": output, "log": limit_log(tail)}, tail
        return {"ok": rc == 0, "code": "ok" if rc == 0 else self._code(argv, tail), "exit": rc, "output": output, "log": limit_log(tail)}, tail

    @staticmethod
    def _code(argv, tail):
        """Der Fehlercode zu einem Rueckgabewert ungleich 0: apt_locked (Sperre), dpkg_failed (dpkg kam mit einem Paket nicht zurecht), sonst apt_failed usw."""
        text = "\n".join(tail).lower()
        if "could not get lock" in text or "unable to acquire the dpkg" in text:
            return "apt_locked"
        name = os.path.basename(argv[0])
        if name == "dpkg" or (name == "apt-get" and ("errors were encountered while processing" in text or "sub-process /usr/bin/dpkg returned an error code" in text)):
            return "dpkg_failed"
        return {"apt-get": "apt_failed", "systemctl": "systemctl_failed", "journalctl": "journal_failed"}.get(name, "action_failed")

    def dpkg_audit(self):
        """`dpkg --audit` (lesend): leer = die Paketdatenbank ist in Ordnung. (Zeilen, Fehlercode oder None); Fehlercode nur, wenn dpkg nicht lief."""
        rc, tail = self.run(["dpkg", "--audit"], lambda line: None, 60)
        if rc is None:
            return [], "not_installed"
        if rc == -1:
            return [], "timeout"
        return ([l for l in tail if l.strip()] if rc == 0 else tail), None

    def broken_result(self, lines):
        return {"ok": False, "code": "dpkg_broken", "output": "\n".join(lines[-TAIL_LINES:]), "log": limit_log(lines)}

    def verb_apt_refresh(self, req, send):
        r, _ = self._run(["apt-get", "update", "-o", "DPkg::Lock::Timeout=60"], send, 600)
        return r

    def verb_apt_upgrade(self, req, send):
        packages, full = req.get("packages"), req.get("full")
        if full is not None and not isinstance(full, bool):
            return {"ok": False, "code": "invalid_request"}
        common = ["-y", "-o", "Dpkg::Options::=--force-confold", "-o", "Dpkg::Options::=--force-confdef", "-o", "DPkg::Lock::Timeout=60"]
        if packages is None:
            # upgrade entfernt nie ein Paket und installiert nichts Neues; dist-upgrade (full) darf neue Abhaengigkeiten holen und Pakete ersetzen (Proxmox verlangt es)
            argv = ["apt-get", "dist-upgrade" if full else "upgrade"] + common
        else:
            if full or not isinstance(packages, list) or not 1 <= len(packages) <= MAX_PACKAGES or not all(isinstance(p, str) and PKG_RE.match(p) for p in packages):
                return {"ok": False, "code": "invalid_request"}
            # nur ein Upgrade vorhandener Pakete, nichts entfernen; die Namen beginnen nie mit '-' (siehe PKG_RE)
            argv = ["apt-get", "install", "--only-upgrade", "--no-remove"] + common + list(dict.fromkeys(packages))
        lines, problem = self.dpkg_audit()                  # eine kaputte Paketdatenbank wird nicht noch weiter "aktualisiert": erst reparieren (apt.repair)
        if problem:
            return {"ok": False, "code": problem}
        if lines:
            return self.broken_result(lines)
        r, _ = self._run(argv, send, 3600)
        return r

    def verb_apt_repair(self, req, send):
        """Halb eingerichtete Pakete reparieren: dpkg --configure -a, danach apt-get -f install. Die zweite Stufe laeuft auch, wenn die erste scheitert (sie behebt oft gerade das)."""
        steps = (["dpkg", "--configure", "-a", "--force-confold", "--force-confdef"],
                 ["apt-get", "-f", "install", "-y", "-o", "Dpkg::Options::=--force-confold", "-o", "Dpkg::Options::=--force-confdef", "-o", "DPkg::Lock::Timeout=60"])
        first, log = None, []
        for argv in steps:
            r, tail = self._run(argv, send, 1800)
            log += tail
            if r.get("code") == "not_installed":
                return r
            if not r["ok"] and first is None:
                first = r
        lines, problem = self.dpkg_audit()
        result = first or {"ok": True, "code": "ok", "exit": 0}
        if result["ok"] and (lines or problem):
            result = {"ok": False, "code": "dpkg_broken" if lines else problem, "exit": 0}       # beide Stufen liefen, aber dpkg meldet noch Probleme
        result["output"] = "\n".join((lines + log if lines and not result["ok"] else log)[-TAIL_LINES:])
        result["log"] = limit_log(log + lines)
        return result

    def verb_power(self, req, send):
        op = req.get("op")
        if op not in ("reboot", "poweroff"):
            return {"ok": False, "code": "invalid_request"}
        delay = req.get("delay")
        delay = max(2, min(30, delay)) if isinstance(delay, int) and not isinstance(delay, bool) else 5
        send({"a": True})                                   # der Agent meldet dem Server "angenommen", bevor der Rechner herunterfaehrt
        self.sleep(delay)
        r, _ = self._run(["systemctl", op], send, 60)
        return r

    def verb_service(self, req, send):
        op, unit = req.get("op"), req.get("unit")
        if op not in ("start", "stop", "restart") or not isinstance(unit, str) or not UNIT_RE.match(unit):
            return {"ok": False, "code": "invalid_request"}
        if op != "start" and unit.startswith("julos-agent"):
            return {"ok": False, "code": "protected_unit"}
        r, _ = self._run(["systemctl", op, "--no-pager", "--", unit], send, 90)
        if r["ok"]:
            rc, tail = self.run(["systemctl", "is-active", "--", unit], lambda line: None, 15)
            r["active"] = tail[-1] if tail else ("" if rc is None else "unknown")
        return r

    def verb_journal(self, req, send):
        unit, lines = req.get("unit"), req.get("lines")
        if not isinstance(unit, str) or not UNIT_RE.match(unit):
            return {"ok": False, "code": "invalid_request"}
        n = max(10, min(LOG_MAX, lines)) if isinstance(lines, int) and not isinstance(lines, bool) else 100
        out = []
        rc, _ = self.run(["journalctl", "--unit=" + unit, "-n", str(n), "--no-pager", "-o", "short-iso", "--quiet"], out.append, 30)
        if rc is None:
            return {"ok": False, "code": "not_installed"}
        if rc != 0:
            return {"ok": False, "code": "journal_failed"}
        return {"ok": True, "lines": out[-n:]}

    def verb_smart_health(self, req, send):
        """SMART-Kurzstatus je Platte, nur lesend. Die Plattenliste bildet der Helfer selbst (lsblk, Typ disk, Namen per Muster geprueft); der Agent nennt nie einen Namen."""
        names, out = [], []
        rc, _ = self.run(["lsblk", "-dn", "-o", "NAME,TYPE"], out.append, 15)
        if rc is None:
            return {"ok": False, "code": "not_installed"}
        for line in out:
            parts = line.split()
            if len(parts) == 2 and parts[1] == "disk" and DEVICE_RE.match(parts[0]):
                names.append(parts[0])
        devices = []
        for name in names[:SMART_MAX_DEVICES]:
            got = []
            rc, _ = self.run(["smartctl", "-H", "-A", "-j", "/dev/" + name], got.append, 25)
            if rc is None:
                return {"ok": False, "code": "not_installed"}          # smartctl fehlt
            devices.append(parse_smart_json("\n".join(got), name))
        return {"ok": True, "devices": devices}


def read_request_line(rfile, timeout=REQUEST_TIMEOUT, limit=65536):
    """Eine Zeile (hoechstens limit Bytes) lesen, aber nur bis zu einer Frist fuer die GANZE Zeile. Wer nichts oder nur tropfenweise sendet, bekommt kein Ergebnis (b"").
    Ohne echten Dateideskriptor (Tests mit BytesIO) wird normal gelesen."""
    try:
        fd = rfile.fileno()
    except (AttributeError, OSError, ValueError):
        return rfile.readline(limit)
    deadline, data = time.monotonic() + timeout, b""
    while len(data) < limit and not data.endswith(b"\n"):
        left = deadline - time.monotonic()
        if left <= 0 or not select.select([fd], [], [], left)[0]:
            return b""
        chunk = os.read(fd, min(4096, limit - len(data)))
        if not chunk:
            break
        data += chunk
        if b"\n" in data:
            data = data[:data.index(b"\n") + 1]
            break
    return data


def serve(rfile, wfile, helper=None, read_timeout=REQUEST_TIMEOUT):
    """Eine Anfrage lesen, bearbeiten, antworten. Bricht der Agent die Verbindung ab, laeuft die Aktion trotzdem zu Ende (ein halbes apt waere schlimmer als ein ungelesenes Ergebnis)."""
    helper = helper or Helper()
    state = {"open": True}

    def send(obj):
        if not state["open"]:
            return
        try:
            wfile.write(json.dumps(obj, separators=(",", ":")).encode() + b"\n")
            wfile.flush()
        except (OSError, ValueError):
            state["open"] = False

    try:
        raw = read_request_line(rfile, read_timeout)
        req = json.loads(raw.decode("utf-8", "replace")) if raw else None
    except ValueError:
        req = None
    if not isinstance(req, dict):
        send({"r": {"ok": False, "code": "invalid_request"}})
        return
    try:
        result = helper.handle(req, send)
    except Exception:  # noqa: BLE001  # nie mit einem Absturztext antworten
        result = {"ok": False, "code": "failed"}
    send({"r": result})


if __name__ == "__main__":
    signal.signal(signal.SIGPIPE, signal.SIG_IGN)
    serve(sys.stdin.buffer, sys.stdout.buffer)
