#!/usr/bin/env bash
# Fuehrt die Tests gegen die PostgreSQL-Datenbank der Test-Instanz aus (vorher deploy/dev/up.sh).
set -euo pipefail
cd "$(dirname "$0")"
set -a; . ./.env; set +a
cd ../..
JULOS_TEST_DB="Host=127.0.0.1;Port=45432;Database=julos;Username=julos;Password=${DEV_DB_PASSWORD}" dotnet test --project tests/JulOS.Backend.Tests/JulOS.Backend.Tests.csproj "$@"
