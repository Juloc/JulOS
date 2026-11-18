#!/usr/bin/env bash
# Schaltet das Gateway auf "nur hinter dem HTTPS-Proxy erreichbar":
# erzeugt ein geheimes Header-Token (PROXY_SECRET in .env) und startet das Gateway neu.
# Danach muss dein Caddy den Header X-Proxy-Secret mitsenden (siehe caddy/Caddyfile.example).
# Rueckgaengig: PROXY_SECRET in .env leeren (oder Zeile loeschen) und "docker compose up -d".
set -euo pipefail
cd "$(dirname "$0")/.."
if grep -q '^PROXY_SECRET=.\+' .env 2>/dev/null; then
  echo "PROXY_SECRET ist schon gesetzt (.env). Nichts geaendert."
else
  sed -i '/^PROXY_SECRET=/d' .env
  printf 'PROXY_SECRET=%s\n' "$(openssl rand -hex 24)" >> .env
  echo "PROXY_SECRET in .env erzeugt."
fi
docker compose up -d --build
echo
echo "Fertig. Das Gateway antwortet jetzt nur noch mit dem Header X-Proxy-Secret (direkter HTTP-Zugriff im LAN ist gesperrt)."
echo "Den Wert findest du in: $(pwd)/.env  (Zeile PROXY_SECRET=...). Nicht weitergeben, nur in die Umgebung deines Caddy eintragen."
