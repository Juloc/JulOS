#!/bin/sh
# Startet Xvnc und einen kleinen Desktop. Umgebung: VNC_PASSWORD (Standard "vnctest1", nur 8 Zeichen zaehlen), VNC_SECURITY
# (VncAuth, None oder X509Vnc/X509Plain; Standard VncAuth), VNC_GEOMETRY (Standard 1024x768).
set -eu
mkdir -p "$HOME/.vnc"
printf '%s\n' "${VNC_PASSWORD:-vnctest1}" | vncpasswd -f > "$HOME/.vnc/passwd"
chmod 600 "$HOME/.vnc/passwd"
security="${VNC_SECURITY:-VncAuth}"
extra=""
case "$security" in
  X509*)
    openssl req -x509 -newkey rsa:2048 -nodes -keyout "$HOME/.vnc/key.pem" -out "$HOME/.vnc/cert.pem" -days 2 -subj "/CN=vnc-target" >/dev/null 2>&1
    extra="-X509Cert $HOME/.vnc/cert.pem -X509Key $HOME/.vnc/key.pem"
    ;;
esac
# Reste eines frueheren Starts (docker restart behaelt das Dateisystem): sonst weigert sich Xvnc, "Server is already active"
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1 2>/dev/null || true
# shellcheck disable=SC2086
Xvnc :1 -geometry "${VNC_GEOMETRY:-1024x768}" -depth 24 -rfbport 5900 -rfbauth "$HOME/.vnc/passwd" -SecurityTypes "$security" -AlwaysShared $extra >"$HOME/xvnc.log" 2>&1 &
export DISPLAY=:1
for _ in $(seq 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.2; done
xsetroot -solid '#204060'
vncconfig -nowin >/dev/null 2>&1 &
openbox >/dev/null 2>&1 &
xterm -geometry 80x24+40+40 -bg black -fg white >/dev/null 2>&1 &

# Fuehler fuer die Tests: was im Desktop ankommt (Tasten und Mausknoepfe in einem Fenster, die Zwischenablage) steht als Text auf einem kleinen
# Webserver (Port 8080: /input.log, /clip.txt), damit ein Test von aussen pruefen kann, ob Eingaben wirklich dort ankamen.
mkdir -p /tmp/probe
: > /tmp/probe/input.log
xev -geometry 300x300+600+400 -name probe 2>/dev/null | grep --line-buffered -E 'keycode|button [0-9]+,' >> /tmp/probe/input.log &
( while true; do xclip -o -selection clipboard > /tmp/probe/clip.tmp 2>/dev/null && mv /tmp/probe/clip.tmp /tmp/probe/clip.txt; sleep 0.3; done ) &
if [ -n "${VNC_CLIPBOARD_TEXT:-}" ]; then
  # alle zwei Sekunden neu setzen, solange die Zwischenablage noch leer oder dieser Text ist (ein Viewer, der etwas anderes ablegt, beendet es)
  ( while true; do
      sleep 2
      current=$(xclip -o -selection clipboard 2>/dev/null || true)
      if [ -z "$current" ] || [ "$current" = "$VNC_CLIPBOARD_TEXT" ]; then printf '%s' "$VNC_CLIPBOARD_TEXT" | xclip -selection clipboard -i; else break; fi
    done ) &
fi
busybox httpd -f -p 8080 -h /tmp/probe &
wait
