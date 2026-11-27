#!/usr/bin/env bash
# Fuehrt die Tests gegen die Test-Instanz aus (vorher deploy/dev/up.sh): PostgreSQL und die Wegwerf-Ziele.
set -euo pipefail
cd "$(dirname "$0")"
set -a; . ./.env; set +a
cd ../..
JULOS_TEST_DB="Host=127.0.0.1;Port=45432;Database=julos;Username=julos;Password=${DEV_DB_PASSWORD}" \
JULOS_TEST_TARGET_PASSWORD="${DEV_TARGET_PASSWORD}" \
JULOS_TEST_RDP_HOST="rdp-target" \
JULOS_TEST_SSH="127.0.0.1:${DEV_SSH_PORT:-42222}" \
  dotnet test --project tests/JulOS.Backend.Tests/JulOS.Backend.Tests.csproj "$@"
# Groessen-Logik des Handy-Clients (reines JavaScript, braucht nur node)
if command -v node >/dev/null; then node --test "tests/frontend/*.test.mjs"; else echo "node fehlt: tests/frontend uebersprungen"; fi
# Das Tor des Codex-Containers (reines Python, tools/codex-gate)
if command -v python3 >/dev/null; then (cd tools/codex-gate && python3 -W ignore -m unittest -q test_codex_gate 2>&1 | grep -v '^gate:'); else echo "python3 fehlt: tools/codex-gate uebersprungen"; fi
