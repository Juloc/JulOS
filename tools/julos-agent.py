#!/usr/bin/env python3
"""JulOS Agent fuer Linux (X11, XFCE getestet)

Laeuft in der grafischen Sitzung und stellt dem Server die "Agent-API v1" bereit
(siehe docs/AGENT_API.md): Fensterliste, Fenster aktivieren/schliessen, Programmliste und
Programmstart, den Programm-Modus (Taskleiste der Sitzung ausblenden) sowie Foto, Datei und Zwischenablage
fuer den Helfer julos-clip.py.

Zwei Wege zum Server, beide mit denselben Anfragen:
- Netzwerk (Standard fuer entfernte Maschinen): der Agent meldet sich von hier aus beim Server (WebSocket, TLS mit den System-
  Zertifikaten). Eingerichtet wird er einmal mit dem Code aus der App:
      julos-agent.py --server https://os.example.org --code ABCDE-FGHIJ
  Das Token steht danach in ~/.config/julos/agent.json (Rechte 0600). Auf dem Rechner lauscht nichts.
- Unix-Socket (Server und Desktop laufen auf demselben Rechner): ~/julos-uploads/.agent/agent.sock (Rechte 0600),
  kein Netzwerkport. Laeuft zusaetzlich zum Netzwerkweg, ausser mit --no-socket.

Nur die Python-Standardbibliothek (plus PyGObject fuer die Sitzung): die Installation bleibt eine Datei.
"""
import argparse, base64, hashlib, http.server, json, os, queue, random, re, shutil, signal, socket, socketserver, ssl, struct, subprocess, sys, threading, time
import urllib.error, urllib.parse, urllib.request

AGENT_VERSION = "0.3.0"      # der Server lehnt aeltere ab (AgentLinks.MinAgentVersion)
API_VERSION = 1
PLATFORM = "linux-x11"
CONFIG = os.environ.get("JULOS_AGENT_CONFIG") or os.path.expanduser("~/.config/julos/agent.json")
UPLOADS = os.path.expanduser("~/julos-uploads")        # den Ordner beobachtet der Zwischenablage-Helfer (julos-clip.py)


# ---------------------------------------------------------------- Einrichtung (ohne Anzeige, vor dem Laden von GTK)
def load_config():
    try:
        with open(CONFIG) as f:
            c = json.load(f)
        return c if isinstance(c, dict) and c.get("server") and c.get("token") else None
    except (OSError, ValueError):
        return None


def save_config(cfg):
    os.makedirs(os.path.dirname(CONFIG), mode=0o700, exist_ok=True)
    tmp = CONFIG + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)      # nie kurz lesbar fuer andere
    with os.fdopen(fd, "w") as f:
        json.dump(cfg, f)
    os.replace(tmp, CONFIG)


def server_url(text, allow_http):
    u = urllib.parse.urlsplit(text.strip())
    if u.scheme not in ("https", "http") or not u.hostname or u.username or u.password or u.query or u.fragment:
        raise SystemExit("Adresse ungueltig (https://name oder https://name:port)")
    if u.scheme == "http" and not (allow_http or u.hostname in ("localhost", "127.0.0.1", "::1")):
        raise SystemExit("Unverschluesselte Adresse (http) abgelehnt: das Token wuerde offen uebertragen. Nur zum Testen: --allow-http")
    return urllib.parse.urlunsplit((u.scheme, u.netloc, u.path.rstrip("/"), "", ""))


def enroll(server, code, allow_http):
    """Tauscht den einmaligen Code aus der App gegen ein Token und merkt es (0600)."""
    base = server_url(server, allow_http)
    body = json.dumps({"code": code, "version": AGENT_VERSION, "platform": PLATFORM}).encode()
    req = urllib.request.Request(base + "/api/agent-link/enroll", data=body, headers={"Content-Type": "application/json", "User-Agent": "julos-agent/" + AGENT_VERSION})
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            ans = json.load(r)
    except urllib.error.HTTPError as e:
        try:
            msg = json.load(e).get("error", "")
        except ValueError:
            msg = ""
        raise SystemExit("Einrichtung abgelehnt (%d): %s" % (e.code, msg or "unbekannter Fehler"))
    except (urllib.error.URLError, OSError, ValueError) as e:
        raise SystemExit("Server nicht erreichbar: %s" % getattr(e, "reason", e))
    token = ans.get("token")
    if not isinstance(token, str) or not token.startswith("jat_"):
        raise SystemExit("Unerwartete Antwort des Servers")
    save_config({"server": base, "token": token, "connection": ans.get("connection"), "allow_http": bool(allow_http)})
    print("Eingerichtet. Der Agent meldet sich ab jetzt selbst bei", base, flush=True)


