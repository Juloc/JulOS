#!/usr/bin/env python3
"""JulOS Agent fuer Linux (X11, XFCE getestet)

Laeuft in der grafischen Sitzung und stellt dem Server die "Agent-API v1" bereit
(siehe docs/AGENT_API.md): Fensterliste, Fenster aktivieren/schliessen, Programmliste und
Programmstart sowie den Programm-Modus (Taskleiste der Sitzung ausblenden).

Kommuniziert ausschliesslich ueber einen Unix-Socket (~/julos-uploads/.agent/agent.sock, Rechte 0600),
kein Netzwerkport. Ein Windows-Agent soll dieselben Endpunkte anbieten.
"""
import base64, http.server, json, os, queue, re, signal, socketserver, subprocess, sys, threading, time
import gi
gi.require_version("Gtk", "3.0"); gi.require_version("Wnck", "3.0"); gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gtk, Gdk, Wnck, GLib, GdkPixbuf, Gio

API_VERSION = 1
SOCK_DIR = os.environ.get("JULOS_AGENT_DIR") or os.path.expanduser("~/julos-uploads/.agent")   # im Upload-Ordner, den der Server schon eingebunden hat
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


# ---------------------------------------------------------------- HTTP ueber Unix-Socket
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
        global last_request
        last_request = time.time()
        p = self.path.split("?")[0]
        try:
            if p == "/v1/health":
                return self.reply(200, {"ok": True, "api": API_VERSION, "platform": "linux-x11", "mode": mode})
            if p == "/v1/windows":
                return self.reply(200, on_main(list_windows))
            if p == "/v1/apps":
                return self.reply(200, {"apps": on_main(list_apps)})
            return self.reply(404, {"error": "unbekannt"})
        except Exception as e:  # noqa: BLE001
            return self.reply(500, {"error": str(e)[:120]})

    def do_POST(self):
        global last_request
        last_request = time.time()
        p = self.path.split("?")[0]
        b = self.body()
        try:
            m = re.fullmatch(r"/v1/windows/(\d+)/(activate|close|minimize)", p)
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
                return self.reply(200 if ok else 404, {"ok": bool(ok)})
            if p == "/v1/apps/launch":
                ok = on_main(lambda: launch_app(str(b.get("id", ""))))
                return self.reply(200 if ok else 404, {"ok": bool(ok)})
            if p == "/v1/mode":
                new = b.get("mode")
                if new not in ("apps", "desktop"):
                    return self.reply(400, {"error": "mode: apps oder desktop"})
                return self.reply(200, {"ok": True, "mode": on_main(lambda: set_mode(new))})
            return self.reply(404, {"error": "unbekannt"})
        except Exception as e:  # noqa: BLE001
            return self.reply(500, {"error": str(e)[:120]})


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
    threading.Thread(target=serve, daemon=True).start()
    GLib.timeout_add_seconds(15, idle_check)
    print(f"julos-agent laeuft (API v{API_VERSION}), Socket {SOCK}", flush=True)
    Gtk.main()
