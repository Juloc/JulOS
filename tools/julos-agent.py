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

Betrieb (auf dem Rechner, ohne Anzeige):
      julos-agent.py check              Sind Python, PyGObject/GTK/Wnck und die grafische Sitzung da?
      julos-agent.py status             Laeuft er, ist er mit dem Server verbunden, fehlt etwas?
      julos-agent.py stop               beendet den laufenden Agenten
      julos-agent.py uninstall [--yes]  entfernt Dienst/Autostart, Programmdateien, Einrichtung und Log (mit --yes ohne Rueckfrage)
Die Logdatei liegt in ~/.local/state/julos/agent.log (dreht sich selbst, drei alte Staende). Updates holt der Agent auf Anweisung
des Servers selbst (nur von der eigenen Server-Adresse, mit Pruefsumme, siehe docs/AGENT_API.md).

Nur die Python-Standardbibliothek (plus PyGObject fuer die Sitzung): die Installation bleibt eine Datei.
"""
import sys

MIN_PYTHON = (3, 8)
if sys.version_info < MIN_PYTHON:
    sys.exit("julos-agent braucht Python %d.%d oder neuer (gefunden: %s). Auf Debian/Ubuntu: neueres python3 installieren." % (MIN_PYTHON + (sys.version.split()[0],)))

import argparse, base64, hashlib, hmac, http.server, json, logging, logging.handlers, os, queue, random, re, shutil, signal, socket, socketserver, ssl, stat, struct, subprocess, tempfile, threading, time
import urllib.error, urllib.parse, urllib.request

AGENT_VERSION = "0.4.0"      # der Server lehnt aeltere ab (AgentLinks.MinAgentVersion); ab 0.4.0 aktualisiert er sich auf Anweisung selbst
API_VERSION = 1
PLATFORM = "linux-x11"
UNIT_NAME = os.environ.get("JULOS_AGENT_UNIT") or "julos-agent.service"


def host_info():
    """Rechnername und Betriebssystem fuer die Anzeige in der App (der Server verwirft Werte mit Sonderzeichen oder ueber 64 Zeichen)."""
    name = socket.gethostname()
    system = ""
    try:
        with open("/etc/os-release", encoding="utf-8") as f:
            for line in f:
                if line.startswith("PRETTY_NAME="):
                    system = line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return {"hostname": name, "os": system}


CONFIG = os.environ.get("JULOS_AGENT_CONFIG") or os.path.expanduser("~/.config/julos/agent.json")
UPLOADS = os.path.expanduser("~/julos-uploads")        # den Ordner beobachtet der Zwischenablage-Helfer (julos-clip.py)
SCRIPT = os.path.realpath(__file__)
# Log, Prozessnummer und Verbindungsstand liegen im Benutzerverzeichnis (nicht in /tmp: dort liest sie jeder und ein Neustart loescht sie)
STATE_DIR = os.environ.get("JULOS_AGENT_STATE") or os.path.join(os.environ.get("XDG_STATE_HOME") or os.path.expanduser("~/.local/state"), "julos")
LOG_FILE = os.path.join(STATE_DIR, "agent.log")
PID_FILE = os.path.join(STATE_DIR, "agent.pid")
STATUS_FILE = os.path.join(STATE_DIR, "agent.status")
PANEL_STATE = os.environ.get("JULOS_AGENT_PANEL_STATE") or os.path.expanduser("~/.cache/julos-agent-panels.json")
STATE = PANEL_STATE
LOG_BYTES = 512 * 1024       # je Datei; dazu drei alte Staende (zusammen hoechstens rund 2 MB)
LOG_KEEP = 3

log = logging.getLogger("julos-agent")


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
    body = json.dumps({"code": code, "version": AGENT_VERSION, "platform": PLATFORM, **host_info()}).encode()
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
    save_config({"server": base, "token": token, "host": ans.get("host"), "allow_http": bool(allow_http)})
    print("Eingerichtet. Der Agent meldet sich ab jetzt selbst bei", base, flush=True)


COMMANDS = ("run", "status", "stop", "uninstall", "check")


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="JulOS Agent (siehe docs/AGENT_API.md)")
    p.add_argument("command", nargs="?", choices=COMMANDS, default="run",
                   help="run (Standard): Agent starten | status: Zustand zeigen | stop: laufenden Agenten beenden | uninstall: alles entfernen | check: Voraussetzungen pruefen")
    p.add_argument("--server", help="Adresse des JulOS-Servers (nur zusammen mit --code)")
    p.add_argument("--code", help="Einrichtungscode aus der App (Agent einrichten); danach endet das Programm")
    p.add_argument("--allow-http", action="store_true", help="nur zum Testen: auch ohne TLS verbinden")
    p.add_argument("--no-socket", action="store_true", help="den lokalen Unix-Socket nicht anbieten")
    p.add_argument("--forget", action="store_true", help="gespeicherte Einrichtung loeschen und beenden")
    p.add_argument("--yes", action="store_true", help="uninstall: ohne Rueckfrage")
    p.add_argument("--keep-config", action="store_true", help="uninstall: die Einrichtung (Token) behalten")
    return p.parse_args(argv)


# ---------------------------------------------------------------- Voraussetzungen
def missing_requirements():
    """Prueft, was der Agent zum Laufen braucht. Gibt (Probleme, Hinweise) zurueck, je als Liste von Texten; ohne Probleme laeuft er los."""
    problems, notes = [], []
    if sys.version_info < MIN_PYTHON:
        problems.append("Python %d.%d oder neuer fehlt (gefunden: %s)." % (MIN_PYTHON + (sys.version.split()[0],)))
    try:
        import gi
    except ImportError:
        problems.append("PyGObject (Python-Modul gi) fehlt. Installieren: sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0")
    else:
        for ns, ver, pkg in (("Gtk", "3.0", "gir1.2-gtk-3.0"), ("Wnck", "3.0", "gir1.2-wnck-3.0"), ("GdkPixbuf", "2.0", "gir1.2-gdkpixbuf-2.0")):
            try:
                gi.require_version(ns, ver)
            except ValueError:
                problems.append("%s %s fehlt. Installieren: sudo apt install %s" % (ns, ver, pkg))
    if not os.environ.get("DISPLAY"):
        if os.environ.get("XDG_SESSION_TYPE") == "wayland" or os.environ.get("WAYLAND_DISPLAY"):
            problems.append("Wayland ohne XWayland (DISPLAY fehlt): der Agent braucht eine X11-Sitzung.")
        else:
            problems.append("Keine grafische Sitzung (DISPLAY ist nicht gesetzt): der Agent muss in der Sitzung des Benutzers starten (Autostart, nicht ueber SSH).")
    if not shutil.which("xfconf-query"):
        notes.append("xfconf-query fehlt: der Programm-Modus (Taskleiste ausblenden) geht nur unter XFCE (sudo apt install xfconf).")
    return problems, notes


# ---------------------------------------------------------------- Prozess, Log, Verbindungsstand
def ensure_state_dir():
    os.makedirs(STATE_DIR, mode=0o700, exist_ok=True)
    try:
        os.chmod(STATE_DIR, 0o700)
    except OSError:
        pass


class _PrivateRotatingHandler(logging.handlers.RotatingFileHandler):
    def _open(self):
        fd = os.open(self.baseFilename, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)      # Log nur fuer den Besitzer lesbar
        return os.fdopen(fd, "a", encoding="utf-8")


def setup_logging():
    """Log in eine Datei im Benutzerverzeichnis (mit Rotation); an einem Terminal zusaetzlich auf den Bildschirm. Auch unbehandelte Fehler landen dort."""
    ensure_state_dir()
    log.setLevel(logging.INFO)
    fmt = logging.Formatter("%(asctime)s %(levelname)s %(message)s", "%Y-%m-%d %H:%M:%S")
    for handler in (_PrivateRotatingHandler(LOG_FILE, maxBytes=LOG_BYTES, backupCount=LOG_KEEP, encoding="utf-8"),
                    logging.StreamHandler(sys.stderr) if sys.stderr and sys.stderr.isatty() else None):
        if handler:
            handler.setFormatter(fmt)
            log.addHandler(handler)
    sys.excepthook = lambda t, e, tb: log.error("Unbehandelter Fehler", exc_info=(t, e, tb))
    threading.excepthook = lambda a: log.error("Unbehandelter Fehler im Thread %s", getattr(a.thread, "name", "?"), exc_info=(a.exc_type, a.exc_value, a.exc_traceback))


def _is_agent_process(pid):
    """Gehoert diese Prozessnummer wirklich zu einem Agenten (und nicht zu irgendetwas, das die Nummer spaeter bekam)?"""
    try:
        with open("/proc/%d/cmdline" % pid, "rb") as f:
            return b"julos-agent" in f.read()
    except OSError:
        return False


def running_pid():
    """Prozessnummer des laufenden Agenten dieses Benutzers (aus der Pid-Datei), sonst None."""
    try:
        with open(PID_FILE) as f:
            pid = int(f.read().strip())
        os.kill(pid, 0)
    except (OSError, ValueError):
        return None
    return pid if _is_agent_process(pid) else None


def write_private(path, text):
    ensure_state_dir()
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(text)
    os.replace(tmp, path)


def write_status(link, detail=""):
    """Verbindungsstand fuer `julos-agent.py status` (reine Anzeige)."""
    try:
        write_private(STATUS_FILE, json.dumps({"pid": os.getpid(), "version": AGENT_VERSION, "link": link, "detail": detail[:200], "since": int(time.time())}))
    except OSError:
        pass


def read_status():
    try:
        with open(STATUS_FILE) as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


# ---------------------------------------------------------------- Betrieb: status, stop, uninstall (ohne Anzeige)
def autostart_dir():
    return os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), "autostart")


def unit_file():
    return os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), "systemd", "user", UNIT_NAME)


def systemctl(*args):
    """systemctl --user; None, wenn es nicht geht (kein systemd, keine Sitzung)."""
    try:
        return subprocess.run(["systemctl", "--user", *args], capture_output=True, text=True, timeout=20)
    except (OSError, subprocess.SubprocessError):
        return None


def unit_active():
    if not os.path.exists(unit_file()):
        return False
    r = systemctl("is-active", UNIT_NAME)
    return bool(r and r.stdout.strip() == "active")


def _clock(ts):
    return time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts))


def command_status():
    cfg = load_config()
    pid = running_pid()
    st = read_status()
    problems, notes = missing_requirements()
    print("julos-agent %s (%s, Python %s)" % (AGENT_VERSION, PLATFORM, sys.version.split()[0]))
    print("Laeuft:            %s" % ("ja (PID %d)" % pid if pid else "nein"))
    print("Einrichtung:       %s" % ("Server %s" % cfg["server"] if cfg else "keine (nur lokaler Socket, kein Server eingerichtet)"))
    if cfg and pid and st.get("pid") == pid:
        since = " seit " + _clock(st["since"]) if isinstance(st.get("since"), int) else ""
        print("Verbindung:        %s%s%s" % (st.get("link", "unbekannt"), since, " (%s)" % st["detail"] if st.get("detail") else ""))
    elif cfg:
        print("Verbindung:        keine (der Agent laeuft nicht)")
    if os.path.exists(unit_file()):
        print("Start:             systemd-Benutzerdienst %s (%s)" % (UNIT_NAME, "aktiv" if unit_active() else "nicht aktiv"))
    elif os.path.exists(os.path.join(autostart_dir(), "julos-agent.desktop")):
        print("Start:             Autostart-Eintrag beim Anmelden")
    else:
        print("Start:             von Hand (weder Dienst noch Autostart eingerichtet)")
    try:
        print("Logdatei:          %s (%d kB)" % (LOG_FILE, (os.path.getsize(LOG_FILE) + 1023) // 1024))
    except OSError:
        print("Logdatei:          %s (noch keine)" % LOG_FILE)
    print("Voraussetzungen:   %s" % ("alle erfuellt" if not problems else "FEHLT"))
    for t in problems:
        print("  - " + t)
    for t in notes:
        print("  Hinweis: " + t)
    return 0 if pid else 3         # wie systemctl status: 3 = laeuft nicht


def command_check():
    problems, notes = missing_requirements()
    for t in problems:
        print("FEHLT: " + t)
    for t in notes:
        print("Hinweis: " + t)
    if not problems:
        print("Alle Voraussetzungen erfuellt.")
    return 3 if problems else 0


def command_stop(wait=8):
    unit = unit_active()
    if unit:
        r = systemctl("stop", UNIT_NAME)
        print("Dienst %s gestoppt." % UNIT_NAME if r and r.returncode == 0 else "Dienst %s liess sich nicht stoppen." % UNIT_NAME)
        return 0 if r and r.returncode == 0 else 1
    pid = running_pid()
    if not pid:
        print("Der Agent laeuft nicht.")
        return 0
    os.kill(pid, signal.SIGTERM)
    until = time.time() + wait
    while time.time() < until and running_pid():
        time.sleep(0.1)
    if running_pid():
        print("Der Agent (PID %d) reagiert nicht auf das Beenden: kill -9 %d" % (pid, pid))
        return 1
    print("Agent beendet (PID %d)." % pid)
    if os.path.exists(os.path.join(autostart_dir(), "julos-agent.desktop")):
        print("Er startet beim naechsten Anmelden wieder; dauerhaft entfernen: julos-agent.py uninstall")
    return 0


def _in_checkout(path):
    """Liegt die Datei in einem Git-Checkout (Entwicklung)? Dann wird nichts geloescht."""
    d = os.path.dirname(os.path.realpath(path))
    for _ in range(3):
        if os.path.exists(os.path.join(d, ".git")):
            return True
        d = os.path.dirname(d)
    return False


def _remove(path):
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass


def uninstall_plan(keep_config):
    """Was `uninstall` entfernen wuerde: Liste aus (Beschreibung, Funktion). Nichts wird hier veraendert."""
    plan = []
    here = os.path.dirname(SCRIPT)
    if running_pid() or unit_active():
        plan.append(("laufenden Agenten beenden", command_stop))
    if os.path.exists(unit_file()):
        def drop_unit():
            systemctl("disable", "--now", UNIT_NAME)
            _remove(unit_file())
            systemctl("daemon-reload")
        plan.append(("systemd-Benutzerdienst " + UNIT_NAME, drop_unit))
    for name in ("julos-agent.desktop", "julos-clip.desktop"):
        path = os.path.join(autostart_dir(), name)
        if os.path.exists(path):
            plan.append(("Autostart-Eintrag " + path, lambda p=path: _remove(p)))
    clip = os.path.join(here, "julos-clip.py")
    if os.path.exists(clip):
        plan.append(("laufenden Zwischenablage-Helfer beenden", lambda: stop_helper(clip)))
    if os.path.exists(PANEL_STATE):
        plan.append(("ausgeblendete Taskleiste wieder einblenden", lambda: restore_panels()))
    if _in_checkout(SCRIPT):
        plan.append(("(Programmdateien bleiben: sie liegen in einem Git-Checkout)", lambda: None))
    else:
        files = [os.path.join(here, n) for n in ("julos-agent.py", "julos-clip.py", "install-linux-agent.sh") if os.path.exists(os.path.join(here, n))]
        for f in files:
            plan.append(("Datei " + f, lambda f=f: _remove(f)))
        if files and os.path.realpath(here) != os.path.realpath(os.path.expanduser("~")):
            plan.append(("Ordner " + here + " (wenn leer)", lambda: _rmdir(here)))
    if os.path.exists(CONFIG) and not keep_config:
        plan.append(("Einrichtung mit Token " + CONFIG, lambda: (_remove(CONFIG), _rmdir(os.path.dirname(CONFIG)))))
    state = [os.path.join(STATE_DIR, n) for n in os.listdir(STATE_DIR) if n.startswith(("agent.", "clip."))] if os.path.isdir(STATE_DIR) else []
    if state:
        plan.append(("Log und Betriebsdaten in " + STATE_DIR, lambda: ([_remove(f) for f in state], _rmdir(STATE_DIR))))
    return plan


def _rmdir(path):
    try:
        os.rmdir(path)
    except OSError:
        pass


def stop_helper(clip_path):
    """Beendet den Zwischenablage-Helfer, der genau diese Datei ausfuehrt (andere Kopien auf dem Rechner bleiben unberuehrt)."""
    for name in os.listdir("/proc"):
        if not name.isdigit():
            continue
        try:
            with open("/proc/%s/cmdline" % name, "rb") as f:
                args = f.read().split(b"\0")
            if len(args) >= 2 and os.path.realpath(args[-2].decode()) == os.path.realpath(clip_path):
                os.kill(int(name), signal.SIGTERM)
        except (OSError, ValueError, UnicodeDecodeError):
            pass


def command_uninstall(yes, keep_config):
    plan = uninstall_plan(keep_config)
    if not plan:
        print("Nichts zu entfernen.")
        return 0
    print("Das wird entfernt:")
    for what, _ in plan:
        print("  - " + what)
    print("Der Ordner ~/julos-uploads (Fotos und Dateien fuer die Zwischenablage) bleibt. In der App den Agenten zusaetzlich entfernen (\"Agent entfernen\"), damit das Token auch dort ungueltig wird.")
    if not yes:
        if not sys.stdin.isatty():
            print("Keine Rueckfrage moeglich: mit --yes bestaetigen.", file=sys.stderr)
            return 2
        if input("Wirklich entfernen? [j/N] ").strip().lower() not in ("j", "ja", "y", "yes"):
            print("Abgebrochen.")
            return 1
    for what, action in plan:
        try:
            action()
        except Exception as e:  # noqa: BLE001
            print("Fehler bei \"%s\": %s" % (what, e), file=sys.stderr)
    print("Entfernt.")
    return 0


# ---------------------------------------------------------------- Aktualisierung (auf Anweisung des Servers)
UPDATE_PATH = "/agent-files/julos-agent.py"      # die einzige Adresse, von der sich der Agent aktualisiert (am Server, mit dem er verbunden ist)
MAX_AGENT_FILE = 1024 * 1024


class UpdateError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def version_tuple(text):
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)", str(text or ""))
    return tuple(int(x) for x in m.groups()) if m else None


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None          # ein Weiterleiten wuerde die Herkunft verlassen: abgelehnt


def update_url(server, path, allow_http=False):
    """Die Adresse der neuen Datei: immer die eigene Server-Adresse plus der feste Pfad. Alles andere (andere Pfade, volle Adressen, //host) wird abgelehnt."""
    if path != UPDATE_PATH:
        raise UpdateError(400, "Adresse nicht erlaubt: nur %s auf dem eigenen Server" % UPDATE_PATH)
    try:
        base = server_url(server, allow_http)
    except SystemExit as e:
        raise UpdateError(400, str(e))
    return base + path


def fetch_verified(url, sha256, dest_dir, opener=None):
    """Laedt die Datei in eine Temp-Datei neben dem Ziel (hoechstens MAX_AGENT_FILE) und prueft die SHA-256-Summe. Gibt (Pfad, Inhalt) zurueck; bei Fehlern bleibt nichts liegen."""
    opener = opener or urllib.request.build_opener(_NoRedirect)
    fd, tmp = tempfile.mkstemp(prefix=".julos-agent-", suffix=".tmp", dir=dest_dir)
    try:
        h, data = hashlib.sha256(), bytearray()
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "julos-agent/" + AGENT_VERSION})
            with opener.open(req, timeout=30) as r:
                if r.status != 200:
                    raise UpdateError(502, "Server antwortet mit HTTP %d" % r.status)
                while True:
                    chunk = r.read(65536)
                    if not chunk:
                        break
                    data += chunk
                    if len(data) > MAX_AGENT_FILE:
                        raise UpdateError(413, "Datei zu gross")
                    h.update(chunk)
        except urllib.error.HTTPError as e:
            raise UpdateError(502, "Server antwortet mit HTTP %d" % e.code)
        except (urllib.error.URLError, OSError, ssl.SSLError) as e:
            raise UpdateError(502, "Download fehlgeschlagen: %s" % getattr(e, "reason", e))
        if not hmac.compare_digest(h.hexdigest(), sha256):
            raise UpdateError(422, "Pruefsumme stimmt nicht: Datei verworfen")
        with os.fdopen(fd, "wb") as f:
            fd = None
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        return tmp, bytes(data)
    except BaseException:
        if fd is not None:
            os.close(fd)
        _remove(tmp)
        raise