def parse_args():
    p = argparse.ArgumentParser(description="JulOS Agent (siehe docs/AGENT_API.md)")
    p.add_argument("--server", help="Adresse des JulOS-Servers (nur zusammen mit --code)")
    p.add_argument("--code", help="Einrichtungscode aus der App (Agent einrichten); danach endet das Programm")
    p.add_argument("--allow-http", action="store_true", help="nur zum Testen: auch ohne TLS verbinden")
    p.add_argument("--no-socket", action="store_true", help="den lokalen Unix-Socket nicht anbieten")
    p.add_argument("--forget", action="store_true", help="gespeicherte Einrichtung loeschen und beenden")
    return p.parse_args()


ARGS = parse_args() if __name__ == "__main__" else argparse.Namespace(server=None, code=None, allow_http=False, no_socket=False, forget=False)
if ARGS.forget:
    try:
        os.unlink(CONFIG)
    except OSError:
        pass
    print("Einrichtung geloescht.", flush=True)
    sys.exit(0)
if ARGS.code or ARGS.server:
    if not (ARGS.code and ARGS.server):
        sys.exit("--server und --code gehoeren zusammen")
    enroll(ARGS.server, ARGS.code, ARGS.allow_http)
    sys.exit(0)

import gi
gi.require_version("Gtk", "3.0"); gi.require_version("Wnck", "3.0"); gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gtk, Gdk, Wnck, GLib, GdkPixbuf, Gio

SOCK_DIR = os.environ.get("JULOS_AGENT_DIR") or os.path.join(UPLOADS, ".agent")   # im Upload-Ordner, den ein Server auf demselben Rechner einbindet
SOCK = os.path.join(SOCK_DIR, "agent.sock")
STATE = os.path.expanduser("~/.cache/julos-agent-panels.json")
IDLE_RESTORE_S = 90          # Programm-Modus faellt zurueck, wenn so lange niemand mehr anfragt

Wnck.set_client_type(Wnck.ClientType.PAGER)   # Aktivierungen gelten als "Pager" (Fokus-Schutz des Fenstermanagers greift nicht)
screen = Wnck.Screen.get_default()
screen.force_update()
theme = Gtk.IconTheme.get_default()

mode = "desktop"
last_request = time.time()
icon_cache = {}            # Schluessel -> (zeit, base64-PNG)
apps_cache = {"t": 0, "data": []}


# ---------------------------------------------------------------- Hilfen
def pixbuf_b64(pb, size):
    if pb is None:
        return ""
    if pb.get_width() != size or pb.get_height() != size:
        pb = pb.scale_simple(size, size, GdkPixbuf.InterpType.BILINEAR)
    ok, buf = pb.save_to_bufferv("png", [], [])
    return base64.b64encode(buf).decode() if ok else ""


def window_icon(w):
    key = (w.get_class_group_name() or "") + "|" + (w.get_class_instance_name() or "") or str(w.get_xid())
    hit = icon_cache.get(key)
    if hit and time.time() - hit[0] < 300:
        return hit[1]
    b64 = pixbuf_b64(w.get_icon(), 32)
    icon_cache[key] = (time.time(), b64)
    return b64


def gicon_b64(gicon):
    if gicon is None:
        return ""
    try:
        info = theme.lookup_by_gicon(gicon, 32, 0)
        return pixbuf_b64(info.load_icon(), 32) if info else ""
    except Exception:  # noqa: BLE001
        return ""


def find_window(xid):
    for w in screen.get_windows():
        if w.get_xid() == xid:
            return w
    return None


