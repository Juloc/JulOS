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
  apt.upgrade    updates    apt-get upgrade, oder fuer genannte Pakete apt-get install --only-upgrade --no-remove
  power          reboot     systemctl reboot | poweroff, nach "angenommen" und kurzer Verzoegerung
  service        services   systemctl start | stop | restart  <Dienst>  (nicht den Agenten selbst)
  journal        logs       journalctl --unit=<Dienst> -n <N>  (hoechstens 200 Zeilen)
Nur die Python-Standardbibliothek.
"""
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
RIGHTS = ("updates", "reboot", "services", "docker", "logs")
VERB_RIGHT = {"apt.refresh": "updates", "apt.upgrade": "updates", "power": "reboot", "service": "services", "journal": "logs"}
PKG_RE = re.compile(r"^[a-z0-9](?:[a-z0-9+.\-]*[a-z0-9+.])?(?::[a-z0-9]+)?\Z")       # kein '-' am Ende: "paket-" hiesse bei apt "entfernen"
UNIT_RE = re.compile(r"^(?!-)[A-Za-z0-9:_.@\-]{1,100}\.service\Z")
MAX_PACKAGES = 20
MAX_LINE = 300
REQUEST_TIMEOUT = 15                    # Sekunden bis die ganze Anfrage-Zeile da sein muss (sonst haelt ein stummer Client einen der wenigen Plaetze, MaxConnections=4, fuer immer)
TAIL_LINES = 40
LOG_MAX = 200
ENV = {"PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C", "LANG": "C", "DEBIAN_FRONTEND": "noninteractive",
       "APT_LISTCHANGES_FRONTEND": "none", "NEEDRESTART_MODE": "l", "NEEDRESTART_SUSPEND": "1"}      # needrestart nur melden, nie Dienste von selbst neu starten


def clean(text, limit=MAX_LINE):
    return "".join(c for c in str(text) if c.isprintable()).strip()[:limit]


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
    """Ein festes Programm ohne Shell; Ausgabe (stdout und stderr zusammen) Zeile fuer Zeile an on_line. (Rueckgabewert, letzte Zeilen); (None, []) wenn es nicht startet, (-1, ...) bei Zeitueberschreitung."""
    try:
        p = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=dict(ENV), close_fds=True)
    except OSError:
        return None, []
    tail, end = [], time.time() + timeout
    try:
        for raw in iter(p.stdout.readline, b""):
            line = clean(raw.decode("utf-8", "replace"))
            if line:
                tail = (tail + [line])[-TAIL_LINES:]
                on_line(line)
            if time.time() > end:
                p.kill()
                p.wait()
                return -1, tail
        return p.wait(), tail
    finally:
        try:
            p.stdout.close()
        except OSError:
            pass


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
        if rc == -1:
            return {"ok": False, "code": "timeout", "output": "\n".join(tail)}, tail
        return {"ok": rc == 0, "code": "ok" if rc == 0 else self._code(argv, tail), "exit": rc, "output": "\n".join(tail)}, tail

    @staticmethod
    def _code(argv, tail):
        text = "\n".join(tail).lower()
        if "could not get lock" in text or "unable to acquire the dpkg" in text:
            return "apt_locked"
        return {"apt-get": "apt_failed", "systemctl": "systemctl_failed", "journalctl": "journal_failed"}.get(os.path.basename(argv[0]), "action_failed")

    def verb_apt_refresh(self, req, send):
        r, _ = self._run(["apt-get", "update", "-o", "DPkg::Lock::Timeout=60"], send, 600)
        return r

    def verb_apt_upgrade(self, req, send):
        packages = req.get("packages")
        common = ["-y", "-o", "Dpkg::Options::=--force-confold", "-o", "Dpkg::Options::=--force-confdef", "-o", "DPkg::Lock::Timeout=60"]
        if packages is None:
            argv = ["apt-get", "upgrade"] + common            # entfernt nie ein Paket und installiert nichts Neues
        else:
            if not isinstance(packages, list) or not 1 <= len(packages) <= MAX_PACKAGES or not all(isinstance(p, str) and PKG_RE.match(p) for p in packages):
                return {"ok": False, "code": "invalid_request"}
            # nur ein Upgrade vorhandener Pakete, nichts entfernen; die Namen beginnen nie mit '-' (siehe PKG_RE)
            argv = ["apt-get", "install", "--only-upgrade", "--no-remove"] + common + list(dict.fromkeys(packages))
        r, _ = self._run(argv, send, 3600)
        return r

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
