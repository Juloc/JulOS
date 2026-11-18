#!/usr/bin/env bash
# Display-Tests (RDP-Client und H.264) gegen den xrdp-Test-Desktop der Test-Instanz, in einem SDK-Container mit libopenh264.
# Voraussetzung: deploy/dev/up.sh lief (Netz julos-dev_default, Container rdp-target). Weitere Argumente gehen an "dotnet test".
# Neben anderen Sitzungen: JULOS_DEV_PROJECT=<compose-Projekt> nimmt das Netz dieses Projekts (Standard julos-dev).
set -euo pipefail
cd "$(dirname "$0")/../.."
docker build -q -t julos-display-test deploy/dev/display-test >/dev/null
docker run --rm --network "${JULOS_DEV_PROJECT:-julos-dev}_default" --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 \
  -e JULOS_TEST_RDP_HOST=rdp-target -v "$PWD":/src -w /src julos-display-test \
  dotnet test --project tests/JulOS.Display.Tests -c Release "$@"
