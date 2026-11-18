#!/bin/sh
# Haelt xrdp-chansrv (Kanaldienst fuer Zwischenablage, Ton, Groessenwechsel) der laufenden xrdp-Sitzung am Leben.
# Ohne ihn wartet xrdp bei jeder Verbindung 10 s (langes Schwarzbild) und der Bildschirm passt sich nicht mehr an.
# Nach 10 s ohne chansrv wird er mit der Umgebung der Sitzung neu gestartet. Jeder Ausfall steht mit Zeit, offenen
# RDP-Verbindungen und den letzten chansrv-Zeilen im Journal (journalctl -t julos-chansrv-watch).
uid=$(id -u)
log() { logger -t julos-chansrv-watch -- "$*" 2>/dev/null || echo "$*" >&2; }
missing=0
while sleep 5; do
  sess=$(pgrep -u "$uid" -x xfce4-session | head -1)
  [ -n "$sess" ] || { missing=0; continue; }                          # keine Sitzung: nichts zu tun
  pgrep -u "$uid" -x xrdp-chansrv >/dev/null && { missing=0; continue; }
  missing=$((missing + 1))
  if [ "$missing" -eq 1 ]; then
    log "chansrv fehlt seit $(date '+%T'); RDP-Verbindungen: $(ss -tn state established '( sport = :3389 )' 2>/dev/null | tail -n +2 | wc -l)"
    journalctl --since '-3 min' --no-pager 2>/dev/null | grep 'xrdp-chansrv\[' | tail -4 | while read -r l; do log "vorher: $l"; done
  fi
  [ "$missing" -ge 2 ] || continue                                      # 10 s Gnadenfrist: xrdp startet ihn bei einer neuen Verbindung teils selbst
  env=$(tr '\0' '\n' < "/proc/$sess/environ" 2>/dev/null)
  get() { printf '%s\n' "$env" | sed -n "s/^$1=//p" | head -1; }
  [ -n "$(get XRDP_SESSION)" ] || { missing=0; continue; }              # keine xrdp-Sitzung (zum Beispiel lokal angemeldet)
  DISPLAY=$(get DISPLAY) XRDP_SOCKET_PATH=$(get XRDP_SOCKET_PATH) XRDP_SESSION=$(get XRDP_SESSION) \
  XRDP_PULSE_SOURCE_SOCKET=$(get XRDP_PULSE_SOURCE_SOCKET) XRDP_PULSE_SINK_SOCKET=$(get XRDP_PULSE_SINK_SOCKET) \
  XDG_RUNTIME_DIR=$(get XDG_RUNTIME_DIR) DBUS_SESSION_BUS_ADDRESS=$(get DBUS_SESSION_BUS_ADDRESS) \
  XAUTHORITY="$HOME/.Xauthority" PULSE_SCRIPT=/etc/xrdp/pulse/default.pa \
    setsid /usr/sbin/xrdp-chansrv >/dev/null 2>&1 < /dev/null &
  sleep 2
  pgrep -u "$uid" -x xrdp-chansrv >/dev/null && log "chansrv neu gestartet" || log "chansrv-Start fehlgeschlagen"
  missing=0
done
