#!/usr/bin/env bash
# Installiert die Hilfsprogramme in der grafischen Sitzung dieses Linux-Rechners (X11, XFCE getestet):
#  - julos-agent.py : Fensterleiste, Programmstarter, Programm-Modus, Foto/Datei/Zwischenablage (Agent-API v1)
#  - julos-clip.py  : Fotos und Dateien aus dem Handy in die Zwischenablage legen
# Beide starten beim Anmelden von selbst und laufen sofort in der aktuellen Sitzung.
#
# Server auf einem ANDEREN Rechner: in der App beim Host "Agent einrichten" waehlen und den Befehl daraus nehmen:
#     bash install-linux-agent.sh --server https://os.example.org --code ABCDE-FGHIJ
# Der Agent meldet sich dann von hier aus beim Server (nur ausgehend, es lauscht nichts). Ohne --server/--code bleibt es beim
# lokalen Weg (Unix-Socket im Ordner ~/julos-uploads, den der Server auf demselben Rechner einbindet).
#
# Weitere Optionen:
#   --systemd    den Agenten als systemd-Benutzerdienst starten (Neustart nach Absturz) statt per Autostart-Eintrag
#   --no-start   nur einrichten, nichts starten
#   --allow-http nur zum Testen: auch ohne TLS verbinden
# Danach auf diesem Rechner:  python3 julos-agent.py status | stop | uninstall   (Log: ~/.local/state/julos/agent.log)
#
# Voraussetzung: python3 (3.8 oder neuer) mit PyGObject, GTK3 und Wnck:  sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
agent="$dir/julos-agent.py"
server="" code="" extra=() systemd=0 start=1
while [ $# -gt 0 ]; do
  case "$1" in
    --server) server="${2:-}"; shift 2 ;;
    --code) code="${2:-}"; shift 2 ;;
    --allow-http) extra+=(--allow-http); shift ;;   # nur zum Testen
    --systemd) systemd=1; shift ;;
    --no-start) start=0; shift ;;
    --help|-h) sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "Unbekannte Option: $1 (siehe --help)" >&2; exit 2 ;;
  esac
done
command -v python3 >/dev/null || { echo "python3 fehlt: sudo apt install python3 python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0" >&2; exit 1; }
# Voraussetzungen klar benennen, bevor etwas eingerichtet wird
python3 "$agent" check || { echo "Bitte das Fehlende installieren und dieses Skript erneut starten." >&2; exit 1; }
if [ -n "$server$code" ]; then
  [ -n "$server" ] && [ -n "$code" ] || { echo "--server und --code gehoeren zusammen" >&2; exit 2; }
  python3 "$agent" --server "$server" --code "$code" ${extra[@]+"${extra[@]}"}   # tauscht den Code gegen ein Token (ohne Anzeige)
fi
config="${XDG_CONFIG_HOME:-$HOME/.config}"
state="${XDG_STATE_HOME:-$HOME/.local/state}/julos"
mkdir -p "$HOME/julos-uploads" "$config/autostart" "$state"
chmod 700 "$state"

# Laufende Instanzen beenden: der Agent ueber seine eigene Pid-Datei/seinen Dienst, aeltere Staende ohne Pid-Datei und der Helfer ueber den
# genauen Pfad dieser Installation (andere Kopien auf dem Rechner bleiben unberuehrt)
python3 "$agent" stop >/dev/null 2>&1 || true
for name in julos-agent julos-clip; do
  for p in $(ps -eo pid,args | awk -v n="python3 $dir/$name.py" 'index($0, n) {print $1}'); do kill "$p" 2>/dev/null || true; done
done

autostart() {   # $1 = Name ohne Endung
  cat > "$config/autostart/$1.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=JulOS ${1#julos-}
Exec=/usr/bin/python3 "$dir/$1.py"
X-GNOME-Autostart-enabled=true
NoDisplay=true
DESKTOP
}
autostart julos-clip
if [ "$systemd" = 1 ]; then
  command -v systemctl >/dev/null || { echo "systemctl fehlt: ohne --systemd installieren (Autostart-Eintrag)" >&2; exit 1; }
  mkdir -p "$config/systemd/user"
  rm -f "$config/autostart/julos-agent.desktop"      # nicht doppelt starten
  escaped="${agent//%/%%}"
  cat > "$config/systemd/user/julos-agent.service" <<UNIT
[Unit]
Description=JulOS Agent (Fensterleiste, Programmstart, Foto/Datei/Zwischenablage)
After=graphical-session.target

[Service]
# Ohne grafische Sitzung (DISPLAY fehlt) beendet sich der Agent mit einer klaren Meldung im Log und wird spaeter erneut versucht
ExecStart=/usr/bin/python3 "$escaped"
Restart=on-failure
RestartSec=30

[Install]
WantedBy=default.target
UNIT
  # die Umgebung der grafischen Sitzung an den Dienst weitergeben (DISPLAY, XAUTHORITY)
  systemctl --user import-environment DISPLAY XAUTHORITY 2>/dev/null || true
  systemctl --user daemon-reload
  [ "$start" = 1 ] && systemctl --user enable --now julos-agent.service || systemctl --user enable julos-agent.service
else
  autostart julos-agent
  if [ -f "$config/systemd/user/julos-agent.service" ]; then
    systemctl --user disable --now julos-agent.service 2>/dev/null || true; rm -f "$config/systemd/user/julos-agent.service"; systemctl --user daemon-reload 2>/dev/null || true
  fi
fi
if [ "$start" = 1 ]; then
  sleep 1
  [ "$systemd" = 1 ] || (setsid nohup python3 "$agent" >/dev/null 2>&1 </dev/null &)      # der Agent schreibt sein Log selbst (mit Rotation)
  (setsid nohup python3 "$dir/julos-clip.py" >"$state/clip.log" 2>&1 </dev/null &)
  sleep 2
  python3 "$agent" status || true
  echo "Gestartet. Log des Agenten: $state/agent.log, des Helfers: $state/clip.log"
else
  echo "Eingerichtet, nichts gestartet. Starten: python3 $agent"
fi