def apply_update(server, message, script=None, allow_http=False, opener=None, current=None):
    """
    Aktualisiert die Agent-Datei: laden, Pruefsumme und Inhalt pruefen, dann atomar ersetzen (os.replace). Gibt (Version, ersetzt) zurueck;
    ersetzt ist False, wenn schon diese oder eine neuere Version laeuft. Wirft UpdateError und laesst die alte Datei unberuehrt.
    `message` ist die Anweisung des Servers: {"path": "/agent-files/julos-agent.py", "sha256": "<hex>", "version": "x.y.z"}.
    """
    script = script or SCRIPT
    current = current or AGENT_VERSION
    sha256, version = str(message.get("sha256", "")).lower(), message.get("version")
    if not re.fullmatch(r"[0-9a-f]{64}", sha256):
        raise UpdateError(400, "Pruefsumme ungueltig")
    new_v = version_tuple(version)
    if new_v is None:
        raise UpdateError(400, "Version ungueltig")
    url = update_url(server, message.get("path"), allow_http)
    if new_v <= version_tuple(current):
        return current, False
    tmp, data = fetch_verified(url, sha256, os.path.dirname(script) or ".", opener)
    try:
        try:
            compile(data, "julos-agent.py", "exec")          # ein kaputtes Skript wuerde den Agenten nicht mehr starten lassen
        except (SyntaxError, ValueError) as e:
            raise UpdateError(422, "Neue Datei ist kein gueltiges Python: %s" % e)
        m = re.search(rb'^AGENT_VERSION\s*=\s*"([0-9.]+)"', data, re.M)
        if not m or m.group(1).decode() != version:
            raise UpdateError(422, "Version in der Datei stimmt nicht mit der Anweisung ueberein")
        try:
            os.chmod(tmp, stat.S_IMODE(os.stat(script).st_mode))
        except OSError:
            os.chmod(tmp, 0o755)
        os.replace(tmp, script)                               # atomar: der Agent ist nie halb ersetzt
    except BaseException:
        _remove(tmp)
        raise
    return version, True


