#!/usr/bin/env bash
# Nimmt im Desktop der Test-Instanz (Container rdp-target) das Mikrofon auf und schreibt die Rohdaten (16 Bit, 16 kHz, Mono) auf die
# Standardausgabe; so erfuehrt der ferne Rechner, dass jemand aufnimmt (xrdp oeffnet dann den Mikrofon-Kanal). Dauer: SECONDS_LONG
# Sekunden (Standard 20). Wie play-tone.sh uebernimmt es die Umgebung der Sitzung von "ubuntu".
# Aufruf: docker exec -i -u ubuntu <rdp-target> bash -s < deploy/dev/record-mic.sh > aufnahme.raw
set -euo pipefail
pid=$(pgrep -u ubuntu -n xfce4-session || true)
[ -n "$pid" ] || { echo "keine Sitzung von ubuntu" >&2; exit 1; }
while IFS= read -r -d '' kv; do
  case "$kv" in DBUS_SESSION_BUS_ADDRESS=*|PULSE_SERVER=*|XRDP_*|DISPLAY=*) export "$kv";; esac
done < "/proc/$pid/environ"
export XDG_RUNTIME_DIR=/run/user/$(id -u)
exec timeout "${SECONDS_LONG:-20}" parecord --raw --format=s16le --rate=16000 --channels=1 --latency-msec=50
