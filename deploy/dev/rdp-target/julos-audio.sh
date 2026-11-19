#!/bin/sh
# Ton im Test-Desktop: ohne systemd startet nichts PipeWire, deshalb tut es diese Sitzung selbst (Autostart beim Anmelden), danach laedt das
# Paket pipewire-module-xrdp die Geraete "xrdp-sink" (Ton heraus) und "xrdp-source" (Mikrofon hinein). Auf einer echten VM macht das systemd.
export XDG_RUNTIME_DIR=/run/user/$(id -u)
export PIPEWIRE_LOG_SYSTEMD=false
pw-cli quit >/dev/null 2>&1 || {
  pipewire >/dev/null 2>&1 &
  sleep 1
}
pgrep -u "$(id -u)" -x wireplumber >/dev/null || wireplumber >/dev/null 2>&1 &
pgrep -u "$(id -u)" -x pipewire-pulse >/dev/null || pipewire-pulse >/dev/null 2>&1 &
sleep 2
exec /usr/libexec/pipewire-module-xrdp/load_pw_modules.sh
