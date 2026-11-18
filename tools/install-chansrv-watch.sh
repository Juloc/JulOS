#!/usr/bin/env bash
# Installiert den chansrv-Waechter in der grafischen xrdp-Sitzung dieses Linux-Rechners: startet beim Anmelden von selbst
# (~/.config/autostart) und laeuft sofort. Keine Root-Rechte noetig. Entfernen: ~/.config/autostart/julos-chansrv-watch.desktop loeschen
# und den Prozess beenden (pkill -f julos-chansrv-watch.sh).
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/.config/autostart"
cat > "$HOME/.config/autostart/julos-chansrv-watch.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=JulOS chansrv watch
Exec=/bin/sh $dir/julos-chansrv-watch.sh
X-GNOME-Autostart-enabled=true
NoDisplay=true
DESKTOP
for p in $(pgrep -u "$(id -u)" -f '^/bin/sh .*julos-chansrv-watch\.sh$' || true); do [ "$p" = "$$" ] || kill "$p" 2>/dev/null || true; done
sleep 1
(setsid nohup /bin/sh "$dir/julos-chansrv-watch.sh" >/dev/null 2>&1 </dev/null &)
sleep 1
pgrep -u "$(id -u)" -f '^/bin/sh .*julos-chansrv-watch\.sh$' >/dev/null && echo "Waechter laeuft. Journal: journalctl -t julos-chansrv-watch" || { echo "Waechter nicht gestartet" >&2; exit 1; }
