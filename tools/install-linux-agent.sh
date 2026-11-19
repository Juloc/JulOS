#!/usr/bin/env bash
# Installiert die Hilfsprogramme in der grafischen Sitzung dieses Linux-Rechners (X11, XFCE getestet):
#  - julos-agent.py : Fensterleiste, Programmstarter, Programm-Modus, Foto/Datei/Zwischenablage (Agent-API v1)
#  - julos-clip.py  : Fotos und Dateien aus dem Handy in die Zwischenablage legen
# Beide starten beim Anmelden von selbst (~/.config/autostart) und laufen sofort in der aktuellen Sitzung.
#
# Server auf einem ANDEREN Rechner: in der App bei der Verbindung "Agent einrichten" waehlen und den Befehl daraus nehmen:
#     bash install-linux-agent.sh --server https://os.example.org --code ABCDE-FGHIJ
# Der Agent meldet sich dann von hier aus beim Server (nur ausgehend, es lauscht nichts). Ohne --server/--code bleibt es beim
# lokalen Weg (Unix-Socket im Ordner ~/julos-uploads, den der Server auf demselben Rechner einbindet).
#
# Voraussetzung: python3 mit PyGObject, GTK3 und Wnck:  sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
server="" code="" extra=()
while [ $# -gt 0 ]; do
  case "$1" in
    --server) server="${2:-}"; shift 2 ;;
    --code) code="${2:-}"; shift 2 ;;
    --allow-http) extra+=(--allow-http); shift ;;   # nur zum Testen
    --help|-h) sed -n '2,14p' "$0"; exit 0 ;;
    *) echo "Unbekannte Option: $1 (siehe --help)" >&2; exit 2 ;;
  esac
done
if [ -n "$server$code" ]; then
  [ -n "$server" ] && [ -n "$code" ] || { echo "--server und --code gehoeren zusammen" >&2; exit 2; }
  python3 "$dir/julos-agent.py" --server "$server" --code "$code" ${extra[@]+"${extra[@]}"}   # tauscht den Code gegen ein Token (ohne Anzeige)
fi
mkdir -p "$HOME/julos-uploads" "$HOME/.config/autostart"
for name in julos-agent julos-clip; do
  cat > "$HOME/.config/autostart/$name.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=JulOS ${name#julos-}
Exec=/usr/bin/python3 $dir/$name.py
X-GNOME-Autostart-enabled=true
NoDisplay=true
DESKTOP
  # laufende Instanz sauber beenden (nur Python-Prozesse mit diesem Skript)
  for p in $(ps -eo pid,comm,args | awk -v n="$name.py" '$2=="python3" && index($0,n){print $1}'); do kill "$p" 2>/dev/null || true; done
done
sleep 1
for name in julos-agent julos-clip; do
  (setsid nohup python3 "$dir/$name.py" >"/tmp/$name.log" 2>&1 </dev/null &)
done
sleep 2
echo "Gestartet. Logs: /tmp/julos-agent.log und /tmp/julos-clip.log"
