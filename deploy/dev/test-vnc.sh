#!/usr/bin/env bash
# VNC-Tests gegen echte TigerVNC-Wegwerfziele: zwei eigene Container (Passwort, und VeNCrypt mit Zertifikat) in einem eigenen Netz, die Tests laufen
# im SDK-Container mit libopenh264 im selben Netz; am Ende werden Container und Netz wieder entfernt. Mit Argumenten geht es an "dotnet test"
# (Standard: nur die VNC-Klassen). Nichts davon beruehrt die Test-Instanz (deploy/dev/up.sh) oder ihre Container.
set -euo pipefail
cd "$(dirname "$0")/../.."
suffix="$$"
net="julos-vnc-test-$suffix"
plain="julos-vnc-plain-$suffix"
tls="julos-vnc-tls-$suffix"
cleanup() { docker rm -f "$plain" "$tls" >/dev/null 2>&1 || true; docker network rm "$net" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker build -q -t julos-vnc-target -f deploy/dev/vnc-target/Dockerfile . >/dev/null
docker build -q -t julos-display-test deploy/dev/display-test >/dev/null
docker network create "$net" >/dev/null
password=vnctest1
clip='julos-clip aou € 123'
docker run -d --rm --name "$plain" --network "$net" --network-alias vnc-target -e VNC_PASSWORD="$password" -e VNC_CLIPBOARD_TEXT="$clip" julos-vnc-target >/dev/null
docker run -d --rm --name "$tls" --network "$net" --network-alias vnc-target-tls -e VNC_PASSWORD="$password" -e VNC_SECURITY=X509Vnc julos-vnc-target >/dev/null
# Warten, bis beide antworten (RFB-Kennung)
for target in "$plain" "$tls"; do
  for _ in $(seq 60); do
    docker exec "$target" sh -c 'bash -c "exec 3<>/dev/tcp/127.0.0.1/5900 && head -c 4 <&3" 2>/dev/null | grep -q RFB' && break
    sleep 0.5
  done
done
args=("$@")
[ ${#args[@]} -eq 0 ] && args=(--filter-namespace "JulOS.Integrations.Display.Tests.Vnc")
docker run --rm --network "$net" --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 \
  -e JULOS_TEST_VNC_HOST=vnc-target -e JULOS_TEST_VNC_TLS_HOST=vnc-target-tls -e JULOS_TEST_VNC_PASSWORD="$password" -e JULOS_TEST_VNC_CLIP="$clip" \
  -v "$PWD":/src -w /src julos-display-test \
  dotnet test --project tests/JulOS.Display.Tests -c Release "${args[@]}"