def list_windows():
    screen.force_update()
    active = screen.get_active_window()
    out = []
    for w in screen.get_windows():
        if w.is_skip_tasklist():
            continue
        if w.get_window_type() not in (Wnck.WindowType.NORMAL, Wnck.WindowType.DIALOG, Wnck.WindowType.UTILITY):
            continue
        cls = (w.get_class_group_name() or "")
        out.append({
            "id": w.get_xid(),
            "title": w.get_name() or "",
            "app": (w.get_application().get_name() if w.get_application() else "") or cls,
            "class": cls,
            "pid": w.get_pid(),
            "minimized": w.is_minimized(),
            "maximized": w.is_maximized(),
            "active": bool(active and active.get_xid() == w.get_xid()),
            "icon": window_icon(w),
        })
    sc = Gdk.Screen.get_default()
    return {"api": API_VERSION, "mode": mode, "screen": {"w": sc.get_width(), "h": sc.get_height()}, "windows": out}


def activate_window(xid, maximize=True):
    w = find_window(xid)
    if not w:
        return False
    ts = Gtk.get_current_event_time()
    if w.is_minimized():
        w.unminimize(ts)
    if maximize:
        w.maximize()
    w.activate(ts)
    return True


def list_apps():
    if time.time() - apps_cache["t"] < 300 and apps_cache["data"]:
        return apps_cache["data"]
    out = []
    for a in Gio.AppInfo.get_all():
        if not a.should_show():
            continue
        out.append({"id": a.get_id(), "name": a.get_name(), "icon": gicon_b64(a.get_icon())})
    out.sort(key=lambda x: x["name"].lower())
    apps_cache.update(t=time.time(), data=out)
    return out


def launch_app(app_id):
    a = Gio.DesktopAppInfo.new(app_id)
    if not a:
        return False
    a.launch([], None)
    return True


# ---------------------------------------------------------------- Programm-Modus (Panels ausblenden)
def xfconf(*args):
    return subprocess.run(["xfconf-query", "-c", "xfce4-panel", *args], capture_output=True, text=True, timeout=5)


def panel_ids():
    r = xfconf("-l")
    return sorted(set(re.findall(r"^/panels/(panel-\d+)/", r.stdout, re.M)))


def set_mode(new):
    global mode
    if new == mode:
        return mode
    if new == "apps":
        saved = {}
        for pid in panel_ids():
            r = xfconf("-p", f"/panels/{pid}/autohide-behavior")
            saved[pid] = int(r.stdout.strip()) if r.returncode == 0 and r.stdout.strip().lstrip("-").isdigit() else None
            xfconf("-p", f"/panels/{pid}/autohide-behavior", "-n", "-t", "int", "-s", "2")   # 2 = immer ausgeblendet
        os.makedirs(os.path.dirname(STATE), exist_ok=True)
        with open(STATE, "w") as f:
            json.dump(saved, f)
        mode = "apps"
    else:
        restore_panels()
        mode = "desktop"
    return mode


def restore_panels():
    try:
        with open(STATE) as f:
            saved = json.load(f)
    except (OSError, ValueError):
        saved = {pid: None for pid in panel_ids()}
    for pid, val in saved.items():
        if val is None:
            xfconf("-p", f"/panels/{pid}/autohide-behavior", "-r")
        else:
            xfconf("-p", f"/panels/{pid}/autohide-behavior", "-n", "-t", "int", "-s", str(val))
    try:
        os.unlink(STATE)
    except OSError:
        pass


def idle_check():
    if mode == "apps" and time.time() - last_request > IDLE_RESTORE_S:
        set_mode("desktop")
    return True


# ---------------------------------------------------------------- Aufrufe im GTK-Hauptthread
def on_main(fn):
    box = queue.Queue(1)

    def run():
        try:
            box.put(("ok", fn()))
        except Exception as e:  # noqa: BLE001
            box.put(("err", str(e)))
        return False
    GLib.idle_add(run)
    kind, val = box.get(timeout=10)
    if kind == "err":
        raise RuntimeError(val)
    return val