if __name__ == "__main__":
    ARGS = parse_args()
else:
    ARGS = argparse.Namespace(command="run", server=None, code=None, allow_http=False, no_socket=False, forget=False, yes=False, keep_config=False)

SOCK_DIR = os.environ.get("JULOS_AGENT_DIR") or os.path.join(UPLOADS, ".agent")   # im Upload-Ordner, den ein Server auf demselben Rechner einbindet
SOCK = os.path.join(SOCK_DIR, "agent.sock")
IDLE_RESTORE_S = 90          # Programm-Modus faellt zurueck, wenn so lange niemand mehr anfragt

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
        self.updating = threading.Lock()
        self.last_error = ""

    def log(self, *a):
        log.info("Link: %s", " ".join(str(x) for x in a))

    def run(self):
        delay, nap = 2, 0
        while True:
            started = time.time()
            try:
                self.session()
            except WsRejected as e:
                if e.status == 401:
                    nap = 300
                    self.last_error = "Token abgelehnt (Agent in der App entfernt?)"
                    self.log("Token abgelehnt: der Agent wurde in der App entfernt? Neu einrichten mit --server und --code.")
                else:
                    self.last_error = str(e)
                    self.log(str(e))
            except WsBye as e:
                nap = 600
                self.last_error = "Server weist diesen Agenten ab (zu alt)"
                self.log(str(e))
            except (WsError, OSError, ssl.SSLError, ValueError) as e:
                self.last_error = str(e)
                self.log("getrennt:", e)
            self.ws = None
            self.abort_put()
            write_status("getrennt", self.last_error)
            if nap:
                time.sleep(nap); nap = 0
                continue
            delay = 2 if time.time() - started > 30 else min(delay * 2, 60)       # nach langer Verbindung sofort wieder, sonst langsamer
            time.sleep(delay * random.uniform(0.7, 1.3))

    def session(self):
        ws = WsClient.connect(self.cfg["server"], {"Authorization": "Bearer " + self.cfg["token"]}, bool(self.cfg.get("allow_http")))
        self.ws = ws
        try:
            ws.send_text(json.dumps({"t": "hello", "agent": AGENT_VERSION, "platform": PLATFORM, "api": API_VERSION, **host_info()}))
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
            write_status("verbunden", "Server " + str(m.get("server", "?")))
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
        elif t == "update" and isinstance(id, int):
            self.background(id, lambda: self.do_update(id, m))
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

    # ---- Aktualisierung auf Anweisung des Servers
    def do_update(self, id, m):
        if not self.updating.acquire(blocking=False):
            return self.reply(id, 409, {"error": "Eine Aktualisierung laeuft schon"})
        try:
            try:
                version, replaced = apply_update(self.cfg["server"], m, allow_http=bool(self.cfg.get("allow_http")))
            except UpdateError as e:
                self.log("Aktualisierung abgelehnt (%d): %s" % (e.status, e))
                return self.reply(id, e.status, {"error": str(e)})
            except OSError as e:
                self.log("Aktualisierung fehlgeschlagen: %s" % e)
                return self.reply(id, 500, {"error": "Datei nicht ersetzbar: %s" % (e.strerror or e)})
            if not replaced:
                return self.reply(id, 200, {"ok": True, "version": version, "restarting": False})
            self.log("Aktualisiert auf %s, starte neu" % version)
            self.reply(id, 200, {"ok": True, "version": version, "restarting": True})
        finally:
            self.updating.release()
        time.sleep(0.5)                                      # die Antwort soll den Server noch erreichen
        GLib.idle_add(restart_agent)

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


