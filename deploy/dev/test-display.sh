#!/usr/bin/env bash
# Display-Tests (RDP-Client und H.264) gegen den xrdp-Test-Desktop der Test-Instanz, in einem SDK-Container mit libopenh264.
# Voraussetzung: deploy/dev/up.sh lief (Netz julos-dev_default, Container rdp-target). Weitere Argumente gehen an "dotnet test".
# Neben anderen Sitzungen: JULOS_DEV_PROJECT=<compose-Projekt> nimmt das Netz dieses Projekts (Standard julos-dev), oder JULOS_DEV_NETWORK=<netz>.
set -euo pipefail
cd "$(dirname "$0")/../.."
net="${JULOS_DEV_NETWORK:-${JULOS_DEV_PROJECT:-julos-dev}_default}"
docker build -q -t julos-display-test deploy/dev/display-test >/dev/null
# Fuer den Tontest: sobald die Sitzung steht, einen Ton im Desktop spielen (mehrfach, der Test fragt erst nach dem Verbinden)
target=$(docker ps --filter network="$net" --filter name=rdp-target --format '{{.Names}}' | head -1)
tone() { for _ in $(seq 12); do sleep 4; SECONDS_LONG=3 docker exec -i -u ubuntu -e SECONDS_LONG=3 "$target" bash -s < deploy/dev/play-tone.sh >/dev/null 2>&1 || true; done; }
tone & tone_pid=$!
# Fuer den Mikrofontest: im Desktop aufnehmen (die Rohdaten kommen in eine Datei, die der Test liest)
probe=$(mktemp -d)
# Auf einem frisch gestarteten Ziel gibt es die Sitzung (und PipeWire) erst nach einigen Sekunden; bis dahin bricht record-mic.sh ab, also erneut versuchen
record() { sleep 5; for _ in $(seq 10); do docker exec -i -u ubuntu -e SECONDS_LONG=40 "$target" bash -s < deploy/dev/record-mic.sh > "$probe/rec.raw" 2>/dev/null || true; [ -s "$probe/rec.raw" ] && break; sleep 3; done; }
record & record_pid=$!
trap 'kill $tone_pid $record_pid 2>/dev/null || true; rm -rf "$probe"' EXIT
docker run --rm --network "$net" --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 \
  -e JULOS_TEST_RDP_HOST=rdp-target -e JULOS_TEST_RDP_GFX_HOST -e JULOS_TEST_RDP_GFX_H264 -e JULOS_TEST_RDP_TONE=1 -e JULOS_TEST_RDP_REC=/probe/rec.raw -v $probe:/probe:ro -v "$PWD":/src -w /src julos-display-test \
  dotnet test --project tests/JulOS.Display.Tests -c Release "$@"