# ---------------------------------------------------------------- Die Anfragen der Agent-API v1 (fuer beide Wege gleich)
def api(method, path, b):
    """Beantwortet eine Anfrage der Agent-API v1 (docs/AGENT_API.md). Gibt (Status, Objekt) zurueck. Nur feste Pfade."""
    global last_request
    last_request = time.time()
    path = path.split("?")[0]
    if not isinstance(b, dict):
        b = {}
    try:
        if method == "GET":
            if path == "/v1/health":
                return 200, {"ok": True, "api": API_VERSION, "platform": PLATFORM, "mode": mode}
            if path == "/v1/windows":
                return 200, on_main(list_windows)
            if path == "/v1/apps":
                return 200, {"apps": on_main(list_apps)}
            return 404, {"error": "unbekannt"}
        if method == "POST":
            m = re.fullmatch(r"/v1/windows/(\d+)/(activate|close|minimize)", path)
            if m:
                xid, act = int(m.group(1)), m.group(2)

                def do():
                    w = find_window(xid)
                    if not w:
                        return False
                    ts = Gtk.get_current_event_time()
                    if act == "activate":
                        return activate_window(xid, bool(b.get("maximize", True)))
                    if act == "close":
                        w.close(ts)
                    else:
                        w.minimize()
                    return True
                ok = on_main(do)
                return (200 if ok else 404), {"ok": bool(ok)}
            if path == "/v1/apps/launch":
                ok = on_main(lambda: launch_app(str(b.get("id", ""))[:200]))
                return (200 if ok else 404), {"ok": bool(ok)}
            if path == "/v1/mode":
                new = b.get("mode")
                if new not in ("apps", "desktop"):
                    return 400, {"error": "mode: apps oder desktop"}
                return 200, {"ok": True, "mode": on_main(lambda: set_mode(new))}
        return 404, {"error": "unbekannt"}
    except Exception as e:  # noqa: BLE001
        return 500, {"error": str(e)[:120]}