def restart_agent():
    """Startet den Agenten mit der (neuen) Datei im selben Prozess neu (exec): gleiche Prozessnummer, Dienst und Autostart merken nichts."""
    try:
        if mode == "apps":
            restore_panels()
        log.info("Neustart")
        os.execv(sys.executable, [sys.executable, SCRIPT] + sys.argv[1:])
    except OSError as e:
        log.error("Neustart nicht moeglich (%s): der Agent laeuft in der alten Version weiter", e)
    return False


def cleanup(*_):
    try:
        if mode == "apps":
            restore_panels()
        _remove(PID_FILE)
        write_status("beendet")
    finally:
        sys.exit(0)


if __name__ == "__main__":
    if ARGS.forget:
        _remove(CONFIG)
        print("Einrichtung geloescht.", flush=True)
        sys.exit(0)
    if ARGS.code or ARGS.server:
        if not (ARGS.code and ARGS.server):
            sys.exit("--server und --code gehoeren zusammen")
        enroll(ARGS.server, ARGS.code, ARGS.allow_http)
        sys.exit(0)
    if ARGS.command == "status":
        sys.exit(command_status())
    if ARGS.command == "check":
        sys.exit(command_check())
    if ARGS.command == "stop":
        sys.exit(command_stop())
    if ARGS.command == "uninstall":
        sys.exit(command_uninstall(ARGS.yes, ARGS.keep_config))

    # Ab hier startet der Agent: erst pruefen, ob er laufen kann, und ein klares Wort sagen, wenn nicht
    _problems, _notes = missing_requirements()
    if _problems:
        sys.stderr.write("julos-agent kann nicht starten:\n" + "".join("  - %s\n" % t for t in _problems))
        sys.exit(3)
    setup_logging()
    for _t in _notes:
        log.warning(_t)
    _other = running_pid()
    if _other and _other != os.getpid():
        log.error("Ein Agent laeuft schon (PID %d): dieser Start wird abgebrochen.", _other)
        sys.stderr.write("Ein Agent laeuft schon (PID %d). Beenden mit: julos-agent.py stop\n" % _other)
        sys.exit(0)

    import gi
    gi.require_version("Gtk", "3.0"); gi.require_version("Wnck", "3.0"); gi.require_version("GdkPixbuf", "2.0")
    from gi.repository import Gtk, Gdk, Wnck, GLib, GdkPixbuf, Gio

    Wnck.set_client_type(Wnck.ClientType.PAGER)   # Aktivierungen gelten als "Pager" (Fokus-Schutz des Fenstermanagers greift nicht)
    screen = Wnck.Screen.get_default()
    if screen is None:
        log.error("Keine Verbindung zur grafischen Sitzung (DISPLAY=%s).", os.environ.get("DISPLAY"))
        sys.stderr.write("julos-agent: keine Verbindung zur grafischen Sitzung (DISPLAY=%s)\n" % os.environ.get("DISPLAY"))
        sys.exit(3)
    screen.force_update()
    theme = Gtk.IconTheme.get_default()

    signal.signal(signal.SIGTERM, cleanup)
    signal.signal(signal.SIGINT, cleanup)
    write_private(PID_FILE, str(os.getpid()))
    # Nach einem Absturz im Programm-Modus: Panels wiederherstellen
    if os.path.exists(STATE):
        restore_panels()
    cfg = load_config()
    if not cfg or not ARGS.no_socket:
        threading.Thread(target=serve, daemon=True).start()
    if cfg:
        sweep_leftovers()
        write_status("verbindet")
        threading.Thread(target=Link(cfg).run, daemon=True).start()
        GLib.timeout_add_seconds(3600, lambda: (sweep_leftovers(), True)[1])
    else:
        write_status("kein Server eingerichtet")
    GLib.timeout_add_seconds(15, idle_check)
    log.info("julos-agent %s laeuft (API v%d)%s%s", AGENT_VERSION, API_VERSION, ", Socket " + SOCK if (not cfg or not ARGS.no_socket) else "", ", Server " + cfg["server"] if cfg else "")
    Gtk.main()
