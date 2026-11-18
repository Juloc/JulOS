#!/usr/bin/env bash
# Konten auf der Konsole verwalten (Notweg bei verlorenem Passwort oder Authenticator):
#   tools/julos-user.sh list | add NAME | passwd NAME | totp NAME | logout NAME | remove NAME
# Passwoerter tippst du im Terminal, sie werden nie als Argument uebergeben.
set -euo pipefail
cd "$(dirname "$0")/.."
exec docker compose exec julos dotnet JulOS.dll user "$@"
