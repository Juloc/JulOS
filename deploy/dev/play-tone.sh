#!/usr/bin/env bash
# Spielt im Desktop der Test-Instanz (Container rdp-target) einen Ton, damit der Tonweg pruefbar ist: 440 Hz Sinus, SECONDS Sekunden
# (Standard 3), wiederholt bis ECHO_COUNT mal (Standard 1). Die Sitzung des Benutzers "ubuntu" muss laufen (Anmeldung per RDP);
# deren Umgebung (Pulse-Socket, Session-Bus) wird vom Sitzungsprozess uebernommen.
# Aufruf: docker exec -i -u ubuntu <rdp-target> bash -s < deploy/dev/play-tone.sh
set -euo pipefail
pid=$(pgrep -u ubuntu -n xfce4-session || true)
[ -n "$pid" ] || { echo "keine Sitzung von ubuntu" >&2; exit 1; }
while IFS= read -r -d '' kv; do
  case "$kv" in XDG_RUNTIME_DIR=*|DBUS_SESSION_BUS_ADDRESS=*|PULSE_SERVER=*|XRDP_*|DISPLAY=*) export "$kv";; esac
done < "/proc/$pid/environ"
export XDG_RUNTIME_DIR=/run/user/$(id -u)
wav=$(mktemp --suffix=.wav)
python3 - "$wav" "${SECONDS_LONG:-3}" <<'PY'
import math, struct, sys, wave
path, seconds = sys.argv[1], float(sys.argv[2])
rate = 44100
with wave.open(path, 'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(rate)
    w.writeframes(b''.join(struct.pack('<hh', int(12000 * math.sin(2 * math.pi * 440 * i / rate)), int(12000 * math.sin(2 * math.pi * 440 * i / rate))) for i in range(int(rate * seconds))))
PY
for _ in $(seq "${ECHO_COUNT:-1}"); do paplay "$wav"; done
rm -f "$wav"