# ---------------------------------------------------------------- HTTP ueber Unix-Socket (Server und Desktop auf demselben Rechner)
class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "julos-agent/1"

    def log_message(self, *a):  # ruhig
        pass

    def address_string(self):
        return "unix"

    def reply(self, code, obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n > 8192:
            return {}
        try:
            return json.loads(self.rfile.read(n) or b"{}")
        except ValueError:
            return {}

    def do_GET(self):
        self.reply(*api("GET", self.path, {}))

    def do_POST(self):
        self.reply(*api("POST", self.path, self.body()))


class Server(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True
    allow_reuse_address = True


def serve():
    os.makedirs(SOCK_DIR, mode=0o700, exist_ok=True)
    os.chmod(SOCK_DIR, 0o700)
    try:
        os.unlink(SOCK)
    except OSError:
        pass
    srv = Server(SOCK, Handler)
    os.chmod(SOCK, 0o600)
    srv.serve_forever()


# ---------------------------------------------------------------- WebSocket-Client (RFC 6455, nur was gebraucht wird)
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
IDLE_S = 75                  # so lange darf der Server schweigen (er pingt alle 30 s)
HEARTBEAT_S = 20


class WsError(Exception):
    pass


class WsClosed(WsError):
    pass


class WsBye(WsError):
    """Der Server nimmt diesen Agenten nicht an (zu alt)."""


class WsRejected(WsError):
    def __init__(self, status):
        super().__init__("Server lehnt ab (HTTP %d)" % status)
        self.status = status


def _xor(data, mask):
    n = len(data)
    if not n:
        return b""
    return (int.from_bytes(data, "big") ^ int.from_bytes((mask * (n // 4 + 1))[:n], "big")).to_bytes(n, "big")


class WsClient:
    def __init__(self, sock, rest=b""):
        self.sock = sock
        self.buf = bytearray(rest)
        self.send_lock = threading.Lock()

    @classmethod
    def connect(cls, base, headers, allow_http=False):
        u = urllib.parse.urlsplit(base)
        tls = u.scheme == "https"
        if not tls and not (allow_http or u.hostname in ("localhost", "127.0.0.1", "::1")):
            raise WsError("unverschluesselte Verbindung abgelehnt")
        port = u.port or (443 if tls else 80)
        raw = socket.create_connection((u.hostname, port), timeout=15)
        try:
            # Zertifikat und Name werden mit den System-Zertifikaten geprueft (kein "unsicher"-Schalter)
            sock = ssl.create_default_context().wrap_socket(raw, server_hostname=u.hostname) if tls else raw
            key = base64.b64encode(os.urandom(16)).decode()
            lines = ["GET %s/api/agent-link HTTP/1.1" % u.path, "Host: %s" % u.netloc, "Upgrade: websocket", "Connection: Upgrade",
                     "Sec-WebSocket-Key: " + key, "Sec-WebSocket-Version: 13", "User-Agent: julos-agent/" + AGENT_VERSION]
            lines += ["%s: %s" % kv for kv in headers.items()]
            sock.sendall(("\r\n".join(lines) + "\r\n\r\n").encode())
            data = b""
            while b"\r\n\r\n" not in data:
                chunk = sock.recv(4096)
                if not chunk or len(data) > 16384:
                    raise WsError("ungueltige Antwort des Servers")
                data += chunk
            head, _, rest = data.partition(b"\r\n\r\n")
            status_line, *hdr_lines = head.decode("latin-1").split("\r\n")
            parts = status_line.split(" ", 2)
            status = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else 0
            if status != 101:
                raise WsRejected(status)
            hdrs = {k.strip().lower(): v.strip() for k, _, v in (l.partition(":") for l in hdr_lines)}
            want = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
            if hdrs.get("sec-websocket-accept") != want:
                raise WsError("Antwort des Servers gehoert nicht zu dieser Anfrage")
            sock.settimeout(IDLE_S)
            return cls(sock, rest)
        except BaseException:
            raw.close()
            raise

    def _read(self, n):
        while len(self.buf) < n:
            try:
                chunk = self.sock.recv(65536)
            except socket.timeout:
                raise WsError("Server antwortet nicht")
            if not chunk:
                raise WsClosed("Verbindung beendet")
            self.buf += chunk
        out = bytes(self.buf[:n])
        del self.buf[:n]
        return out

    def send_frame(self, op, payload=b""):
        n = len(payload)
        head = bytearray([0x80 | op])
        if n < 126:
            head.append(0x80 | n)
        elif n < 65536:
            head.append(0x80 | 126); head += struct.pack(">H", n)
        else:
            head.append(0x80 | 127); head += struct.pack(">Q", n)
        mask = os.urandom(4)
        with self.send_lock:           # Client-Frames muessen maskiert sein
            self.sock.sendall(bytes(head) + mask + _xor(payload, mask))

    def send_text(self, text):
        self.send_frame(0x1, text.encode())

    def recv(self, max_size):
        """Eine ganze Nachricht (op, Bytes); antwortet auf Pings, endet mit WsClosed bei Close."""
        msg, op0 = bytearray(), None
        while True:
            b1, b2 = self._read(2)
            fin, op, ln = b1 & 0x80, b1 & 0x0F, b2 & 0x7F
            if ln == 126:
                ln = struct.unpack(">H", self._read(2))[0]
            elif ln == 127:
                ln = struct.unpack(">Q", self._read(8))[0]
            mask = self._read(4) if b2 & 0x80 else None
            if ln > max_size:
                raise WsError("Nachricht zu gross")
            payload = self._read(ln)
            if mask:
                payload = _xor(payload, mask)
            if op >= 8:
                if op == 8:
                    try:
                        self.send_frame(8, payload[:2])
                    except OSError:
                        pass
                    raise WsClosed("Server hat die Verbindung beendet")
                if op == 9:
                    self.send_frame(0xA, payload)
                continue
            if op != 0:
                op0, msg = op, bytearray()
            msg += payload
            if len(msg) > max_size:
                raise WsError("Nachricht zu gross")
            if fin:
                return op0, bytes(msg)

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass


# ---------------------------------------------------------------- Foto, Datei und Zwischenablage (Ordner des Helfers julos-clip.py)
CHUNK = 48 * 1024
MAX_IMAGE = 14 * 1024 * 1024
MAX_FILE = 64 * 1024 * 1024
HELPER_WAIT_S = 8
PNG_SIG = b"\x89PNG\r\n\x1a\n"
BLOCKED_EXT = (".desktop", ".lnk", ".scf", ".url", ".bat", ".cmd", ".exe", ".msi", ".com", ".sh", ".ps1", ".vbs", ".js")


def image_ext(head):
    if head.startswith(PNG_SIG):
        return "png"
    if head[:3] == b"\xff\xd8\xff":
        return "jpg"
    if head[:4] == b"RIFF" and head[8:12] == b"WEBP":
        return "webp"
    if head[:6] in (b"GIF87a", b"GIF89a"):
        return "gif"
    return None


def clean_name(raw):
    """Nur ein einfacher Dateiname (auch wenn der Server ihn schon bereinigt hat: dem Netz wird nicht vertraut)."""
    name = str(raw).replace("\\", "/").split("/")[-1]
    name = "".join("_" if (ord(c) < 32 or c in '<>:"|?*\x7f') else c for c in name).strip(" ").rstrip(". ")
    if not name or name.startswith(".") or set(name) <= {".", "_"} or len(name) > 120:
        return None
    if name.lower().endswith(BLOCKED_EXT):
        return None
    return name


def new_id():
    return "%d-%s" % (int(time.time() * 1000), os.urandom(4).hex())


def wait_for(paths, seconds=HELPER_WAIT_S):
    """Wartet, bis der Helfer eine der Dateien anlegt; gibt den Index zurueck oder None."""
    until = time.time() + seconds
    while time.time() < until:
        for i, p in enumerate(paths):
            if os.path.exists(p):
                return i
        time.sleep(0.05)
    return None


def read_detail(path):
    try:
        with open(path, errors="replace") as f:
            return "".join(c for c in f.read(120) if c.isprintable())[:80]
    except OSError:
        return ""


def rm(path):
    try:
        if os.path.isdir(path):
            shutil.rmtree(path, ignore_errors=True)
        else:
            os.unlink(path)
    except OSError:
        pass


def sweep_leftovers():
    """Angefangene Uebertragungen, die ein Abbruch liegen liess (.tmp), nach einer Stunde wegraeumen."""
    now = time.time()
    for d in (UPLOADS, os.path.join(UPLOADS, "files")):
        try:
            for n in os.listdir(d):
                p = os.path.join(d, n)
                if n.endswith(".tmp") and now - os.path.getmtime(p) > 3600:
                    rm(p)
        except OSError:
            pass


def deliver_image(tmp):
    """Das fertig empfangene Bild dem Helfer geben und sein Ergebnis abwarten."""
    with open(tmp, "rb") as f:
        ext = image_ext(f.read(16))
    if not ext:
        rm(tmp)
        return 400, {"error": "Kein gueltiges Bild"}
    final = os.path.join(UPLOADS, "%s.%s" % (new_id(), ext))
    os.rename(tmp, final)                       # erst fertig, dann sichtbar (der Helfer ignoriert .tmp)
    hit = wait_for([final + ".ok", final + ".err"])
    if hit == 0:
        rm(final + ".ok")
        return 200, {"ok": True}
    if hit == 1:
        detail = read_detail(final + ".err")
        rm(final + ".err"); rm(final)
        return 500, {"error": detail or "Helfer meldet einen Fehler"}
    rm(final)                                   # niemand hat sich gemeldet: nicht liegen lassen
    return 504, {"error": "Der Helfer antwortet nicht (laeuft julos-clip.py?)"}


def deliver_file(tmpdir, finaldir):
    os.rename(tmpdir, finaldir)
    hit = wait_for([os.path.join(finaldir, ".ok"), os.path.join(finaldir, ".err")])
    if hit == 0:
        return 200, {"ok": True}
    if hit == 1:
        detail = read_detail(os.path.join(finaldir, ".err"))
        rm(finaldir)
        return 500, {"error": detail or "Helfer meldet einen Fehler"}
    rm(finaldir)
    return 504, {"error": "Der Helfer antwortet nicht (laeuft julos-clip.py?)"}


def ask_clipboard_image():
    """Bild aus der Zwischenablage ueber den Helfer holen: (Status, Bytes oder Fehlertext)."""
    pull = os.path.join(UPLOADS, "pull")
    os.makedirs(pull, exist_ok=True)
    base = os.path.join(pull, new_id())
    with open(base + ".req.tmp", "wb"):
        pass
    os.rename(base + ".req.tmp", base + ".req")
    answers = [base + ".png", base + ".none", base + ".err"]
    try:
        hit = wait_for(answers)
        if hit == 0:
            with open(answers[0], "rb") as f:
                data = f.read(MAX_IMAGE + 1)
            if len(data) > MAX_IMAGE:
                return 500, "Bild zu gross"
            return (200, data) if data.startswith(PNG_SIG) else (500, "kein Bild")
        if hit == 1:
            return 204, b""
        if hit == 2:
            return 500, read_detail(answers[2]) or "Helfer meldet einen Fehler"
        return 504, "Der Helfer antwortet nicht (laeuft julos-clip.py?)"
    finally:
        for p in answers + [base + ".req"]:
            rm(p)


# ---------------------------------------------------------------- Die Verbindung zum Server
class Link:
    """Haelt die ausgehende WebSocket-Verbindung zum Server (mit Wiederverbinden) und beantwortet seine Anfragen."""

    def __init__(self, cfg):
        self.cfg = cfg
        self.ws = None
        self.put = None                       # die gerade ankommende Uebertragung
        self.cancelled = set()
        self.workers = threading.BoundedSemaphore(6)

    def log(self, *a):
        print(time.strftime("%H:%M:%S"), "Link:", *a, flush=True)

    def run(self):
        delay, nap = 2, 0
        while True:
            started = time.time()
            try:
                self.session()
            except WsRejected as e:
                if e.status == 401:
                    nap = 300
                    self.log("Token abgelehnt: der Agent wurde in der App entfernt? Neu einrichten mit --server und --code.")
                else:
                    self.log(str(e))
            except WsBye as e:
                nap = 600
                self.log(str(e))
            except (WsError, OSError, ssl.SSLError, ValueError) as e:
                self.log("getrennt:", e)
            self.ws = None
            self.abort_put()
            if nap:
                time.sleep(nap); nap = 0
                continue
            delay = 2 if time.time() - started > 30 else min(delay * 2, 60)       # nach langer Verbindung sofort wieder, sonst langsamer
            time.sleep(delay * random.uniform(0.7, 1.3))

    def session(self):
        ws = WsClient.connect(self.cfg["server"], {"Authorization": "Bearer " + self.cfg["token"]}, bool(self.cfg.get("allow_http")))
        self.ws = ws
        try:
            ws.send_text(json.dumps({"t": "hello", "agent": AGENT_VERSION, "platform": PLATFORM, "api": API_VERSION}))
            stop = threading.Event()
            threading.Thread(target=self.heartbeat, args=(ws, stop), daemon=True).start()
            try:
                while True:
                    op, data = ws.recv(70 * 1024)
                    if op == 2:
                        self.on_binary(data)
                    else:
                        self.on_text(json.loads(data.decode()))
            finally:
                stop.set()
        finally:
            ws.close()

    def heartbeat(self, ws, stop):
        while not stop.wait(HEARTBEAT_S):
            try:
                ws.send_text('{"t":"hb"}')
            except OSError:
                return

    def send(self, obj):
        ws = self.ws
        if ws:
            ws.send_text(json.dumps(obj, separators=(",", ":")))

    def reply(self, id, status, body):
        try:
            self.send({"t": "res", "id": id, "status": status, "body": body})
        except OSError:
            pass

    def background(self, id, fn):
        """Langsames (GTK-Hauptthread, Helfer abwarten) neben dem Empfang erledigen."""
        if not self.workers.acquire(blocking=False):
            return self.reply(id, 429, {"error": "zu viele gleichzeitige Anfragen"})

        def run():
            try:
                fn()
            except Exception as e:  # noqa: BLE001
                self.reply(id, 500, {"error": str(e)[:120]})
            finally:
                self.workers.release()
        threading.Thread(target=run, daemon=True).start()

    # ---- Nachrichten des Servers
    def on_text(self, m):
        t, id = m.get("t"), m.get("id")
        if t == "welcome":
            self.log("verbunden mit Server", m.get("server", "?"))
        elif t == "bye":
            raise WsBye("Server weist diesen Agenten ab (%s): bitte aktualisieren" % m.get("reason", "?"))
        elif t == "req" and isinstance(id, int):
            method, path, body = m.get("method"), m.get("path"), m.get("body")
            if method in ("GET", "POST") and isinstance(path, str) and path.startswith("/v1/"):
                self.background(id, lambda: self.reply(id, *api(method, path, body)))
            else:
                self.reply(id, 400, {"error": "ungueltig"})
        elif t == "put" and isinstance(id, int):
            self.start_put(id, m)
        elif t == "end" and isinstance(id, int):
            self.end_put(id)
        elif t == "pull" and isinstance(id, int):
            self.background(id, lambda: self.do_pull(id, int(m.get("max") or MAX_IMAGE)))
        elif t == "cancel" and isinstance(id, int):
            self.cancelled.add(id)
            if self.put and self.put["id"] == id:
                self.abort_put()

    def on_binary(self, data):
        p = self.put
        if len(data) < 4 or not p or struct.unpack(">I", data[:4])[0] != p["id"]:
            return                                   # gehoert zu etwas, das schon abgebrochen wurde
        p["got"] += len(data) - 4
        if p["got"] > p["size"]:
            self.reply(p["id"], 413, {"error": "mehr Daten als angekuendigt"})
            return self.abort_put()
        p["f"].write(data[4:])

    # ---- Foto und Datei vom Server
    def abort_put(self):
        p, self.put = self.put, None
        if p:
            try:
                p["f"].close()
            except OSError:
                pass
            rm(p["tmp"])

    def start_put(self, id, m):
        kind, size, name = m.get("kind"), m.get("size"), m.get("name", "")
        self.abort_put()
        if kind not in ("image", "file") or not isinstance(size, int) or size <= 0 or size > (MAX_IMAGE if kind == "image" else MAX_FILE):
            return self.reply(id, 413 if isinstance(size, int) and size > 0 else 400, {"error": "Groesse oder Art ungueltig"})
        try:
            if kind == "image":
                os.makedirs(UPLOADS, exist_ok=True)
                tmp = os.path.join(UPLOADS, new_id() + ".img.tmp")
                final = None
            else:
                safe = clean_name(name)
                if not safe:
                    return self.reply(id, 400, {"error": "Dateiname nicht erlaubt"})
                files = os.path.join(UPLOADS, "files")
                fid = new_id()
                os.makedirs(os.path.join(files, fid + ".tmp"), exist_ok=True)
                tmp = os.path.join(files, fid + ".tmp")
                final = (os.path.join(files, fid), safe)
            path = tmp if kind == "image" else os.path.join(tmp, final[1])
            if os.path.commonpath([os.path.realpath(path), os.path.realpath(UPLOADS)]) != os.path.realpath(UPLOADS):
                return self.reply(id, 400, {"error": "Pfad nicht erlaubt"})
            f = open(path, "xb")
        except OSError as e:
            return self.reply(id, 500, {"error": "Ordner nicht beschreibbar: %s" % e.strerror})
        self.cancelled.discard(id)
        self.put = {"id": id, "kind": kind, "size": size, "got": 0, "f": f, "tmp": tmp, "final": final}

    def end_put(self, id):
        p = self.put
        if not p or p["id"] != id:
            return
        self.put = None
        p["f"].close()
        if p["got"] != p["size"]:
            rm(p["tmp"])
            return self.reply(id, 400, {"error": "Daten unvollstaendig"})

        def done():
            if p["kind"] == "image":
                status, body = deliver_image(p["tmp"])
            else:
                status, body = deliver_file(p["tmp"], p["final"][0])
            self.reply(id, status, body)
        self.background(id, done)

    # ---- Bild aus der Zwischenablage zum Server
    def do_pull(self, id, limit):
        status, data = ask_clipboard_image()
        if status != 200:
            return self.reply(id, status, {} if status == 204 else {"error": data})
        if len(data) > min(limit, MAX_IMAGE):
            return self.reply(id, 500, {"error": "Bild zu gross"})
        self.send({"t": "blob", "id": id, "size": len(data)})
        head = struct.pack(">I", id)
        for off in range(0, len(data), CHUNK):
            if id in self.cancelled:
                self.cancelled.discard(id)
                return
            self.ws.send_frame(0x2, head + data[off:off + CHUNK])
        self.reply(id, 200, {})


def cleanup(*_):
    try:
        if mode == "apps":
            restore_panels()
    finally:
        sys.exit(0)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, cleanup)
    signal.signal(signal.SIGINT, cleanup)
    # Nach einem Absturz im Programm-Modus: Panels wiederherstellen
    if os.path.exists(STATE):
        restore_panels()
    cfg = load_config()
    if not cfg or not ARGS.no_socket:
        threading.Thread(target=serve, daemon=True).start()
    if cfg:
        sweep_leftovers()
        threading.Thread(target=Link(cfg).run, daemon=True).start()
        GLib.timeout_add_seconds(3600, lambda: (sweep_leftovers(), True)[1])
    GLib.timeout_add_seconds(15, idle_check)
    print("julos-agent %s laeuft (API v%d)%s%s" % (AGENT_VERSION, API_VERSION, ", Socket " + SOCK if (not cfg or not ARGS.no_socket) else "", ", Server " + cfg["server"] if cfg else ""), flush=True)
    Gtk.main()
