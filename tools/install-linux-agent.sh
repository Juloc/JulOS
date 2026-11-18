#!/usr/bin/env bash
# Installiert die Hilfsprogramme in der grafischen Sitzung dieses Linux-Rechners (X11, XFCE getestet):
#  - julos-agent.py : Fensterleiste, Programmstarter, Programm-Modus (Agent-API v1)
#  - julos-clip.py  : Fotos aus dem Handy in die Zwischenablage legen
# Beide starten beim Anmelden von selbst (~/.config/autostart) und laufen sofort in der aktuellen Sitzung.
# Voraussetzung: python3 mit PyGObject, GTK3 und Wnck:  sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/julos-uploads" "$HOME/.config/autostart"
for name in julos-agent julos-clip; do
  cat > "$HOME/.config/autostart/$name.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=JulOS ${name#julos-}
Exec=/usr/bin/python3 $dir/$name.py
X-GNOME-Autostart-enabled=true
NoDisplay=true
EOF
  # laufende Instanz sauber beenden (nur Python-Prozesse mit diesem Skript)
  for p in $(ps -eo pid,comm,args | awk -v n="$name.py" '$2=="python3" && index($0,n){print $1}'); do kill "$p" 2>/dev/null || true; done
done
sleep 1
for name in julos-agent julos-clip; do
  (setsid nohup python3 "$dir/$name.py" >"/tmp/$name.log" 2>&1 </dev/null &)
done
sleep 2
echo "Gestartet. Logs: /tmp/julos-agent.log und /tmp/julos-clip.log"
