#!/usr/bin/env python3
"""JulOS Agent fuer Linux: EIN Agent pro Rechner mit zwei Faehigkeitsgruppen (Desktop und Host)

Eine Datei, eine Verbindung zum Server, zwei Betriebsarten:
- Desktop-Modus (Standard, wenn GTK/Wnck da sind): laeuft in der grafischen Sitzung des Benutzers (als Benutzerprozess, nie als root) und stellt die
  Desktop-Faehigkeiten bereit (siehe unten). Seit 0.7.0 beantwortet er ZUSAETZLICH alle /v1/host/*-Anfragen des Host-Modus (Systemwerte, Updates, Festplatten,
  Dienste, Docker, bestaetigte Aktionen) mit denselben Pruefungen: ein Rechner mit Desktop hat damit auch die Host-Funktionen, ohne zweiten Agenten.
- Host-Modus (--host, oder automatisch ohne GTK/Wnck): fuer Rechner ohne Desktop (Docker-VM, Proxmox-Host, Debian/Ubuntu-Server).
  Nur Python-Standardbibliothek, laeuft als eigener Benutzer ohne Anmeldung (tools/install-agent.sh). Er meldet seine Faehigkeiten
  (sysinfo, updates, services, docker) und beantwortet die LESENDEN Anfragen /v1/host/* (docs/HOST-AGENT.md). Dazu kommen ab 0.6.0 die Schreib-Aktionen
  (Updates, Neustart, Dienste, Container, Logs): nur mit lokaler Erlaubnis je Aktionsart (/etc/julos-agent/permissions) und nur auf bestaetigte Auftraege des Servers.
  Die Host-Faehigkeiten brauchen nur die Standardbibliothek; GTK wird nur fuer die Desktop-Faehigkeiten geladen.

Desktop-Faehigkeiten: die "Agent-API v1" (siehe docs/AGENT_API.md): Fensterliste, Fenster aktivieren/schliessen, Programmliste und
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

import argparse, base64, hashlib, hmac, http.client, http.server, json, logging, logging.handlers, os, queue, random, re, shutil, signal, socket, socketserver, ssl, stat, struct, subprocess, tempfile, threading, time
import urllib.error, urllib.parse, urllib.request

AGENT_VERSION = "0.9.1"      # der Server lehnt aeltere ab (AgentLinks.MinAgentVersion); ab 0.4.0 aktualisiert er sich auf Anweisung selbst; ab 0.5.0 gibt es den Host-Modus; ab 0.6.0 Schreib-Aktionen; ab 0.7.0 beantwortet auch der Desktop-Agent die Host-Anfragen (hello: caps desktop/host); ab 0.8.0 Dateien als root (Wort files-root, /v1/host/files/*); ab 0.9.1 erkennt der Agent eine tote Verbindung in rund 10 s (Ping alle 5 s, Frist 10 s, hello: hb) und verbindet mit zufaellig gestreutem Abstand neu
API_VERSION = 1
PLATFORM = "linux-x11"         # im Host-Modus "linux-host" (HOST_PLATFORM)
HOST_PLATFORM = "linux-host"
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


def enroll(server, code, allow_http, platform=None):
    """Tauscht den einmaligen Code aus der App gegen ein Token und merkt es (0600). platform: Plattform dieses Agenten (Host-Modus: linux-host)."""
    base = server_url(server, allow_http)
    body = json.dumps({"code": code, "version": AGENT_VERSION, "platform": platform or PLATFORM, **host_info()}).encode()
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
    p.add_argument("--host", action="store_true", help="Host-Modus (Rechner ohne Desktop: Systemwerte, Updates, Dienste, Docker; nur Python-Standardbibliothek)")
    p.add_argument("--desktop", action="store_true", help="Desktop-Modus erzwingen (ohne GTK/Anzeige mit Fehlermeldung beenden statt in den Host-Modus zu wechseln); er beantwortet auch die Host-Anfragen")
    p.add_argument("--allow-updates", action="store_true", help="Host-Faehigkeiten (Host-Modus und Desktop-Agent): Installieren von Updates erlauben (wirkt erst mit Stufe 3 des Host-Agenten; sonst /etc/julos-agent/permissions)")
    p.add_argument("--allow-reboot", action="store_true", help="Host-Faehigkeiten: Neustart erlauben (wirkt erst mit Stufe 3 des Host-Agenten)")
    p.add_argument("--docker-socket", help="Host-Faehigkeiten: Docker-Socket (Standard /var/run/docker.sock); nur lesend benutzt, ein Socket-Proxy ist besser als die Gruppe docker")
    p.add_argument("--forget", action="store_true", help="gespeicherte Einrichtung loeschen und beenden")
    p.add_argument("--yes", action="store_true", help="uninstall: ohne Rueckfrage")
    p.add_argument("--keep-config", action="store_true", help="uninstall: die Einrichtung (Token) behalten")
    return p.parse_args(argv)


# ---------------------------------------------------------------- Voraussetzungen
def gtk_stack_problems():
    """Was vom Desktop-Unterbau fehlt (PyGObject, GTK, Wnck, GdkPixbuf), als Liste von Texten."""
    problems = []
    try:
        import gi
    except ImportError:
        return ["PyGObject (Python-Modul gi) fehlt. Installieren: sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0"]
    for ns, ver, pkg in (("Gtk", "3.0", "gir1.2-gtk-3.0"), ("Wnck", "3.0", "gir1.2-wnck-3.0"), ("GdkPixbuf", "2.0", "gir1.2-gdkpixbuf-2.0")):
        try:
            gi.require_version(ns, ver)
        except ValueError:
            problems.append("%s %s fehlt. Installieren: sudo apt install %s" % (ns, ver, pkg))
    return problems


def choose_mode(args):
    """'host' oder 'desktop'. --host und --desktop entscheiden; sonst Host-Modus nur, wenn der Desktop-Unterbau (GTK/Wnck) fehlt.
    Ist er da, aber DISPLAY nicht, bleibt es beim Desktop-Modus mit klarer Fehlermeldung (ein Desktop-Rechner soll nicht still zum Host werden,
    nur weil der Dienst vor der Anmeldung startet)."""
    if getattr(args, "host", False):
        return "host"
    if getattr(args, "desktop", False):
        return "desktop"
    return "host" if gtk_stack_problems() else "desktop"


def host_requirements():
    """Voraussetzungen des Host-Modus: nur Python."""
    if sys.version_info < MIN_PYTHON:
        return ["Python %d.%d oder neuer fehlt (gefunden: %s)." % (MIN_PYTHON + (sys.version.split()[0],))], []
    notes = [] if sys.platform.startswith("linux") else ["Kein Linux: die Systemwerte des Host-Modus gibt es nur unter Linux."]
    return [], notes


def missing_requirements(mode="desktop"):
    """Prueft, was der Agent zum Laufen braucht. Gibt (Probleme, Hinweise) zurueck, je als Liste von Texten; ohne Probleme laeuft er los."""
    if mode == "host":
        return host_requirements()
    problems, notes = [], []
    if sys.version_info < MIN_PYTHON:
        problems.append("Python %d.%d oder neuer fehlt (gefunden: %s)." % (MIN_PYTHON + (sys.version.split()[0],)))
    problems += gtk_stack_problems()
    if not os.environ.get("DISPLAY"):
        if os.environ.get("XDG_SESSION_TYPE") == "wayland" or os.environ.get("WAYLAND_DISPLAY"):
            problems.append("Wayland ohne XWayland (DISPLAY fehlt): der Agent braucht eine X11-Sitzung.")
        else:
            problems.append("Keine grafische Sitzung (DISPLAY ist nicht gesetzt): der Agent muss in der Sitzung des Benutzers starten (Autostart, nicht ueber SSH). Auf einem Rechner ohne Desktop: julos-agent.py --host")
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


def setup_logging(console=False):
    """Log in eine Datei im Benutzerverzeichnis (mit Rotation); an einem Terminal zusaetzlich auf den Bildschirm (console: immer, so landet es bei einem
    Systemdienst im Journal). Auch unbehandelte Fehler landen dort."""
    ensure_state_dir()
    log.setLevel(logging.INFO)
    fmt = logging.Formatter("%(asctime)s %(levelname)s %(message)s", "%Y-%m-%d %H:%M:%S")
    for handler in (_PrivateRotatingHandler(LOG_FILE, maxBytes=LOG_BYTES, backupCount=LOG_KEEP, encoding="utf-8"),
                    logging.StreamHandler(sys.stderr) if sys.stderr and (console or sys.stderr.isatty()) else None):
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
        write_private(STATUS_FILE, json.dumps({"pid": os.getpid(), "version": AGENT_VERSION, "mode": RUN_MODE, "link": link, "detail": detail[:200], "since": int(time.time())}))
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


def system_unit_active():
    """Laeuft der Host-Agent als systemd-Systemdienst (tools/install-agent.sh --host)?"""
    try:
        r = subprocess.run(["systemctl", "is-active", UNIT_NAME], capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.SubprocessError):
        return False
    return r.stdout.strip() == "active"


def command_status():
    cfg = load_config()
    pid = running_pid()
    st = read_status()
    mode = choose_mode(ARGS)
    problems, notes = missing_requirements(mode)
    print("julos-agent %s (%s, Python %s)" % (AGENT_VERSION, HOST_PLATFORM if mode == "host" else PLATFORM, sys.version.split()[0]))
    print("Modus:             %s" % ("Host (ohne Desktop; Faehigkeiten: %s)" % (", ".join(host_caps()) or "keine") if mode == "host" else "Desktop (dazu die Host-Faehigkeiten: %s)" % (", ".join(host_caps()) or "keine")))
    print("Lokal erlaubt:     %s" % (", ".join(sorted(permissions())) or "nichts (nur Lesen)"))
    print("Laeuft:            %s" % ("ja (PID %d)" % pid if pid else "nein"))
    print("Einrichtung:       %s" % ("Server %s" % cfg["server"] if cfg else "keine (nur lokaler Socket, kein Server eingerichtet)"))
    if cfg and pid and st.get("pid") == pid:
        since = " seit " + _clock(st["since"]) if isinstance(st.get("since"), int) else ""
        print("Verbindung:        %s%s%s" % (st.get("link", "unbekannt"), since, " (%s)" % st["detail"] if st.get("detail") else ""))
    elif cfg:
        print("Verbindung:        keine (der Agent laeuft nicht)")
    if system_unit_active():
        print("Start:             systemd-Systemdienst %s (aktiv)" % UNIT_NAME)
    elif os.path.exists(unit_file()):
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
    mode = choose_mode(ARGS)
    problems, notes = missing_requirements(mode)
    print("Modus: %s" % ("Host (ohne Desktop)" if mode == "host" else "Desktop (mit den Host-Faehigkeiten)"))
    for t in problems:
        print("FEHLT: " + t)
    for t in notes:
        print("Hinweis: " + t)
    if not problems:
        print("Alle Voraussetzungen erfuellt.")
    return 3 if problems else 0


def command_stop(wait=8, hint=True):
    if system_unit_active():
        print("Der Agent laeuft als Systemdienst: sudo systemctl stop %s (entfernen: sudo sh install-agent.sh --uninstall)" % UNIT_NAME)
        return 1
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
    if hint and os.path.exists(os.path.join(autostart_dir(), "julos-agent.desktop")):
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
        plan.append(("laufenden Agenten beenden", lambda: command_stop(hint=False)))
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
        files = [os.path.join(here, n) for n in ("julos-agent.py", "julos-clip.py", "install-linux-agent.sh", "install-agent.sh") if os.path.exists(os.path.join(here, n))]
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


# ---------------------------------------------------------------- Host-Modus (Rechner ohne Desktop; nur Standardbibliothek, siehe docs/HOST-AGENT.md)
# Lesen: Systemwerte, Update-Liste, fehlgeschlagene Dienste, Docker-Container. Schreiben (Stufe 3, Abschnitt "Schreib-Aktionen" weiter unten): nur feste Pfade,
# nur mit lokaler Erlaubnis je Aktionsart, nur ein Auftrag zugleich.
HOST_CAPS_ALL = ("sysinfo", "updates", "services", "docker")
CONTROL_RIGHTS = ("updates", "reboot", "services", "docker", "logs", "disks", "files-root")
PERMISSIONS_FILE = os.environ.get("JULOS_AGENT_PERMISSIONS") or "/etc/julos-agent/permissions"
DOCKER_SOCKET = os.environ.get("JULOS_AGENT_DOCKER_SOCKET") or "/var/run/docker.sock"
CMD_TIMEOUT = 30
MAX_OUTPUT = 8 * 1024 * 1024
MAX_PACKAGES = 500           # die Antwort bleibt weit unter den 600 kB, die der Server annimmt
MAX_CONTAINERS = 100
MAX_ITEMS = 64               # Platten, Adressen, Dienste
DEVICE_NAME_RE = re.compile(r"^[a-z][a-z0-9]{1,15}\Z")      # Plattenname aus der Antwort des Helfers (/dev/<name>)
RUN_MODE = "desktop"         # was dieser Prozess ist: "desktop" oder "host" (fuer Statusdatei und hello)

def clean(text, limit=200):
    """Text aus dem System fuer die Anzeige: nur druckbare Zeichen (keine Steuerzeichen, kein Zeilenumbruch), hoechstens limit Zeichen (der Server prueft noch einmal)."""
    return "".join(c for c in str(text if text is not None else "") if c.isprintable()).strip()[:limit]


def run_cmd(argv, timeout=CMD_TIMEOUT):
    """Ein festes Programm ohne Shell: (Rueckgabewert, Ausgabe, Fehlerausgabe); None, wenn es fehlt, nicht startet oder zu lange braucht. Immer englische Meldungen (LC_ALL=C)."""
    env = dict(os.environ, LC_ALL="C", LANG="C", PATH=os.environ.get("PATH", "") + ":/usr/local/sbin:/usr/sbin:/sbin")
    try:
        p = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, stdin=subprocess.DEVNULL, timeout=timeout, env=env)
    except (OSError, subprocess.SubprocessError):
        return None
    return p.returncode, p.stdout[:MAX_OUTPUT].decode("utf-8", "replace"), p.stderr[:65536].decode("utf-8", "replace")


def read_text(path, limit=1024 * 1024):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read(limit)
    except OSError:
        return ""


# ---- Rechte (Stufe 3): am Server nach Stufe 'control' geprueft, hier noch einmal lokal
def parse_permissions(text):
    """Die Datei /etc/julos-agent/permissions: je Zeile ein Recht (updates, reboot, services, docker, logs, disks, files-root), '#' beginnt einen Kommentar. Unbekanntes wird ignoriert."""
    out = set()
    for line in text.splitlines():
        word = line.split("#", 1)[0].strip().lower()
        if word in CONTROL_RIGHTS:
            out.add(word)
    return out


def read_permissions(path=None, trusted_uids=(0,)):
    """Rechte aus der lokalen Datei. Sie zaehlt nur, wenn sie einem vertrauten Benutzer (root) gehoert und fuer Gruppe und andere nicht beschreibbar ist:
    der Agent selbst (eigener Benutzer) kann sich so keine Rechte geben."""
    path = path or PERMISSIONS_FILE
    try:
        st = os.stat(path)
    except OSError:
        return set()
    if st.st_uid not in trusted_uids or st.st_mode & 0o022 or not stat.S_ISREG(st.st_mode):
        log.warning("Rechte-Datei %s ignoriert: sie muss root gehoeren und darf nur root beschreibbar sein.", path)
        return set()
    return parse_permissions(read_text(path, 4096))


def permissions(args=None):
    """Was dieser Agent lokal erlaubt: Aufrufparameter (--allow-updates, --allow-reboot) plus Datei."""
    args = args if args is not None else ARGS
    granted = set(read_permissions())
    if getattr(args, "allow_updates", False):
        granted.add("updates")
    if getattr(args, "allow_reboot", False):
        granted.add("reboot")
    return granted


# ---- Systemwerte
def read_os_release(path="/etc/os-release"):
    out = {}
    for line in read_text(path, 16384).splitlines():
        key, sep, value = line.partition("=")
        if sep and key.strip() in ("PRETTY_NAME", "ID", "VERSION_ID", "NAME"):
            out[key.strip()] = value.strip().strip('"').strip("'")
    return out


def read_meminfo(proc="/proc"):
    """Arbeitsspeicher und Auslagerung in Bytes (MemAvailable gilt als frei, nicht MemFree)."""
    kb = {}
    for line in read_text(os.path.join(proc, "meminfo"), 65536).splitlines():
        m = re.match(r"^(\w+):\s+(\d+)\s*kB", line)
        if m:
            kb[m.group(1)] = int(m.group(2))
    total = kb.get("MemTotal", 0) * 1024
    avail = kb.get("MemAvailable", kb.get("MemFree", 0)) * 1024
    swap = kb.get("SwapTotal", 0) * 1024
    return {"totalBytes": total, "availableBytes": min(avail, total) if total else avail, "swapTotalBytes": swap, "swapFreeBytes": min(kb.get("SwapFree", 0) * 1024, swap)}


def read_uptime(proc="/proc"):
    try:
        return int(float(read_text(os.path.join(proc, "uptime"), 256).split()[0]))
    except (IndexError, ValueError):
        return None


def read_load(proc="/proc"):
    try:
        return [float(x) for x in read_text(os.path.join(proc, "loadavg"), 256).split()[:3]] or None
    except ValueError:
        return None


REAL_FS = {"ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "vfat", "exfat", "ntfs", "ntfs3", "reiserfs", "jfs", "ubifs", "overlay", "bcachefs"}
SKIP_MOUNT_PREFIXES = ("/snap/", "/var/lib/docker/", "/var/lib/containers/", "/var/lib/kubelet/", "/run/", "/sys/", "/proc/", "/dev/")


def unescape_mount(text):
    return re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), text)


def read_disks(proc="/proc", statvfs=os.statvfs, isdir=os.path.isdir):
    """Datentraeger mit Fuellstand (nur echte Dateisysteme, kein tmpfs/Netzlaufwerk: ein totes NFS wuerde die Abfrage haengen lassen).
    Doppelte Einbindungen desselben Geraets (Container, systemd-Schutz) zaehlen einmal, mit dem kuerzesten Pfad."""
    best = {}
    for line in read_text(os.path.join(proc, "mounts"), 262144).splitlines():
        parts = line.split()
        if len(parts) < 3:
            continue
        dev, mount, fs = parts[0], unescape_mount(parts[1]), parts[2]
        if fs not in REAL_FS or mount.startswith(SKIP_MOUNT_PREFIXES):
            continue
        if not isdir(mount):
            continue                                       # eine einzelne Datei als Mount (/etc/hosts im Container) ist keine Platte
        if fs == "overlay" and mount != "/":
            continue                                       # in einem Container ist "/" ein overlay; weitere sind Docker-Schichten
        key = dev if dev.startswith("/") else mount
        if key in best and len(best[key][0]) <= len(mount):
            continue
        best[key] = (mount, fs)
    out = []
    for mount, fs in best.values():
        try:
            st = statvfs(mount)
        except OSError:
            continue
        total = st.f_blocks * st.f_frsize
        if total <= 0:
            continue
        used = (st.f_blocks - st.f_bfree) * st.f_frsize
        avail = st.f_bavail * st.f_frsize
        denom = used + avail                               # wie df: Prozent vom benutzbaren Teil
        out.append({"mount": clean(mount, 120), "fs": fs, "totalBytes": total, "usedBytes": used, "freeBytes": avail, "percent": round(100.0 * used / denom, 1) if denom > 0 else 0.0})
    out.sort(key=lambda d: (d["mount"] != "/", d["mount"]))
    return out[:MAX_ITEMS]


_IP_LINE = re.compile(r"^\d+:\s+(\S+)\s+(inet6?)\s+([0-9a-fA-F:.]+)/(\d+)\s.*?\bscope\s+(\S+)")


def parse_ip_addr(text):
    """Ausgabe von `ip -o addr show`: Adressen ausser Loopback und Link-lokal (und ohne die virtuellen veth-Paare von Containern)."""
    out = []
    for line in text.splitlines():
        m = _IP_LINE.match(line)
        if not m:
            continue
        iface, fam, addr, prefix, scope = m.groups()
        if scope in ("host", "link") or iface.startswith("veth") or iface == "lo":
            continue
        out.append({"iface": clean(iface.split("@")[0], 20), "family": 6 if fam == "inet6" else 4, "address": addr, "prefix": int(prefix)})
    return out


def parse_fib_trie(text):
    """Ersatz ohne `ip` (Minimal-Container): die lokalen IPv4-Adressen aus /proc/net/fib_trie."""
    out, last = [], None
    for line in text.splitlines():
        m = re.match(r"^\s*\|--\s+(\d+\.\d+\.\d+\.\d+)\s*$", line)
        if m:
            last = m.group(1)
        elif last and "/32 host LOCAL" in line:
            if not last.startswith("127.") and not any(a["address"] == last for a in out):
                out.append({"iface": "", "family": 4, "address": last, "prefix": 32})
            last = None
    return out


def read_addresses(proc="/proc"):
    r = run_cmd(["ip", "-o", "addr", "show"], 5)
    found = parse_ip_addr(r[1]) if r and r[0] == 0 else parse_fib_trie(read_text(os.path.join(proc, "net", "fib_trie"), 262144))
    return found[:MAX_ITEMS]


CONTAINER_VIRTS = {"lxc", "lxc-libvirt", "openvz", "docker", "podman", "systemd-nspawn", "rkt", "wsl", "proot", "pouch"}


def detect_virt(proc="/proc", exists=os.path.exists):
    """Laeuft dieser Rechner virtualisiert? ('none', 'vm' oder 'container') plus der Name, den systemd-detect-virt nennt (kvm, qemu, lxc, docker ...)."""
    r = run_cmd(["systemd-detect-virt"], 5)
    name = r[1].strip().lower() if r and r[1].strip() else ""
    if not name:
        if exists("/.dockerenv"):
            name = "docker"
        elif re.search(r"^flags\s*:.*\bhypervisor\b", read_text(os.path.join(proc, "cpuinfo"), 262144), re.M):
            name = "vm"
        else:
            name = "none"
    kind = "none" if name == "none" else "container" if name in CONTAINER_VIRTS else "vm"
    return {"kind": kind, "name": clean(name, 24)}


def detect_proxmox(exists=os.path.exists):
    """Ist das ein Proxmox-Host (VE, Backup Server, Mail Gateway)? {'product': 've', 'version': '8.x'} oder None."""
    for product, marks in (("ve", ("/usr/bin/pveversion", "/etc/pve")), ("pbs", ("/usr/sbin/proxmox-backup-manager", "/etc/proxmox-backup")), ("pmg", ("/usr/bin/pmgversion", "/etc/pmg"))):
        if any(exists(m) for m in marks):
            version = ""
            if product == "ve":
                r = run_cmd(["pveversion"], 10)
                m = re.search(r"pve-manager/([0-9][0-9.]*)", r[1]) if r else None
                version = m.group(1) if m else ""
            return {"product": product, "version": version}
    return None


# ---- Netzwerk und Gesundheit der Platten (SMART, RAID, ZFS); alles lesend
def read_netdev(proc="/proc"):
    """Zaehler je Netzwerkschnittstelle aus /proc/net/dev (Bytes seit dem Start; die Rate rechnet der Betrachter aus zwei Ablesungen). Ohne Loopback und Container-Paare."""
    out = []
    for line in read_text(os.path.join(proc, "net", "dev"), 65536).splitlines()[2:]:
        name, sep, rest = line.partition(":")
        name, f = name.strip(), rest.split()
        if not sep or len(f) < 9 or name == "lo" or name.startswith(("veth", "docker", "br-", "virbr", "tap")):
            continue
        if int(f[0]) or int(f[8]):
            out.append({"iface": clean(name, 20), "rxBytes": int(f[0]), "txBytes": int(f[8])})
    return out[:MAX_ITEMS]


def parse_mdstat(text):
    """/proc/mdstat: je Verbund {name, level, state (ok, degraded, recovering, inactive), active, total, syncPercent?}. '[UU]' = beide Platten da, '_' = fehlt."""
    arrays, cur = [], None
    for line in text.splitlines():
        m = re.match(r"^(md\w+)\s*:\s*(\w+)(?:\s*\([^)]*\))?\s*(\w+)?", line)
        if m:
            cur = {"name": clean(m.group(1), 16), "level": clean(m.group(3) or "", 16), "state": "ok" if m.group(2) == "active" else "inactive", "active": 0, "total": 0}
            arrays.append(cur)
            continue
        if cur is None:
            continue
        m = re.search(r"\[(\d+)/(\d+)\]\s*\[([U_]+)\]", line)
        if m:
            cur["total"], cur["active"] = int(m.group(1)), int(m.group(2))
            if cur["active"] < cur["total"] and cur["state"] == "ok":
                cur["state"] = "degraded"
        m = re.search(r"(resync|recovery|reshape|check)\s*=\s*([\d.]+)%", line)
        if m:
            cur["syncPercent"] = float(m.group(2))
            if m.group(1) != "check" and cur["state"] != "inactive":      # check ist nur die regelmaessige Pruefung eines gesunden Verbunds
                cur["state"] = "recovering"
    return arrays[:MAX_ITEMS]


def parse_zpool_status(text, rc=0):
    """`zpool status -x` (Text und Rueckgabewert). Ergebnis {state, healthy, pools, message?}:
      healthy  'all pools are healthy'
      none     'no pools available' (es gibt keine Pools: kein Problem)
      problem  mindestens ein Pool wird gelistet (-x nennt nur Pools mit Problem): je Pool name, state (DEGRADED, FAULTED, UNAVAIL, auch ONLINE mit Fehlern), scan, errors, status
      unknown  alles andere (ZFS-Module nicht geladen, Version passt nicht, Fehlermeldung): KEIN Pool '?', nur eine kurze bereinigte Meldung."""
    text = str(text or "")
    low = text.lower()
    pools, cur = [], None
    for line in text.splitlines():
        m = re.match(r"^\s*pool:\s*(\S+)", line)
        if m:
            cur = {"name": clean(m.group(1), 40), "state": ""}
            pools.append(cur)
            continue
        m = re.match(r"^\s*(state|scan|errors|status):\s*(.*)$", line)
        if m and cur is not None:
            key, val = m.group(1), clean(m.group(2), 100)
            if key == "state":
                cur["state"] = clean(val.split()[0] if val else "", 16)
            elif val and key not in cur:
                cur[key] = val
    pools = [p for p in pools if p["state"]]
    if pools and rc == 0:
        return {"state": "problem", "healthy": False, "pools": pools[:MAX_ITEMS]}
    if rc == 0 and "all pools are healthy" in low:
        return {"state": "healthy", "healthy": True, "pools": []}
    if rc == 0 and "no pools available" in low:
        return {"state": "none", "healthy": True, "pools": []}
    first = next((l for l in (clean(x, 120) for x in text.splitlines()) if l), "")
    return {"state": "unknown", "healthy": False, "pools": [], "message": first or ("zpool rc %s" % rc)}


def parse_smart_health(text):
    """Ausgabe von `smartctl -H`: 'ok' (PASSED / OK), 'failing' (FAILED), sonst 'unknown'; 'no_access', wenn der Zugriff verweigert wurde."""
    low = text.lower()
    if "permission denied" in low or "operation not permitted" in low:
        return "no_access"
    if "failed" in low and ("overall-health" in low or "health status" in low):
        return "failing"
    if re.search(r"overall-health self-assessment test result:\s*passed", low) or re.search(r"smart health status:\s*ok", low):
        return "ok"
    return "unknown"


def collect_smart():
    """SMART-Kurzstatus je Platte. Mit dem Wort `disks` fragt der Agent den root-Helfer (Verb smart.health, nur lesend, die Plattenliste bildet der Helfer selbst);
    ohne das Wort versucht er es als Dienst-Benutzer (meist kein Zugriff, dann bleibt die ehrliche Meldung). {'available': False, 'reason': ...} sagt, warum nichts zu lesen war."""
    if "disks" in permissions():
        r = helper_call({"verb": "smart.health"}, None, 600)
        if r is None:
            return {"available": False, "reason": "helper_unavailable"}
        if not r.get("ok"):
            return {"available": False, "reason": "no_smartctl" if r.get("code") == "not_installed" else "read_failed"}
        devices = []
        for d in (r.get("devices") if isinstance(r.get("devices"), list) else [])[:16]:
            if isinstance(d, dict) and isinstance(d.get("name"), str) and DEVICE_NAME_RE.match(d["name"]):
                e = {"name": "/dev/" + d["name"], "health": d.get("health") if d.get("health") in ("ok", "failing") else "unknown"}
                if isinstance(d.get("temp"), int) and not isinstance(d.get("temp"), bool) and -50 <= d["temp"] <= 150:
                    e["temp"] = d["temp"]
                devices.append(e)
        return {"available": True, "devices": devices} if devices else {"available": False, "reason": "no_devices"}
    if not shutil.which("smartctl"):
        return {"available": False, "reason": "no_smartctl"}
    scan = run_cmd(["smartctl", "--scan"], 10)
    names = []
    for line in (scan[1] if scan else "").splitlines():
        m = re.match(r"^(/dev/\S+)", line)
        if m:
            names.append(m.group(1))
    if not names:
        return {"available": False, "reason": "no_devices"}
    devices, denied = [], 0
    for dev in names[:16]:
        r = run_cmd(["smartctl", "-H", dev], 8)
        health = parse_smart_health((r[1] + r[2]) if r else "")
        if health == "no_access":
            denied += 1
        devices.append({"name": clean(dev, 40), "health": "unknown" if health == "no_access" else health})
    if denied == len(devices):
        return {"available": False, "reason": "no_access"}
    return {"available": True, "devices": devices}


def collect_zfs():
    if not shutil.which("zpool"):
        return None
    r = run_cmd(["zpool", "status", "-x"], 10)
    if r is None:
        return {"state": "unknown", "healthy": False, "pools": [], "message": "zpool antwortet nicht"}
    return parse_zpool_status((r[1] + "\n" + r[2]) if r[0] != 0 else r[1], r[0])


def compute_health(proc="/proc"):
    return {"smart": collect_smart(), "md": parse_mdstat(read_text(os.path.join(proc, "mdstat"), 65536)), "zfs": collect_zfs(), "checkedAt": int(time.time())}


HEALTH = {"t": 0, "data": None, "busy": False}
HEALTH_TTL = 600             # SMART und ZFS aendern sich langsam und smartctl braucht Sekunden: hoechstens alle 10 Minuten, im Hintergrund


def health_snapshot(compute=None, now=None):
    """Der letzte Stand der Platten-Gesundheit; ist er aelter als HEALTH_TTL, wird er im Hintergrund neu gelesen (die Antwort wartet nicht darauf).
    Beim ersten Mal gibt es noch nichts: {'pending': True}."""
    compute = compute or compute_health
    now = time.time() if now is None else now
    if now - HEALTH["t"] > HEALTH_TTL and not HEALTH["busy"]:
        HEALTH["busy"] = True

        def work():
            try:
                HEALTH["data"], HEALTH["t"] = compute(), time.time()
            except Exception as e:  # noqa: BLE001
                log.warning("Gesundheitsabfrage fehlgeschlagen: %s", e)
                HEALTH["t"] = time.time()
            finally:
                HEALTH["busy"] = False
        threading.Thread(target=work, daemon=True).start()
    return HEALTH["data"] or {"pending": True}


def kernel_key(version):
    """Ein Kernel-Versionstext als vergleichbarer Schluessel: '6.12.57+deb13-amd64' -> ((6, 12, 57, 13), 'amd64'). Die Zahlen werden verglichen, die Art (amd64, pve, generic) bestimmt, was vergleichbar ist."""
    nums = tuple(int(n) for n in re.findall(r"\d+", version.rsplit("-", 1)[0] if "-" in version else version))
    flavour = version.rsplit("-", 1)[1] if "-" in version else ""
    return nums, flavour if not flavour[:1].isdigit() else ""


def kernel_pending(running, installed):
    """Ist ein neuerer Kernel der gleichen Art installiert als der laufende? installed: Versionstexte aus /boot/vmlinuz-*. Gibt die neueste installierte Version zurueck, wenn sie neuer ist, sonst None.
    Das braucht auch Proxmox (proxmox-kernel): dort setzt apt keine Datei /var/run/reboot-required."""
    run_nums, run_flavour = kernel_key(running)
    best = None
    for v in installed:
        nums, flavour = kernel_key(v)
        if flavour == run_flavour and nums > run_nums and (best is None or nums > kernel_key(best)[0]):
            best = v
    return best


def read_installed_kernels(boot="/boot"):
    """Die Versionen der installierten Kernel: Namen 'vmlinuz-<version>' im Ordner /boot (nur der Ordner wird gelesen, nie eine Datei)."""
    try:
        return [clean(n[len("vmlinuz-"):], 64) for n in os.listdir(boot) if n.startswith("vmlinuz-") and re.match(r"^vmlinuz-\d[\w.+~\-]*\Z", n)][:64]
    except OSError:
        return []


def is_proxmox(exists=os.path.exists, run=None):
    """Ist das ein Proxmox-Rechner (Paket pve-manager installiert oder /etc/pve, auch Backup Server und Mail Gateway)? Dort darf nur `apt-get dist-upgrade` aktualisieren."""
    if any(exists(m) for m in ("/etc/pve", "/etc/proxmox-backup", "/etc/pmg")):
        return True
    r = (run or run_cmd)(["dpkg-query", "-W", "-f=${Status}", "pve-manager"], 10)
    return bool(r and r[0] == 0 and "install ok installed" in r[1])


def collect_sysinfo(proc="/proc", etc="/etc", var_run="/var/run", statvfs=os.statvfs, exists=os.path.exists):
    osr = read_os_release(os.path.join(etc, "os-release"))
    un = os.uname()
    flag = exists(os.path.join(var_run, "reboot-required"))
    pkgs = [clean(l, 80) for l in read_text(os.path.join(var_run, "reboot-required.pkgs"), 8192).split() if l][:20] if flag else []
    new_kernel = kernel_pending(un.release, read_installed_kernels())      # neuer Kernel installiert, laeuft noch nicht: Neustart noetig, auch ohne die Datei
    reboot = flag or bool(new_kernel)
    return {
        "hostname": clean(socket.gethostname(), 64),
        "os": clean(osr.get("PRETTY_NAME") or osr.get("NAME"), 64), "osId": clean(osr.get("ID"), 24), "osVersion": clean(osr.get("VERSION_ID"), 24),
        "kernel": clean(un.release, 64), "arch": clean(un.machine, 16),
        "uptimeSeconds": read_uptime(proc), "load": read_load(proc), "cpuCount": os.cpu_count() or 0,
        "memory": read_meminfo(proc), "disks": read_disks(proc, statvfs), "addresses": read_addresses(proc), "network": read_netdev(proc),
        "health": health_snapshot(),
        "rebootRequired": reboot, "rebootPackages": pkgs, "newKernel": new_kernel,
        "virt": detect_virt(proc, exists), "proxmox": detect_proxmox(exists),
        "collectedAt": int(time.time()),
    }


# ---- Updates (apt; dnf, apk und pacman nur das Einfachste)
_APT_INST = re.compile(r"^Inst (\S+) (?:\[(\S+)\] )?\((\S+) (.*?)(?: \[[^\]]*\])?\)")


def is_security_origin(origin):
    """Debian ('Debian-Security:12/stable-security'), Ubuntu ('Ubuntu:22.04/jammy-security', auch ESM): 'security' in der Herkunft."""
    return "security" in origin.lower()


def parse_apt_simulation(text):
    """Ausgabe von `apt-get -s dist-upgrade`: {'packages': [{name, from, to, security, new}], 'keptBack': [...], 'removals': n}.
    'Inst name [alt] (neu Herkunft [arch])'; ohne [alt] ist es ein neues Paket (Abhaengigkeit, zum Beispiel ein neuer Kernel)."""
    packages, kept, removals, in_kept = [], [], 0, False
    for line in text.splitlines():
        if in_kept:
            if line.startswith(" "):
                kept += line.split()
                continue
            in_kept = False
        if line.startswith("The following packages have been kept back"):
            in_kept = True
            continue
        m = _APT_INST.match(line)
        if m:
            name, old, new, origin = m.groups()
            packages.append({"name": clean(name, 80), "from": clean(old, 64) or None, "to": clean(new, 64), "security": is_security_origin(origin), "new": old is None})
        elif line.startswith("Remv "):
            removals += 1
    return {"packages": packages, "keptBack": [clean(k, 80) for k in kept if k], "removals": removals}


def apt_lists_checked(lists="/var/lib/apt/lists", stamp="/var/lib/apt/periodic/update-success-stamp"):
    """Wann wurden die Paketlisten zuletzt aktualisiert (Epoche)? Das Stempelfile der automatischen Aktualisierung, sonst die neueste Listendatei.
    Die Listen selbst zu aktualisieren (apt-get update) ist Sache von root, nicht dieses Agenten."""
    times = []
    try:
        times.append(os.stat(stamp).st_mtime)
    except OSError:
        pass
    try:
        for name in os.listdir(lists):
            if name in ("lock", "partial", "auxfiles"):
                continue
            try:
                times.append(os.stat(os.path.join(lists, name)).st_mtime)
            except OSError:
                pass
    except OSError:
        pass
    return int(max(times)) if times else None


def broken_packages():
    """Pakete, die halb eingerichtet sind (`dpkg --audit`, lesend, ohne Root): leere Liste, wenn alles in Ordnung ist oder dpkg nicht antwortet."""
    r = run_cmd(["dpkg", "--audit"], 15)
    if not r or r[0] != 0 or not r[1].strip():
        return []
    return summarize_apt(r[1].splitlines())["failedPackages"]


def collect_updates_apt():
    sim = run_cmd(["apt-get", "-s", "-q", "-o", "Debug::NoLocking=true", "dist-upgrade"], 60)
    if sim is None:
        return 500, {"error": "apt_failed", "detail": "apt-get startet nicht oder braucht zu lange"}
    broken = broken_packages()
    rc, out, err = sim
    if rc != 0:
        if broken:       # die Simulation scheitert oft gerade an halb eingerichteten Paketen: dann nicht "Fehler", sondern der Weg zur Reparatur (die Liste der Updates ist dann unbekannt)
            return 200, finish_updates("apt", [], [], [], apt_lists_checked(), 0, proxmox=is_proxmox(), broken=broken, unreadable=True)
        return 500, {"error": "apt_failed", "detail": clean(" ".join((err or out).split())[-200:], 200)}
    parsed = parse_apt_simulation(out)
    kept = list(parsed["keptBack"])
    if parsed["packages"]:
        # was `apt-get upgrade` (nicht dist-upgrade) zurueckhaelt: Pakete mit neuen Abhaengigkeiten, zum Beispiel ein neuer Kernel
        up = run_cmd(["apt-get", "-s", "-q", "-o", "Debug::NoLocking=true", "upgrade"], 60)
        if up and up[0] == 0:
            kept += [k for k in parse_apt_simulation(up[1])["keptBack"] if k not in kept]
    hold = run_cmd(["apt-mark", "showhold"], 10)
    held = [clean(x, 80) for x in hold[1].split()] if hold and hold[0] == 0 else []
    return 200, finish_updates("apt", parsed["packages"], kept, held, apt_lists_checked(), parsed["removals"], proxmox=is_proxmox(), broken=broken)


def parse_dnf_check_update(text):
    out = []
    for line in text.splitlines():
        parts = line.split()
        if len(parts) == 3 and re.search(r"\.[A-Za-z0-9_]+$", parts[0]) and re.match(r"^\d|^\d+:", parts[1]):
            out.append({"name": clean(parts[0].rsplit(".", 1)[0], 80), "from": None, "to": clean(parts[1], 64), "security": None, "new": False})
    return out


def parse_apk_upgradable(text):
    out = []
    for line in text.splitlines():
        m = re.match(r"^(.+?)-(\d[^\s-]*-r\d+) \S+ \{[^}]*\} \([^)]*\) \[upgradable from: (.+?)-(\d[^\s-]*-r\d+)\]", line)
        if m:
            out.append({"name": clean(m.group(1), 80), "from": clean(m.group(4), 64), "to": clean(m.group(2), 64), "security": None, "new": False})
    return out


def parse_checkupdates(text):
    out = []
    for line in text.splitlines():
        m = re.match(r"^(\S+) (\S+) -> (\S+)$", line.strip())
        if m:
            out.append({"name": clean(m.group(1), 80), "from": clean(m.group(2), 64), "to": clean(m.group(3), 64), "security": None, "new": False})
    return out


def finish_updates(manager, packages, kept, held, lists_checked, removals=0, proxmox=False, broken=(), unreadable=False):
    """Die Antwort des Agenten: Sicherheitsupdates zuerst, die Liste bei MAX_PACKAGES abgeschnitten. security ist null, wo der Paketmanager es nicht sagt."""
    now = int(time.time())
    known = manager == "apt"
    total = len(packages)
    ordered = sorted(packages, key=lambda p: (not p["security"], p["name"]))
    return {
        "manager": manager, "count": total, "security": sum(1 for p in packages if p["security"]) if known else None,
        "securityKnown": known, "truncated": total > MAX_PACKAGES, "packages": ordered[:MAX_PACKAGES],
        "keptBack": kept[:MAX_ITEMS], "held": held[:MAX_ITEMS], "removals": removals, "proxmox": bool(proxmox), "broken": list(broken)[:MAX_ITEMS], "unreadable": bool(unreadable), "newPackages": sum(1 for p in packages if p.get("new")),
        "listsCheckedAt": lists_checked, "listsAgeSeconds": max(0, now - lists_checked) if lists_checked else None, "checkedAt": now,
    }


def update_manager(which=shutil.which):
    for tool, name in (("apt-get", "apt"), ("dnf", "dnf"), ("apk", "apk"), ("checkupdates", "pacman")):
        if which(tool):
            return name
    return None


def collect_updates():
    manager = update_manager()
    if manager == "apt":
        return collect_updates_apt()
    if manager == "dnf":
        r = run_cmd(["dnf", "-q", "-C", "check-update"], 60)          # -C: nur den Zwischenspeicher, nichts aktualisieren; Rueckgabewert 100 = Updates da
        if r and r[0] in (0, 100):
            return 200, finish_updates("dnf", parse_dnf_check_update(r[1]), [], [], None)
    elif manager == "apk":
        r = run_cmd(["apk", "list", "-u"], 30)
        if r and r[0] == 0:
            return 200, finish_updates("apk", parse_apk_upgradable(r[1]), [], [], None)
    elif manager == "pacman":
        r = run_cmd(["checkupdates"], 60)                              # Rueckgabewert 2 = keine Updates
        if r and r[0] in (0, 2):
            return 200, finish_updates("pacman", parse_checkupdates(r[1]), [], [], None)
    elif manager is None:
        return 404, {"error": "unsupported", "detail": "kein Paketmanager gefunden"}
    return 500, {"error": "update_check_failed", "detail": manager}


# ---- Dienste (systemd)
def parse_failed_units(text):
    out = []
    for line in text.splitlines():
        line = line.lstrip("●* ").rstrip()
        parts = line.split(None, 4)
        if len(parts) >= 4 and parts[0].endswith((".service", ".socket", ".timer", ".mount", ".target", ".path", ".scope", ".slice")):
            out.append({"unit": clean(parts[0], 80), "sub": clean(parts[3], 24), "description": clean(parts[4] if len(parts) > 4 else "", 100)})
    return out


def systemd_running(exists=os.path.exists):
    return exists("/run/systemd/system")


def collect_services():
    if not systemd_running():
        return 404, {"error": "unsupported", "detail": "kein systemd"}
    r = run_cmd(["systemctl", "--failed", "--no-legend", "--plain", "--no-pager"], 15)
    if r is None or r[0] != 0:
        return 500, {"error": "systemctl_failed"}
    state = run_cmd(["systemctl", "is-system-running"], 10)
    failed = parse_failed_units(r[1])
    return 200, {"state": clean(state[1].strip(), 24) if state else "unknown", "count": len(failed), "failed": failed[:MAX_ITEMS], "checkedAt": int(time.time())}


# ---- Docker (nur lesend: genau eine Anfrage, GET /containers/json)
class _UnixHTTP(http.client.HTTPConnection):
    def __init__(self, path, timeout=5):
        super().__init__("localhost", timeout=timeout)
        self._path = path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self._path)


def summarize_containers(items):
    """Die Antwort von /containers/json (all=1) in die Anzeigeform: laufende zuerst, hoechstens MAX_CONTAINERS."""
    out = []
    for c in items if isinstance(items, list) else []:
        if not isinstance(c, dict):
            continue
        names = c.get("Names") if isinstance(c.get("Names"), list) else []
        entry = {"id": clean(str(c.get("Id", ""))[:12], 12), "name": clean((names[0] if names else "").lstrip("/"), 64), "image": clean(c.get("Image"), 100),
                 "state": clean(c.get("State"), 16), "status": clean(c.get("Status"), 64)}
        labels = c.get("Labels") if isinstance(c.get("Labels"), dict) else {}
        project = clean(labels.get("com.docker.compose.project"), 40)       # Compose-Projekt: der Server warnt, wenn es JulOS selbst ist
        if project:
            entry["project"] = project
        out.append(entry)
    running = sum(1 for c in out if c["state"] == "running")
    restarting = sum(1 for c in out if c["state"] == "restarting")        # Container in einer Neustart-Schleife
    out.sort(key=lambda c: (c["state"] != "running", c["name"]))
    return {"available": True, "running": running, "restarting": restarting, "total": len(out), "containers": out[:MAX_CONTAINERS], "truncated": len(out) > MAX_CONTAINERS}


def collect_docker(socket_path=None):
    path = socket_path or getattr(ARGS, "docker_socket", None) or DOCKER_SOCKET
    if not os.path.exists(path):
        return 200, {"available": False, "reason": "no_socket"}
    conn = _UnixHTTP(path)
    try:
        conn.request("GET", "/containers/json?all=1", headers={"Host": "docker", "User-Agent": "julos-agent/" + AGENT_VERSION})
        r = conn.getresponse()
        data = r.read(4 * 1024 * 1024 + 1)
        if r.status != 200 or len(data) > 4 * 1024 * 1024:
            return 200, {"available": False, "reason": "error"}
        return 200, summarize_containers(json.loads(data.decode("utf-8", "replace")))
    except PermissionError:
        return 200, {"available": False, "reason": "no_access"}
    except (OSError, ValueError, http.client.HTTPException):
        return 200, {"available": False, "reason": "error"}
    finally:
        conn.close()


def host_caps(exists=os.path.exists, which=shutil.which):
    """Was dieser Rechner kann; das meldet der Agent in seinem hello. docker steht auch ohne Zugriff dabei (die Antwort sagt dann 'kein Zugriff')."""
    caps = ["sysinfo"] if sys.platform.startswith("linux") else []
    if update_manager(which):
        caps.append("updates")
    if systemd_running(exists):
        caps.append("services")
    if exists(getattr(ARGS, "docker_socket", None) or DOCKER_SOCKET):
        caps.append("docker")
    return caps


HOST_READS = {          # Pfad -> (Faehigkeit, Funktion); die Funktionen werden erst beim Aufruf nachgeschlagen
    "/v1/host/sysinfo": ("sysinfo", lambda: (200, collect_sysinfo())),
    "/v1/host/updates": ("updates", lambda: collect_updates()),
    "/v1/host/services": ("services", lambda: collect_services()),
    "/v1/host/docker": ("docker", lambda: collect_docker()),
}


# ---------------------------------------------------------------- Schreib-Aktionen (Stufe 3, docs/HOST-AGENT.md)
# Der Agent fuehrt nur aus, was der Server nach der Bestaetigung des Benutzers schickt, und nur, wenn die lokale Datei /etc/julos-agent/permissions das Wort der Aktionsart enthaelt.
# Feste Pfade, geprueftes Ziel (Paket aus der Update-Liste, Dienst aus der Dienstliste, Container aus der Containerliste), kein freier Befehl, keine Shell.
# Was root braucht (apt, systemctl, journalctl, reboot), laeuft NICHT hier, sondern ueber den kleinen Helfer tools/julos-agent-helper.py (root-eigen, Socket-Dienst): der Agent
# bleibt ein Benutzer ohne Rechte und ohne sudo. Docker geht ueber den Socket, den die Lese-Funktionen schon benutzen. Eine schreibende Aktion zugleich (409 busy).
HELPER_SOCKET = os.environ.get("JULOS_AGENT_HELPER_SOCKET") or "/run/julos-agent-helper.sock"
JOB_FILE = os.path.join(STATE_DIR, "jobs.json")
JOB_KEEP = 20
JOB_RE = re.compile(r"^[0-9a-f]{32}\Z")
# Alle Muster enden mit \Z, nicht mit $ ($ liesse einen Zeilenumbruch am Ende durch).
# Debian-Paketname; kein '-' am Ende (apt-get install paket- ENTFERNT das Paket), keine '=', '/' oder Leerzeichen (Version, Release)
PKG_RE = re.compile(r"^[a-z0-9](?:[a-z0-9+.\-]*[a-z0-9+.])?(?::[a-z0-9]+)?\Z")
UNIT_RE = re.compile(r"^(?!-)[A-Za-z0-9:_.@\-]{1,100}\.service\Z")
CONTAINER_RE = re.compile(r"^[0-9a-f]{12}\Z")
IMAGE_RE = re.compile(r"^[a-z0-9][a-zA-Z0-9._/:\-]{0,199}\Z")
MAX_UNITS = 300
MAX_PACKAGES_PER_ACTION = 20
LOG_LINES_MIN, LOG_LINES_MAX, LOG_LINES_DEFAULT = 10, 200, 100
LOG_LINE_WIDTH = 400
OUTPUT_TAIL = 4000           # Zeichen der Ausgabe eines Programms, die ein Ergebnis hoechstens mitbringt
POWER_DELAY = 5              # Sekunden zwischen "angenommen" und dem Neustart: der Server soll die Antwort noch bekommen
# (Pfad -> (Recht, Aktion)): die einzigen schreibenden Pfade
WRITE_ROUTES = {
    "/v1/host/updates/refresh": ("updates", "updates.refresh"),
    "/v1/host/updates/install": ("updates", "updates.install"),
    "/v1/host/updates/repair": ("updates", "updates.repair"),
    "/v1/host/reboot": ("reboot", "power.reboot"),
    "/v1/host/poweroff": ("reboot", "power.poweroff"),
    "/v1/host/service/start": ("services", "service.start"),
    "/v1/host/service/stop": ("services", "service.stop"),
    "/v1/host/service/restart": ("services", "service.restart"),
    "/v1/host/docker/start": ("docker", "docker.start"),
    "/v1/host/docker/stop": ("docker", "docker.stop"),
    "/v1/host/docker/restart": ("docker", "docker.restart"),
    "/v1/host/docker/pull": ("docker", "docker.pull"),
    "/v1/host/logs/unit": ("logs", "logs.unit"),
    "/v1/host/logs/container": ("logs", "logs.container"),
}
WRITE_LOCK = threading.Lock()
_JOBS = None


def clean_lines(text, count, width=LOG_LINE_WIDTH):
    """Die letzten count Zeilen eines Textes, jede nur aus druckbaren Zeichen und hoechstens width lang."""
    lines = [clean(l.rstrip("\r"), width) for l in str(text or "").split("\n")]
    while lines and not lines[-1]:
        lines.pop()
    return lines[-count:] if count > 0 else []


def tail_text(lines, limit=OUTPUT_TAIL):
    return "\n".join(lines)[-limit:]


class JobStore:
    """Die letzten Auftraege (laufend und fertig) mit Ergebnis, im Speicher und in einer Datei des Dienstbenutzers: so kann der Server das Ergebnis nach einem Verbindungsabbruch
    abholen (GET /v1/host/jobs). Logs (Inhalt) werden nie gespeichert. Ein Auftrag, der beim Start noch als laufend in der Datei steht, ist unterbrochen."""

    def __init__(self, path, keep=JOB_KEEP):
        self.path, self.keep, self.lock = path, keep, threading.Lock()
        self.items = []
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
            for j in (data.get("jobs") if isinstance(data, dict) else None) or []:
                if isinstance(j, dict) and isinstance(j.get("job"), str) and JOB_RE.match(j["job"]):
                    if j.get("state") == "running":
                        j["state"], j["status"], j["result"] = "interrupted", 0, {"error": "interrupted"}
                    self.items.append(j)
        except (OSError, ValueError):
            pass
        self.items = self.items[-keep:]

    def _save(self):
        try:
            os.makedirs(os.path.dirname(self.path), exist_ok=True)
            tmp = self.path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump({"jobs": self.items}, f)
            os.chmod(tmp, 0o600)
            os.replace(tmp, self.path)
        except OSError as e:
            log.warning("Auftragsliste nicht gespeichert: %s", e)

    def has(self, job):
        with self.lock:
            return any(j["job"] == job for j in self.items)

    def start(self, job, action, target):
        rec = {"job": job, "action": action, "target": target, "state": "running", "startedAt": int(time.time()), "progress": ""}
        with self.lock:
            self.items.append(rec)
            del self.items[:-self.keep]
            self._save()

    def progress(self, job, text, done=None, total=None):
        with self.lock:
            for j in self.items:
                if j["job"] == job:
                    j["progress"] = clean(text, 160)
                    if done is not None:
                        j["done"], j["total"] = int(done), int(total or 0)

    def finish(self, job, status, result, state=None):
        with self.lock:
            for j in self.items:
                if j["job"] == job:
                    j.update(state=state or ("done" if status == 200 else "failed"), status=int(status), result=result, finishedAt=int(time.time()))
            self._save()

    def snapshot(self):
        with self.lock:
            return [dict(j) for j in reversed(self.items)]


def job_store():
    global _JOBS
    if _JOBS is None:
        _JOBS = JobStore(JOB_FILE)
    return _JOBS


def helper_call(request, on_line=None, timeout=3600, path=None):
    """Schickt dem root-Helfer (tools/julos-agent-helper.py) EINE Anfrage und liest, bis er ein Ergebnis oder "angenommen" meldet. None, wenn der Helfer nicht erreichbar ist.
    Zeilen "p" sind Fortschritt (an on_line), "a" heisst angenommen (Neustart folgt), "r" ist das Ergebnis. Der Helfer prueft Recht und Werte selbst noch einmal."""
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.settimeout(30)
    try:
        s.connect(path or HELPER_SOCKET)
    except OSError:
        s.close()
        return None
    try:
        s.sendall(json.dumps(request, separators=(",", ":")).encode() + b"\n")
        s.settimeout(timeout)
        f = s.makefile("rb")
        end = time.time() + timeout
        while time.time() < end:
            raw = f.readline(1048576)
            if not raw:
                return {"ok": False, "code": "helper_closed"}
            try:
                msg = json.loads(raw.decode("utf-8", "replace"))
            except ValueError:
                continue
            if not isinstance(msg, dict):
                continue
            if isinstance(msg.get("p"), str):
                if on_line:
                    on_line(msg["p"])
            elif msg.get("a"):
                return {"ok": True, "accepted": True}
            elif isinstance(msg.get("r"), dict):
                return msg["r"]
        return {"ok": False, "code": "timeout"}
    except (OSError, socket.timeout):
        return {"ok": False, "code": "helper_closed"}
    finally:
        s.close()


def docker_call(method, path, timeout=30, limit=4 * 1024 * 1024, socket_path=None):
    """Eine Anfrage an die Docker-Schnittstelle (derselbe Socket wie beim Lesen). (Status, Bytes); None, wenn der Socket fehlt; Status 'denied' ohne Zugriff, 'error' bei Fehlern."""
    sock = socket_path or getattr(ARGS, "docker_socket", None) or DOCKER_SOCKET
    if not os.path.exists(sock):
        return None
    conn = _UnixHTTP(sock, timeout=timeout)
    try:
        conn.request(method, path, headers={"Host": "docker", "User-Agent": "julos-agent/" + AGENT_VERSION})
        r = conn.getresponse()
        return r.status, r.read(limit)
    except PermissionError:
        return "denied", b""
    except (OSError, http.client.HTTPException):
        return "error", b""
    finally:
        conn.close()


class Ctx:
    """Was eine laufende Aktion braucht: ihre Auftragsnummer und den Fortschrittskanal zum Server (emit(text, done, total))."""

    def __init__(self, job, emit):
        self.job, self.emit = job, emit
        self.last = 0.0
        self.pending = []

    def line(self, text, done=None, total=None):
        job_store().progress(self.job, text, done, total)
        self.pending = (self.pending + [clean(text, 200)])[-20:]
        now = time.time()
        if now - self.last >= 0.4:          # nicht jede Zeile von apt einzeln schicken: die Zeilen seit der letzten Meldung gehen gebuendelt mit
            self.last, lines, self.pending = now, self.pending, []
            try:
                self.emit(clean(text, 160), done, total, lines)
            except TypeError:
                try:
                    self.emit(clean(text, 160), done, total)       # ein Empfaenger ohne Zeilen (aelterer Aufrufer)
                except Exception:  # noqa: BLE001
                    pass
            except Exception:  # noqa: BLE001  # ein Fortschritt darf nie die Aktion stoeren
                pass


def err(status, code, **extra):
    return status, {"error": code, **extra}


LOG_STORE_LINES = 200        # so viele Zeilen des Protokolls bleiben in der Auftragsdatei (das ganze Protokoll geht nur an den Server, nie auf die Platte)
LOG_KEEP_LINES = 2000
LOG_KEEP_WIDTH = 240
LOG_KEEP_BYTES = 200 * 1024
_BROKEN_HEAD = ("half configured", "half installed", "in a mess", "awaiting trigger", "triggers", "unpacked but")


def clean_log(text, limit=LOG_KEEP_WIDTH):
    """Eine Protokollzeile: nur druckbare Zeichen, Einrueckung bleibt (apt rueckt Paketlisten ein), hoechstens limit Zeichen."""
    return "".join(c for c in str(text if text is not None else "") if c.isprintable()).rstrip()[:limit]


def summarize_apt(lines):
    """Die Fehlerzusammenfassung eines apt- oder dpkg-Laufs aus seinem Protokoll (Zeilen mit Einrueckung). Gibt zurueck:
      errors          die 'E:'-Zeilen (hoechstens 8)
      failedPackages  Pakete aus 'Errors were encountered while processing:', 'dpkg: error processing package X' und der Liste von `dpkg --audit` (halb eingerichtet)
      keptBack        Pakete unter 'The following packages have been kept back:'
      configQuestion  dpkg wollte eine Frage zu einer Konfigurationsdatei stellen (der Lauf kann sie nicht beantworten)
      locked          die Paketverwaltung war gesperrt
      installed       wie viele Pakete 'Setting up' meldeten
    Nur Text aus dem Protokoll, bereinigt und begrenzt; nichts davon wird ausgefuehrt."""
    errors, failed, kept, mode, waiting = [], [], [], None, False
    installed, question, locked = 0, False, False
    for raw in lines:
        line = clean_log(raw)
        text = line.strip()
        low = text.lower()
        if mode and line[:1] in (" ", "\t") and text:
            tokens = text.split()
            (kept if mode == "kept" else failed).extend(tokens if mode == "kept" else tokens[:1])
            waiting = False
            continue
        if mode and waiting and text:
            continue                                              # die Einleitung von `dpkg --audit` geht ueber mehrere Zeilen, erst danach kommt die Liste
        mode = None
        if text.startswith("E: ") and len(errors) < 8 and text not in errors:
            errors.append(text[:200])
        if "could not get lock" in low or "unable to acquire the dpkg" in low:
            locked = True
        head, sep, rest = text.partition(":")
        if low.startswith("errors were encountered while processing"):
            mode = "failed"
            failed.extend(rest.split())                           # steht die Liste in derselben Zeile
        elif low.startswith("the following packages have been kept back"):
            mode = "kept"
            kept.extend(rest.split())
        elif low.startswith("the following packages") and any(w in low for w in _BROKEN_HEAD):
            mode, waiting = "failed", True
        m = re.match(r"^dpkg: error processing package (\S+)", text)
        if m:
            failed.append(m.group(1))
        if low.startswith("setting up "):
            installed += 1
        if "configuration file '" in low or "install the package maintainer's version" in low or "y or i  :" in low:
            question = True
    uniq = lambda xs: [x for i, x in enumerate(xs) if x not in xs[:i] and PKG_RE.match(x)][:MAX_ITEMS]
    return {"errors": errors, "failedPackages": uniq(failed), "keptBack": uniq(kept), "configQuestion": question, "locked": locked, "installed": installed}


def from_helper(r, **extra):
    """Das Ergebnis des Helfers als (Status, Objekt): ok -> 200, sonst ein kurzer Fehlercode (nie freier Text des Helfers)."""
    if r is None:
        return err(503, "helper_unavailable")
    if r.get("ok"):
        out = {"ok": True, **extra}
        if r.get("accepted"):
            out["accepted"] = True
        if isinstance(r.get("lines"), list):
            out["lines"] = [clean(l, LOG_LINE_WIDTH) for l in r["lines"][:LOG_LINES_MAX] if isinstance(l, str)]
        if isinstance(r.get("output"), str):
            out["output"] = tail_text(clean_lines(r["output"], 40))
        add_log(out, r)
        return 200, out
    code = r.get("code") if isinstance(r.get("code"), str) and re.match(r"^[a-z_]{1,32}$", r["code"]) else "action_failed"
    out = {"error": code, **extra}
    if isinstance(r.get("output"), str):
        out["output"] = tail_text(clean_lines(r["output"], 40))
    add_log(out, r, failed=code)
    return (403 if code == "not_permitted" else 500), out


def add_log(out, r, failed=None):
    """Das Protokoll (alle Zeilen, die der Helfer mitgibt, begrenzt) und seine Zusammenfassung ins Ergebnis; bei einem Fehler auch 'needsRepair', wenn Pakete halb eingerichtet blieben."""
    log_lines = r.get("log") if isinstance(r.get("log"), list) else None
    if log_lines is None:
        return
    log_lines = [clean_log(l) for l in log_lines[-LOG_KEEP_LINES:] if isinstance(l, str)]
    total = sum(len(l) + 1 for l in log_lines)
    while log_lines and total > LOG_KEEP_BYTES:                  # die Antwort an den Server bleibt klein (Grenze 600 kB): die aeltesten Zeilen fallen zuerst weg
        total -= len(log_lines.pop(0)) + 1
    summary = summarize_apt(log_lines)
    out["log"], out["summary"] = log_lines, summary
    if failed in ("dpkg_failed", "dpkg_broken") or (failed and summary["failedPackages"]):
        out["needsRepair"] = True


def int_in(value, low, high, default):
    return max(low, min(high, value)) if isinstance(value, int) and not isinstance(value, bool) else default


# ---- Listen, aus denen Ziele stammen duerfen
def parse_units(text):
    out = []
    for line in text.splitlines():
        parts = line.lstrip("●* ").split(None, 4)
        if len(parts) >= 4 and UNIT_RE.match(parts[0]) and parts[1] == "loaded":
            out.append({"unit": clean(parts[0], 100), "active": clean(parts[2], 16), "sub": clean(parts[3], 24), "description": clean(parts[4] if len(parts) > 4 else "", 100)})
    out.sort(key=lambda u: u["unit"])
    return out


def collect_units():
    """Die geladenen systemd-Dienste (nur lesen). Das ist die Liste, aus der Dienstnamen fuer Aktionen stammen muessen."""
    if not systemd_running():
        return 404, {"error": "unsupported", "detail": "kein systemd"}
    r = run_cmd(["systemctl", "list-units", "--type=service", "--all", "--no-legend", "--plain", "--no-pager"], 20)
    if r is None or r[0] != 0:
        return 500, {"error": "systemctl_failed"}
    units = parse_units(r[1])
    return 200, {"units": units[:MAX_UNITS], "count": len(units), "truncated": len(units) > MAX_UNITS, "checkedAt": int(time.time())}


def known_units():
    status, body = collect_units()
    return {u["unit"] for u in body["units"]} if status == 200 else None


def known_containers():
    """{Id (12 Zeichen): Eintrag} der Containerliste des Agenten; None, wenn Docker nicht lesbar ist."""
    status, body = collect_docker()
    if status != 200 or not body.get("available"):
        return None
    return {c["id"]: c for c in body["containers"]}


def protected_unit(unit):
    """Dienste, die der Agent nicht stoppen oder neu starten darf: er selbst und sein Helfer (sonst waere die Verbindung weg und niemand koennte ihn wieder starten)."""
    return unit.startswith("julos-agent") or unit == UNIT_NAME


def reboot_required(exists=os.path.exists):
    return exists("/var/run/reboot-required")


def reboot_needed():
    """Neustart noetig: die Datei von apt oder ein neuerer Kernel als der laufende."""
    return reboot_required() or bool(kernel_pending(os.uname().release, read_installed_kernels()))


def apt_progress(ctx, total):
    state = {"done": 0}

    def on_line(text):
        if text.startswith("Setting up "):
            state["done"] += 1
        ctx.line(text, min(state["done"], total) if total else None, total or None)
    return on_line


# ---- Die Aktionen
def act_updates_refresh(body, ctx):
    if update_manager() != "apt":
        return err(501, "not_implemented", detail="nur apt")
    ctx.line("apt-get update")
    return from_helper(helper_call({"verb": "apt.refresh"}, ctx.line, 600), summary="refreshed")


def act_updates_install(body, ctx):
    packages = body.get("packages")
    everything = body.get("all") is True
    if everything == (packages is not None):
        return err(400, "invalid_request", field="packages")            # genau eins von beiden
    if body.get("full") not in (None, True, False) or (body.get("full") is True and not everything):
        return err(400, "invalid_request", field="full")
    if update_manager() != "apt":
        return err(501, "not_implemented", detail="nur apt")
    status, upd = collect_updates()
    if status != 200:
        return err(500, "update_check_failed")
    proxmox = bool(upd.get("proxmox"))
    if proxmox and not everything:
        return err(409, "proxmox_whole")                               # Proxmox wird nur als Ganzes aktualisiert (dist-upgrade); einzelne Pakete lassen es halb aktualisiert zurueck
    full = everything and (proxmox or body.get("full") is True)         # auf Proxmox immer dist-upgrade, sonst nur auf ausdruecklichen Wunsch
    listed = {p["name"] for p in upd.get("packages", [])}
    if not everything:
        if not isinstance(packages, list) or not 1 <= len(packages) <= MAX_PACKAGES_PER_ACTION or not all(isinstance(p, str) and PKG_RE.match(p) for p in packages):
            return err(400, "invalid_request", field="packages")
        if any(p not in listed for p in packages):
            return err(404, "unknown_target")                          # nur Pakete der Update-Liste
        packages = list(dict.fromkeys(packages))
    total = len(packages) if not everything else int(upd.get("count") or 0)
    ctx.line("apt-get " + ("dist-upgrade" if full else "upgrade" if everything else "install --only-upgrade"), 0, total or None)
    req = {"verb": "apt.upgrade"}
    if not everything:
        req["packages"] = packages
    elif full:
        req["full"] = True
    r = helper_call(req, apt_progress(ctx, total), 3600)
    return from_helper(r, rebootRequired=reboot_needed(), count=total, **({"full": True} if full else {}))


def act_updates_repair(body, ctx):
    """Halb eingerichtete Pakete reparieren (der Helfer fuehrt dpkg --configure -a und danach apt-get -f install aus)."""
    if update_manager() != "apt":
        return err(501, "not_implemented", detail="nur apt")
    ctx.line("dpkg --configure -a")
    return from_helper(helper_call({"verb": "apt.repair"}, apt_progress(ctx, 0), 3700), rebootRequired=reboot_needed())


def act_power(op):
    def run(body, ctx):
        ctx.line(op)
        return from_helper(helper_call({"verb": "power", "op": op, "delay": POWER_DELAY}, None, 30), delay=POWER_DELAY)
    return run


def act_service(op):
    def run(body, ctx):
        unit = body.get("unit")
        if not isinstance(unit, str) or not UNIT_RE.match(unit):
            return err(400, "invalid_request", field="unit")
        if not systemd_running():
            return err(404, "unsupported")
        units = known_units()
        if units is None:
            return err(500, "systemctl_failed")
        if unit not in units:
            return err(404, "unknown_target")                          # nur Dienste aus der gemeldeten Liste
        if op != "start" and protected_unit(unit):
            return err(409, "protected_unit")
        ctx.line("systemctl %s %s" % (op, unit))
        return from_helper(helper_call({"verb": "service", "op": op, "unit": unit}, None, 120), unit=unit)
    return run


def act_logs_unit(body, ctx):
    unit = body.get("unit")
    if not isinstance(unit, str) or not UNIT_RE.match(unit):
        return err(400, "invalid_request", field="unit")
    if not systemd_running():
        return err(404, "unsupported")
    units = known_units()
    if units is None:
        return err(500, "systemctl_failed")
    if unit not in units:
        return err(404, "unknown_target")
    n = int_in(body.get("lines"), LOG_LINES_MIN, LOG_LINES_MAX, LOG_LINES_DEFAULT)
    return from_helper(helper_call({"verb": "journal", "unit": unit, "lines": n}, None, 60), unit=unit)


def demux_docker_log(data, tty):
    """Die Ausgabe von /containers/{id}/logs: ohne TTY in Rahmen (8 Byte Kopf: Strom, Laenge), mit TTY der blanke Text."""
    if tty:
        return data.decode("utf-8", "replace")
    out, i = [], 0
    while i + 8 <= len(data):
        size = struct.unpack(">I", data[i + 4:i + 8])[0]
        out.append(data[i + 8:i + 8 + size].decode("utf-8", "replace"))
        i += 8 + size
    return "".join(out)


def _container(body):
    """(Eintrag, None) fuer eine Container-Id aus der eigenen Liste, sonst (None, Fehlerantwort)."""
    cid = body.get("id")
    if not isinstance(cid, str) or not CONTAINER_RE.match(cid):
        return None, err(400, "invalid_request", field="id")
    known = known_containers()
    if known is None:
        return None, err(503, "docker_unavailable")
    if cid not in known:
        return None, err(404, "unknown_target")                        # nur Container der eigenen Liste
    return known[cid], None


def docker_result(r, ok=(204,), **extra):
    if r is None:
        return err(503, "docker_unavailable")
    if r[0] in ("denied", 403):
        return err(403, "docker_denied")
    if r[0] == "error":
        return err(500, "docker_failed")
    if r[0] in ok:
        return 200, {"ok": True, **extra}
    if r[0] == 304:
        return 200, {"ok": True, "already": True, **extra}
    if r[0] == 404:
        return err(404, "unknown_target")
    return err(500, "docker_failed")


def act_logs_container(body, ctx):
    c, bad = _container(body)
    if bad:
        return bad
    n = int_in(body.get("lines"), LOG_LINES_MIN, LOG_LINES_MAX, LOG_LINES_DEFAULT)
    info = docker_call("GET", "/containers/%s/json" % c["id"], 15)
    tty = False
    if info and info[0] == 200:
        try:
            tty = bool(json.loads(info[1].decode("utf-8", "replace")).get("Config", {}).get("Tty"))
        except (ValueError, AttributeError):
            pass
    r = docker_call("GET", "/containers/%s/logs?stdout=1&stderr=1&tail=%d" % (c["id"], n), 30, 512 * 1024)
    if not r or r[0] != 200:
        return docker_result(r, ok=())
    return 200, {"ok": True, "container": c["name"], "lines": clean_lines(demux_docker_log(r[1], tty), n)}


def act_docker(op):
    def run(body, ctx):
        c, bad = _container(body)
        if bad:
            return bad
        ctx.line("docker %s %s" % (op, c["name"]))
        suffix = "" if op == "start" else "?t=10"
        return docker_result(docker_call("POST", "/containers/%s/%s%s" % (c["id"], op, suffix), 90), container=c["name"])
    return run


def image_ref(image):
    """(Name, Tag) eines Images aus der Containerliste; None bei einer blossen Id oder unbrauchbarem Text: dort gibt es nichts zu holen. '@' (Digest) ist im Muster nicht erlaubt."""
    if not isinstance(image, str) or not IMAGE_RE.match(image) or re.fullmatch(r"(sha256:)?[0-9a-f]{12,64}", image):
        return None
    name, tag = image, "latest"
    if ":" in image.rsplit("/", 1)[-1]:
        name, tag = image.rsplit(":", 1)
    return (name, tag) if name and tag else None


def act_docker_pull(body, ctx):
    c, bad = _container(body)
    if bad:
        return bad
    ref = image_ref(c["image"])
    if not ref:
        return err(409, "no_registry_image")
    name, tag = ref
    full = "%s:%s" % (name, tag)

    def image_id():
        r = docker_call("GET", "/images/%s/json" % urllib.parse.quote(full, safe="/:"), 15)
        try:
            return json.loads(r[1].decode()).get("Id") if r and r[0] == 200 else None
        except (ValueError, AttributeError):
            return None
    before = image_id()
    ctx.line("docker pull " + full)
    r = docker_call("POST", "/images/create?fromImage=%s&tag=%s" % (urllib.parse.quote(name, safe="/"), urllib.parse.quote(tag)), 900, 4 * 1024 * 1024)
    if r is None or r[0] in ("denied", "error"):
        return docker_result(r)
    if r[0] != 200:
        return err(404 if r[0] == 404 else 500, "pull_failed")
    if any('"error"' in line for line in r[1].decode("utf-8", "replace").splitlines()):      # der Strom meldet Fehler als Zeile mit Status 200
        return err(500, "pull_failed")
    after = image_id()
    return 200, {"ok": True, "container": c["name"], "image": full, "updated": bool(after and after != before)}


ACTIONS = {
    "updates.refresh": act_updates_refresh,
    "updates.install": act_updates_install,
    "updates.repair": act_updates_repair,
    "power.reboot": act_power("reboot"),
    "power.poweroff": act_power("poweroff"),
    "service.start": act_service("start"),
    "service.stop": act_service("stop"),
    "service.restart": act_service("restart"),
    "docker.start": act_docker("start"),
    "docker.stop": act_docker("stop"),
    "docker.restart": act_docker("restart"),
    "docker.pull": act_docker_pull,
    "logs.unit": act_logs_unit,
    "logs.container": act_logs_container,
}
KEEP_BUSY_AFTER = {"power.reboot", "power.poweroff"}       # der Rechner faehrt herunter: bis dahin keine weitere schreibende Aktion


def target_label(action, body):
    if action == "updates.install":
        return "all" if body.get("all") is True else ",".join(str(p) for p in (body.get("packages") if isinstance(body.get("packages"), list) else [])[:MAX_PACKAGES_PER_ACTION])[:200]
    return clean(body.get("unit") or body.get("id") or "", 100)


def perform(action, body, emit=None):
    """Eine schreibende Aktion: Auftragsnummer pruefen, die Sperre nehmen (eine Aktion zugleich), ausfuehren, das Ergebnis merken. (Status, Objekt)"""
    emit = emit or (lambda *a, **k: None)
    if not isinstance(body, dict):
        return err(400, "invalid_request")
    job = body.get("job")
    if not isinstance(job, str) or not JOB_RE.match(job):
        return err(400, "invalid_request", field="job")
    jobs = job_store()
    if jobs.has(job):
        return err(409, "duplicate_job")                               # ein Auftrag laeuft nur einmal (Wiederholung einer Anfrage tut nichts)
    if not WRITE_LOCK.acquire(blocking=False):
        running = next((j for j in jobs.snapshot() if j.get("state") == "running"), {})
        return err(409, "busy", job=running.get("job"), action=running.get("action"))
    release = True
    try:
        jobs.start(job, action, target_label(action, body))
        try:
            status, res = ACTIONS[action](body, Ctx(job, emit))
        except Exception as e:  # noqa: BLE001
            log.error("Aktion %s fehlgeschlagen: %s", action, e)
            status, res = err(500, "failed")
        res = dict(res, job=job, action=action)
        stored = {k: v for k, v in res.items() if k != "lines"}         # der Inhalt von Logs wird nicht gespeichert
        if isinstance(stored.get("log"), list):
            stored["log"] = stored["log"][-LOG_STORE_LINES:]            # vom Protokoll eines apt-Laufs bleibt in der Datei nur das Ende (die Zusammenfassung bleibt ganz)
        jobs.finish(job, status, stored, state="accepted" if status == 200 and res.get("accepted") else None)
        release = not (status == 200 and action in KEEP_BUSY_AFTER)
        return status, res
    finally:
        if release:
            WRITE_LOCK.release()
        else:
            threading.Timer(120, WRITE_LOCK.release).start()


def public_jobs():
    keys = ("job", "action", "target", "state", "startedAt", "finishedAt", "progress", "done", "total", "status", "result")
    return {"jobs": [{k: v for k, v in j.items() if k in keys} for j in job_store().snapshot()]}


# ---------------------------------------------------------------- Dateien als root (Wort files-root, docs/FILES.md)
# Der Agent reicht nur weiter: feste Pfade /v1/host/files/<verb>, geprueftes Wort, geformte Anfrage; die Arbeit macht der root-Helfer (Verben files.*), der Wort und Werte selbst noch einmal prueft.
# Kein Auftrag und keine Sperre: jede Anfrage steht fuer sich (ein Block, eine Liste, ein Umbenennen). Der Inhalt von Dateien wird nie gespeichert oder geloggt.
FILE_VERBS = ("list", "stat", "read", "create", "write", "rename", "delete", "rmdir", "mkdir")
FILE_STATUS = {"not_found": 404, "denied": 403, "exists": 409, "not_empty": 409, "not_a_directory": 400, "is_directory": 400, "invalid_path": 400, "invalid_request": 400, "invalid_name": 400,
               "bad_offset": 409, "type_mismatch": 409, "protected_path": 403, "root_forbidden": 400, "not_a_file": 400, "cross_device": 409, "is_link": 409, "not_permitted": 403}
FILE_FIELDS = {"list": ("path", "limit"), "stat": ("path", "follow"), "read": ("path", "offset", "length"), "create": ("path",), "write": ("path", "offset", "data"),
               "rename": ("from", "to", "overwrite"), "delete": ("path",), "rmdir": ("path",), "mkdir": ("path",)}


def files_root(verb, body):
    """Eine Dateianfrage an den root-Helfer. (Status, Objekt). Der Agent prueft das Wort und die Form, der Helfer alles noch einmal."""
    if verb not in FILE_VERBS or not isinstance(body, dict):
        return err(400, "invalid_request")
    if "files-root" not in permissions():
        return 403, {"error": "not_permitted", "need": "files-root"}
    request = {"verb": "files." + verb}
    for key in FILE_FIELDS[verb]:
        if key in body:
            request[key] = body[key]
    r = helper_call(request, None, 120)
    if r is None:
        return err(503, "helper_unavailable")
    if r.get("ok"):
        return 200, {k: v for k, v in r.items() if k != "code"}
    code = r.get("code") if isinstance(r.get("code"), str) and re.match(r"^[a-z_]{1,32}\Z", r.get("code")) else "failed"
    return FILE_STATUS.get(code, 500 if code in ("helper_closed", "timeout", "failed") else 400), {"error": code}



def host_api(method, path, body, emit=None):
    """Die Anfragen an einen Host-Agenten (docs/HOST-AGENT.md). Gibt (Status, Objekt) zurueck. Nur feste Pfade; ein Desktop-Pfad ist hier 404.
    emit(text, done, total) meldet den Fortschritt einer langen Aktion an den Server."""
    path = path.split("?")[0]
    try:
        if method == "GET":
            if path == "/v1/health":
                return 200, {"ok": True, "api": API_VERSION, "platform": HOST_PLATFORM, "mode": "host", "caps": host_caps(), "allow": sorted(permissions())}
            if path == "/v1/host/jobs":
                return 200, public_jobs()
            if path == "/v1/host/units":
                if "services" not in permissions():
                    return 403, {"error": "not_permitted", "need": "services"}
                return collect_units()
            hit = HOST_READS.get(path)
            if hit:
                if hit[0] not in host_caps():
                    return 404, {"error": "unsupported"}
                return hit[1]()
            return 404, {"error": "unbekannt"}
        if method == "POST" and path.startswith("/v1/host/files/"):
            return files_root(path[len("/v1/host/files/"):], body)
        if method == "POST" and path in WRITE_ROUTES:
            need, action = WRITE_ROUTES[path]
            if need not in permissions():
                return 403, {"error": "not_permitted", "need": need}
            return perform(action, body, emit)
        return 404, {"error": "unbekannt"}
    except Exception as e:  # noqa: BLE001
        log.error("Host-Anfrage %s fehlgeschlagen: %s", path, e)
        return 500, {"error": "failed", "detail": clean(e, 120)}


def host_hello():
    """Was der Host-Agent im hello zusaetzlich meldet: die Plattform (linux-host), die Faehigkeiten ("host" und was der Rechner kann) und lokal erlaubte Schreibrechte."""
    return {"platform": HOST_PLATFORM, "caps": ["host"] + host_caps(), "allow": sorted(permissions())}


def desktop_hello():
    """Was der Desktop-Agent im hello zusaetzlich meldet (seit 0.7.0): "desktop" immer, "host" samt den Faehigkeiten des Rechners, wenn er die Host-Anfragen beantworten kann
    (Linux), und die lokal erlaubten Schreibrechte aus /etc/julos-agent/permissions. Die Plattform bleibt linux-x11. Der Server richtet sich nach diesen Woertern, nicht nach dem Plattformnamen."""
    caps = host_caps()
    return {"caps": ["desktop"] + (["host"] + caps if caps else []), "allow": sorted(permissions())}


if __name__ == "__main__":
    ARGS = parse_args()
else:
    ARGS = argparse.Namespace(command="run", server=None, code=None, allow_http=False, no_socket=False, forget=False, yes=False, keep_config=False,
                          host=False, desktop=False, allow_updates=False, allow_reboot=False, docker_socket=None)

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


def agent_api(method, path, body, emit=None):
    """Die Anfragen an den Desktop-Agenten ueber den Link (seit 0.7.0): /v1/host/* geht an den Host-Teil (gleiche Pruefungen, gleiche lokale Erlaubnisdatei wie im Host-Modus),
    alles andere an die Desktop-API. Der Host-Teil laeuft im Arbeitsthread des Links, nie im GTK-Hauptthread. Der lokale Unix-Socket kennt /v1/host/* nicht (nur die Desktop-API)."""
    if path.split("?")[0].startswith("/v1/host/"):
        return host_api(method, path, body, emit)
    return api(method, path, body)


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
DEAD_S = 10                  # so lange darf der Server schweigen: der Agent pingt alle HEARTBEAT_S, die Antwort (Pong) kommt binnen Sekundenbruchteilen; ohne sie ist die Leitung tot
HEARTBEAT_S = 5              # Ping und "hb" (der Server haelt eine Verbindung ohne Meldung des Agenten nach drei Takten fuer tot, siehe hello: hb)


def enable_keepalive(sock):
    """TCP-Keepalive, wo das System es kann: das Betriebssystem erkennt eine tote Leitung dann selbst (nach rund 25 s), auch wenn der Agent gerade nichts liest."""
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
        for name, value in (("TCP_KEEPIDLE", 10), ("TCP_KEEPINTVL", 5), ("TCP_KEEPCNT", 3)):
            opt = getattr(socket, name, None)
            if opt is not None:
                sock.setsockopt(socket.IPPROTO_TCP, opt, value)
    except OSError:
        pass                                   # nicht jedes System erlaubt es: dann bleibt es bei Ping und Frist


def reconnect_delay(previous, lived, uniform=random.uniform):
    """Pause vor dem naechsten Verbindungsversuch: (neuer Grundwert, Schlaf in Sekunden). Nach einer langen Verbindung (ein Abriss des Netzes) kurz und zufaellig gestreut,
    damit nicht alle Agenten, die zugleich getrennt wurden, zugleich anklopfen; sonst wachsend bis 60 s (Server weg), ebenfalls gestreut."""
    if lived > 30:
        return 2, uniform(0.3, 2.5)
    base = min(previous * 2, 60)
    return base, base * uniform(0.7, 1.3)


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
        enable_keepalive(raw)
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
            sock.settimeout(DEAD_S)
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

    def __init__(self, cfg, handler=None, hello=None, restart=None, transfers=True, progress=None):
        """handler: beantwortet "req" (Standard: die Desktop-API api, im Host-Modus host_api); hello: was zusaetzlich im hello steht (Plattform, Faehigkeiten);
        restart: startet nach einer Aktualisierung neu (Standard: ueber GTK); transfers: Foto, Datei und Zwischenablage (nur Desktop);
        progress: der Handler nimmt als vierten Wert eine Funktion fuer Fortschrittsmeldungen (Standard: ja fuer host_api)."""
        self.cfg = cfg
        self.handler = handler
        self.progress_ok = progress if progress is not None else handler is host_api
        self.hello_extra = hello
        self.restart = restart
        self.transfers = transfers
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
            delay, nap_s = reconnect_delay(delay, time.time() - started)       # nach langer Verbindung gleich wieder (gestreut), sonst langsamer
            time.sleep(nap_s)

    def session(self):
        ws = WsClient.connect(self.cfg["server"], {"Authorization": "Bearer " + self.cfg["token"]}, bool(self.cfg.get("allow_http")))
        self.ws = ws
        try:
            extra = self.hello_extra() if callable(self.hello_extra) else (self.hello_extra or {})
            ws.send_text(json.dumps({"t": "hello", "agent": AGENT_VERSION, "platform": PLATFORM, "api": API_VERSION, "hb": HEARTBEAT_S, **host_info(), **extra}))
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
                ws.send_frame(0x9)             # WebSocket-Ping: der Server antwortet gleich mit Pong, das hebt die Frist DEAD_S an (ohne Antwort endet der Empfang mit "Server antwortet nicht")
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

    def progress(self, id, text, done=None, total=None, lines=None):
        """Eine Fortschrittsmeldung zu einer laufenden Anfrage (lange Aktionen); geht die Verbindung gerade nicht, ist sie verloren, die Aktion laeuft trotzdem weiter.
        lines: die Ausgabezeilen seit der letzten Meldung (fuer die Live-Ansicht), hoechstens 20 zu je 200 Zeichen."""
        msg = {"t": "progress", "id": id, "text": str(text)[:160]}
        if lines:
            msg["lines"] = [str(l)[:200] for l in lines[-20:]]
        if done is not None:
            msg["done"], msg["total"] = int(done), int(total or 0)
        try:
            self.send(msg)
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
                if self.progress_ok:
                    self.background(id, lambda: self.reply(id, *self.handler(method, path, body, lambda text, done=None, total=None, lines=None: self.progress(id, text, done, total, lines))))
                else:
                    self.background(id, lambda: self.reply(id, *(self.handler or api)(method, path, body)))
            else:
                self.reply(id, 400, {"error": "ungueltig"})
        elif t in ("put", "pull") and isinstance(id, int) and not self.transfers:
            self.reply(id, 404, {"error": "unsupported"})            # ein Host ohne Desktop hat keine Zwischenablage
        elif t == "put" and isinstance(id, int):
            self.start_put(id, m)
        elif t == "end" and isinstance(id, int) and self.transfers:
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
        if self.restart:
            self.restart()
        else:
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


def run_host(cfg):
    """Der Host-Modus: ohne Anzeige, ohne GTK, ohne Unix-Socket; die Verbindung zum Server laeuft im Hauptthread bis zum Beenden."""
    signal.signal(signal.SIGTERM, cleanup)
    signal.signal(signal.SIGINT, cleanup)
    write_private(PID_FILE, str(os.getpid()))
    write_status("verbindet")
    log.info("julos-agent %s laeuft im Host-Modus (API v%d), Faehigkeiten: %s, Server %s", AGENT_VERSION, API_VERSION, ", ".join(host_caps()) or "keine", cfg["server"])
    allowed = permissions()
    if allowed:
        log.info("Lokal erlaubte Aktionen (nur auf bestaetigten Auftrag des Servers): %s", ", ".join(sorted(allowed)))
    Link(cfg, handler=host_api, hello=host_hello, restart=restart_agent, transfers=False).run()


if __name__ == "__main__":
    if ARGS.forget:
        _remove(CONFIG)
        print("Einrichtung geloescht.", flush=True)
        sys.exit(0)
    if ARGS.code or ARGS.server:
        if not (ARGS.code and ARGS.server):
            sys.exit("--server und --code gehoeren zusammen")
        enroll(ARGS.server, ARGS.code, ARGS.allow_http, HOST_PLATFORM if choose_mode(ARGS) == "host" else None)
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
    RUN_MODE = choose_mode(ARGS)
    _problems, _notes = missing_requirements(RUN_MODE)
    if _problems:
        sys.stderr.write("julos-agent kann nicht starten:\n" + "".join("  - %s\n" % t for t in _problems))
        sys.exit(3)
    setup_logging(console=RUN_MODE == "host")
    for _t in _notes:
        log.warning(_t)
    _other = running_pid()
    if _other and _other != os.getpid():
        log.error("Ein Agent laeuft schon (PID %d): dieser Start wird abgebrochen.", _other)
        sys.stderr.write("Ein Agent laeuft schon (PID %d). Beenden mit: julos-agent.py stop\n" % _other)
        sys.exit(0)
    if RUN_MODE == "host":
        _cfg = load_config()
        if not _cfg:
            log.error("Host-Modus: noch nicht eingerichtet (kein Token).")
            sys.stderr.write("julos-agent (Host-Modus) ist noch nicht eingerichtet. In der App beim Host \"Agent per SSH installieren\" waehlen, oder:\n  julos-agent.py --host --server https://SERVER --code CODE\n")
            sys.exit(2)
        run_host(_cfg)
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
        threading.Thread(target=Link(cfg, handler=agent_api, hello=desktop_hello, progress=True).run, daemon=True).start()
        GLib.timeout_add_seconds(3600, lambda: (sweep_leftovers(), True)[1])
    else:
        write_status("kein Server eingerichtet")
    GLib.timeout_add_seconds(15, idle_check)
    log.info("julos-agent %s laeuft (API v%d)%s%s", AGENT_VERSION, API_VERSION, ", Socket " + SOCK if (not cfg or not ARGS.no_socket) else "", ", Server " + cfg["server"] if cfg else "")
    if cfg:
        log.info("Host-Faehigkeiten des Desktop-Agenten: %s", ", ".join(host_caps()) or "keine")
        if permissions():
            log.info("Lokal erlaubte Aktionen (nur auf bestaetigten Auftrag des Servers): %s", ", ".join(sorted(permissions())))
    Gtk.main()
