#!/usr/bin/env bash
# Startet die Test-Instanz (Datenbank, Server, Test-SSH, Test-Desktop). Erzeugt beim ersten Mal zufaellige Wegwerf-Zugangsdaten in deploy/dev/.env.
set -euo pipefail
cd "$(dirname "$0")"
if [ ! -f .env ]; then
  umask 077
  printf 'DEV_DB_PASSWORD=%s\nDEV_TARGET_PASSWORD=%s\n' "$(openssl rand -hex 16)" "$(openssl rand -hex 12)" > .env
  echo "Zugangsdaten fuer die Test-Instanz in deploy/dev/.env erzeugt (nur lokal, nicht im Repo)."
fi
grep -q "^DEV_SECRET_KEY=" .env || printf 'DEV_SECRET_KEY=%s\n' "$(openssl rand -base64 32)" >> .env
docker compose up -d --build "$@"
for i in $(seq 1 40); do
  if curl -fsS -m 2 http://127.0.0.1:18080/api/health >/dev/null 2>&1; then echo "Test-Instanz bereit: http://127.0.0.1:18080"; exit 0; fi
  sleep 2
done
echo "Server antwortet nicht. Log: docker compose -f deploy/dev/compose.yaml logs julos" >&2
exit 1
