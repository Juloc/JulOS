#!/usr/bin/env bash
# Fuehrt die Tests gegen die Test-Instanz aus (vorher deploy/dev/up.sh): PostgreSQL, guacd und die Wegwerf-Ziele.
set -euo pipefail
cd "$(dirname "$0")"
set -a; . ./.env; set +a
cd ../..
JULOS_TEST_DB="Host=127.0.0.1;Port=45432;Database=julos;Username=julos;Password=${DEV_DB_PASSWORD}" \
JULOS_TEST_GUACD="127.0.0.1:44822" \
JULOS_TEST_TARGET_PASSWORD="${DEV_TARGET_PASSWORD}" \
JULOS_TEST_RDP_HOST="rdp-target" \
  dotnet test --project tests/JulOS.Backend.Tests/JulOS.Backend.Tests.csproj "$@"
