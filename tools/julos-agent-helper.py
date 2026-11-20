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
  files.*        files-root Dateien als root (docs/FILES.md, "Root"): list, stat, read (Bloecke), create + write (Bloecke, nur in eine neue Datei .julos-*.part), rename, delete, rmdir, mkdir.
                            Nie ueber einen Link hinweg schreiben oder loeschen, Pfade nur absolut ohne "..", nie die Dateien des Agenten selbst (/etc/julos-agent, sein Ordner) veraendern.
Nur die Python-Standardbibliothek.
"""
import base64
import errno
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
RIGHTS = ("updates", "reboot", "services", "docker", "logs", "files-root")
VERB_RIGHT = {"apt.refresh": "updates", "apt.upgrade": "updates", "power": "reboot", "service": "services", "journal": "logs",
              "files.list": "files-root", "files.stat": "files-root", "files.read": "files-root", "files.create": "files-root", "files.write": "files-root",
              "files.rename": "files-root", "files.delete": "files-root", "files.rmdir": "files-root", "files.mkdir": "files-root"}
PKG_RE = re.compile(r"^[a-z0-9](?:[a-z0-9+.\-]*[a-z0-9+.])?(?::[a-z0-9]+)?\Z")       # kein '-' am Ende: "paket-" hiesse bei apt "entfernen"
UNIT_RE = re.compile(r"^(?!-)[A-Za-z0-9:_.@\-]{1,100}\.service\Z")
MAX_PACKAGES = 20
MAX_LINE = 300
MAX_REQUEST = 131072                    # eine Anfragezeile: ein Block (48 KiB) als Base64 ist rund 64 KiB, dazu der Rahmen
FILE_BLOCK = 49152                      # Bytes je Lese- und Schreibblock
MAX_LIST = 2000
MAX_PATH = 4096
MAX_RESOLVE = 200                       # Links einer Liste, deren Ziel geprueft wird
TEMP_RE = re.compile(r"^\.julos-[0-9a-f]{12}\.part\Z")      # die einzigen Dateien, in die dieser Helfer schreibt (create, write); alles andere ersetzt nur ein Umbenennen
PROTECTED = ("/etc/julos-agent", "/usr/local/libexec/julos-agent", "/opt/julos-agent", "/var/lib/julos-agent")      # was den Agenten und seine Rechte ausmacht: nie veraendern
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


# ---------------------------------------------------------------- Dateien als root (Wort files-root)
def norm_path(path):
    """Ein absoluter Pfad in Normalform ("/", "/a/b") oder None: relativ, "..", NUL, Steuerzeichen, zu lang. "." und leere Teile fallen weg."""
    if not isinstance(path, str) or not path.startswith("/") or len(path) > MAX_PATH or any(ord(c) < 32 or ord(c) == 127 for c in path):
        return None
    parts = [p for p in path.split("/") if p not in ("", ".")]
    if ".." in parts or len(parts) > 128:
        return None
    return "/" + "/".join(parts)


def protected(path):
    return any(path == p or path.startswith(p + "/") for p in PROTECTED)


def perm_text(mode):
    return "".join(c if mode & bit else "-" for c, bit in zip("rwxrwxrwx", (0o400, 0o200, 0o100, 0o040, 0o020, 0o010, 0o004, 0o002, 0o001)))


def kind_of(mode):
    return "directory" if stat.S_ISDIR(mode) else "file" if stat.S_ISREG(mode) else "link" if stat.S_ISLNK(mode) else "other"


def os_code(e):
    """Der kurze Fehlercode zu einem Fehler des Betriebssystems (nie dessen Text)."""
    if isinstance(e, FileNotFoundError):
        return "not_found"
    if isinstance(e, PermissionError):
        return "denied"
    if isinstance(e, FileExistsError):
        return "exists"
    if isinstance(e, NotADirectoryError):
        return "not_a_directory"
    if isinstance(e, IsADirectoryError):
        return "is_directory"
    no = getattr(e, "errno", None)
    if no in (errno.ENOTEMPTY, errno.EEXIST):
        return "not_empty"
    if no == errno.EXDEV:
        return "cross_device"
    if no == errno.ELOOP:
        return "is_link"
    return "failed"


class FileOps:
    """Die Dateiverben. root: das Wurzelverzeichnis, unter dem alle Pfade liegen ("/" im Betrieb; Tests setzen ein anderes). Jeder Pfad ist absolut; der Ordner davor wird einmal aufgeloest und
    als Dateideskriptor geoeffnet, der Name dahinter nie als Link verfolgt (O_NOFOLLOW, dir_fd): ein Link wird als Link geloescht, umbenannt oder ersetzt, nie sein Ziel."""

    def __init__(self, root="/"):
        self.root = root

    def real(self, path):
        return os.path.join(self.root, path.lstrip("/")) if self.root != "/" else path

    def _split(self, req, key="path", mutating=False):
        path = norm_path(req.get(key))
        if path is None:
            return None, None, {"ok": False, "code": "invalid_path"}
        if mutating and (path == "/" or protected(path)):
            return None, None, {"ok": False, "code": "protected_path" if path != "/" else "root_forbidden"}
        return path, os.path.dirname(path), None

    def _dirfd(self, parent):
        return os.open(os.path.realpath(self.real(parent)), os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)

    @staticmethod
    def _entry(name, st, resolved=None):
        e = {"name": name, "kind": kind_of(st.st_mode), "size": st.st_size, "mtime": int(st.st_mtime), "mode": perm_text(st.st_mode)}
        if resolved:
            e["resolved"] = resolved
        return e

    def list(self, req):
        path = norm_path(req.get("path"))
        limit = req.get("limit")
        limit = max(1, min(MAX_LIST, limit)) if isinstance(limit, int) and not isinstance(limit, bool) else MAX_LIST
        if path is None:
            return {"ok": False, "code": "invalid_path"}
        entries, hidden, truncated, links = [], 0, False, 0
        try:
            with os.scandir(os.path.realpath(self.real(path))) as it:
                for de in it:
                    if any(ord(c) < 32 or ord(c) == 127 for c in de.name):
                        hidden += 1
                        continue
                    if len(entries) >= limit:
                        truncated = True
                        break
                    try:
                        st = de.stat(follow_symlinks=False)
                    except OSError:
                        continue
                    resolved = None
                    if stat.S_ISLNK(st.st_mode) and links < MAX_RESOLVE:
                        links += 1
                        try:
                            resolved = kind_of(os.stat(de.path).st_mode)
                        except OSError:
                            resolved = None
                    entries.append(self._entry(de.name, st, resolved))
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        return {"ok": True, "path": path, "entries": entries, "truncated": truncated, "hidden": hidden}

    def stat(self, req):
        path, parent, bad = self._split(req)
        if bad:
            return bad
        follow = req.get("follow") is True
        try:
            if path == "/":
                st = os.stat(self.real("/"))
                return {"ok": True, "exists": True, "entry": self._entry("/", st)}
            st = os.stat(self.real(path)) if follow else os.lstat(os.path.join(os.path.realpath(self.real(parent)), os.path.basename(path)))
            resolved = None
            if not follow and stat.S_ISLNK(st.st_mode):
                try:
                    resolved = kind_of(os.stat(self.real(path)).st_mode)
                except OSError:
                    resolved = None
            return {"ok": True, "exists": True, "entry": self._entry(os.path.basename(path), st, resolved)}
        except FileNotFoundError:
            return {"ok": True, "exists": False}
        except OSError as e:
            return {"ok": False, "code": os_code(e)}

    def read(self, req):
        path, _, bad = self._split(req)
        offset, length = req.get("offset"), req.get("length")
        if bad:
            return bad
        if not isinstance(offset, int) or isinstance(offset, bool) or offset < 0 or not isinstance(length, int) or isinstance(length, bool) or not 1 <= length <= FILE_BLOCK:
            return {"ok": False, "code": "invalid_request"}
        try:
            fd = os.open(self.real(path), os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC)         # folgt einem Link (Lesen/Download einer verlinkten Datei); nie blockieren (FIFO)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        try:
            st = os.fstat(fd)
            if not stat.S_ISREG(st.st_mode):
                return {"ok": False, "code": "not_a_file"}
            data = os.pread(fd, length, offset)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        finally:
            os.close(fd)
        return {"ok": True, "data": base64.b64encode(data).decode("ascii"), "n": len(data), "eof": len(data) < length, "size": st.st_size}

    def create(self, req):
        path, parent, bad = self._split(req, mutating=True)
        if bad:
            return bad
        if not TEMP_RE.match(os.path.basename(path)):
            return {"ok": False, "code": "invalid_name"}
        try:
            dfd = self._dirfd(parent)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        try:
            fd = os.open(os.path.basename(path), os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=dfd)
            os.close(fd)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        finally:
            os.close(dfd)
        return {"ok": True}

    def write(self, req):
        path, parent, bad = self._split(req, mutating=True)
        offset, data = req.get("offset"), req.get("data")
        if bad:
            return bad
        if not TEMP_RE.match(os.path.basename(path)):
            return {"ok": False, "code": "invalid_name"}
        if not isinstance(offset, int) or isinstance(offset, bool) or offset < 0 or not isinstance(data, str) or len(data) > FILE_BLOCK * 4 // 3 + 8:
            return {"ok": False, "code": "invalid_request"}
        try:
            raw = base64.b64decode(data.encode("ascii"), validate=True)
        except (ValueError, UnicodeEncodeError):
            return {"ok": False, "code": "invalid_request"}
        if len(raw) > FILE_BLOCK:
            return {"ok": False, "code": "invalid_request"}
        try:
            dfd = self._dirfd(parent)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        try:
            fd = os.open(os.path.basename(path), os.O_WRONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=dfd)      # nie neu anlegen: die Datei muss "create" gemacht haben
            try:
                st = os.fstat(fd)
                if not stat.S_ISREG(st.st_mode) or st.st_size != offset:
                    return {"ok": False, "code": "bad_offset"}                                                # nur der Reihe nach, ohne Luecke und ohne Ueberschreiben
                n = 0
                while n < len(raw):
                    n += os.pwrite(fd, raw[n:], offset + n)
                return {"ok": True, "size": offset + len(raw)}
            finally:
                os.close(fd)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        finally:
            os.close(dfd)

    def rename(self, req):
        src, sparent, bad = self._split(req, "from", mutating=True)
        if bad:
            return bad
        dst, dparent, bad = self._split(req, "to", mutating=True)
        if bad:
            return bad
        overwrite = req.get("overwrite") is True
        try:
            sfd = self._dirfd(sparent)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        dfd = None
        try:
            dfd = self._dirfd(dparent)
            sname, dname = os.path.basename(src), os.path.basename(dst)
            sst = os.lstat(sname, dir_fd=sfd)
            try:
                dst_st = os.lstat(dname, dir_fd=dfd)
            except FileNotFoundError:
                dst_st = None
            if dst_st is not None:
                if not overwrite:
                    return {"ok": False, "code": "exists"}
                if stat.S_ISLNK(dst_st.st_mode) or stat.S_ISLNK(sst.st_mode) or stat.S_ISDIR(dst_st.st_mode) or stat.S_ISDIR(sst.st_mode):
                    return {"ok": False, "code": "type_mismatch"}          # ein Link und ein Ordner wird nie ersetzt
            if TEMP_RE.match(sname) and stat.S_ISREG(sst.st_mode):
                # eine fertig geschriebene Datei: ersetzt sie eine vorhandene, behaelt sie deren Besitzer und Rechte, sonst 0644
                fd = os.open(sname, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=sfd)
                try:
                    if dst_st is not None:
                        os.fchown(fd, dst_st.st_uid, dst_st.st_gid)
                        os.fchmod(fd, stat.S_IMODE(dst_st.st_mode))
                    else:
                        os.fchmod(fd, 0o644)
                    os.fsync(fd)
                finally:
                    os.close(fd)
            os.rename(sname, dname, src_dir_fd=sfd, dst_dir_fd=dfd)
            return {"ok": True}
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        finally:
            os.close(sfd)
            if dfd is not None:
                os.close(dfd)

    def _simple(self, req, op):
        path, parent, bad = self._split(req, mutating=True)
        if bad:
            return bad
        try:
            dfd = self._dirfd(parent)
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        try:
            name = os.path.basename(path)
            if op == "delete":
                os.unlink(name, dir_fd=dfd)                       # entfernt eine Datei oder einen Link, nie dessen Ziel; ein Ordner ist ein Fehler
            elif op == "rmdir":
                os.rmdir(name, dir_fd=dfd)
            else:
                os.mkdir(name, 0o755, dir_fd=dfd)
            return {"ok": True}
        except OSError as e:
            return {"ok": False, "code": os_code(e)}
        finally:
            os.close(dfd)

    def delete(self, req):
        return self._simple(req, "delete")

    def rmdir(self, req):
        return self._simple(req, "rmdir")

    def mkdir(self, req):
        return self._simple(req, "mkdir")


class Helper:
    """run, sleep und permissions sind einsetzbar, damit Tests nie ein echtes Programm starten."""

    def __init__(self, run=real_run, sleep=time.sleep, permissions=read_permissions, fs_root="/"):
        self.run, self.sleep, self.permissions = run, sleep, permissions
        self.files = FileOps(fs_root)

    def handle(self, req, send):
        """Eine Anfrage bearbeiten; send(dict) schreibt eine Antwortzeile. Gibt das Ergebnis (r) zurueck."""
        verb = req.get("verb") if isinstance(req, dict) else None
        if verb not in VERB_RIGHT:
            return {"ok": False, "code": "unknown_verb"}
        if VERB_RIGHT[verb] not in self.permissions():
            return {"ok": False, "code": "not_permitted"}
        if verb.startswith("files."):
            return getattr(self.files, verb[6:])(req)
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


def read_request_line(rfile, timeout=REQUEST_TIMEOUT, limit=MAX_REQUEST):
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
