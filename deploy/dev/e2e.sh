#!/usr/bin/env bash
# Browser-Tests (Playwright in Docker, nichts auf dem Host noetig) gegen einen eigenen, frisch aufgesetzten Stack aus diesem Arbeitsordner:
# Datenbank, Server, SSH-Ziel und RDP-Desktop in einem eigenen Compose-Projekt "julos-e2e" ohne veroeffentlichte Ports.
# Neben einer anderen Sitzung mit eigenem Stack: E2E_PROJECT=<name> (Standard julos-e2e).
# Aufruf: deploy/dev/e2e.sh [Playwright-Optionen, z. B. -g Terminal]    Stack danach stehen lassen: E2E_KEEP=1    Aufraeumen: deploy/dev/e2e.sh down
# Fehlerbilder und Traces landen ausserhalb des Repos in $JULOS_E2E_STATE (Standard ~/.cache/julos-e2e/out).
set -euo pipefail
cd "$(dirname "$0")"
state="${JULOS_E2E_STATE:-${XDG_CACHE_HOME:-$HOME/.cache}/julos-e2e}"
export E2E_OUT="$state/out" E2E_UID="$(id -u)" E2E_GID="$(id -g)"
mkdir -p "$state"
# eigene Wegwerf-Zugangsdaten, nur lokal und nicht im Repo; die Datei bleibt, weil das Datenbank-Passwort beim ersten Start in das Volume geht
if [ ! -f "$state/env" ]; then
  ( umask 077; printf 'DEV_DB_PASSWORD=%s\nDEV_TARGET_PASSWORD=%s\nDEV_SECRET_KEY=%s\n' "$(openssl rand -hex 16)" "$(openssl rand -hex 12)" "$(openssl rand -base64 32)" > "$state/env" )
fi
dc() { docker compose -p "${E2E_PROJECT:-julos-e2e}" -f compose.yaml -f e2e/compose.override.yaml --env-file "$state/env" "$@"; }
# Aufbau und Neustarts laufen leise; bei einem Fehler steht die Ausgabe in $state/stack.log
quiet() { "$@" >>"$state/stack.log" 2>&1 || { echo "Fehlgeschlagen: $* (Ausgabe: $state/stack.log)" >&2; exit 1; }; }
teardown() { [ "${E2E_KEEP:-}" = 1 ] || dc --profile e2e down -v --remove-orphans >/dev/null 2>&1 || true; }

if [ "${1:-}" = down ]; then dc --profile e2e down -v --remove-orphans; exit; fi

start=$SECONDS
echo "Last: $(cut -d' ' -f1-3 /proc/loadavg) (die Entwicklungs-VM hat 6 Kerne; unter etwa 6 bleiben)"
rm -rf "$E2E_OUT"; mkdir -p "$E2E_OUT"; : > "$state/stack.log"
trap teardown EXIT
quiet dc up -d --build --quiet-pull
quiet dc --profile e2e build e2e
# frische Datenbank bei jedem Lauf (die Ersteinrichtung ist Teil der Tests); die Ziele bleiben stehen
quiet dc stop julos
quiet dc exec -T postgres psql -q -U julos -d julos -c 'drop schema public cascade; create schema public;'
# Nanosekunden: mit ganzen Sekunden lag die Zeile des ersten Starts (vor dem Loeschen der Datenbank) manchmal noch im Fenster, und der Test nahm den falschen Setup-Code
since=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)
quiet dc start julos
code=""
for _ in $(seq 1 60); do
  code=$(dc logs --since "$since" julos 2>&1 | sed -n 's/.*Setup-Code ein: \([A-Za-z0-9-]*\).*/\1/p' | tail -1)
  [ -n "$code" ] && break; sleep 1
done
[ -n "$code" ] || { echo "Kein Setup-Code im Log: der Server ist nicht hochgekommen. Log: deploy/dev/e2e.sh down; dann docker compose -p julos-e2e logs julos" >&2; exit 2; }
echo "Stack bereit nach $((SECONDS - start)) s, starte die Tests ..."

# Standard: nur der Kern (Abläufe 1 bis 7). Alles: E2E_FULL=1 deploy/dev/e2e.sh (vor grossen Aenderungen an Bild, Ton, Zwischenablage, Programmen)
if [ "${E2E_FULL:-}" != 1 ] && [ $# -eq 0 ]; then set -- --grep-invert @full; fi
rc=0
dc run --rm --no-deps -e E2E_SETUP_CODE="$code" e2e "$@" 2>&1 | grep -v '^ Container ' || rc=${PIPESTATUS[0]}
[ "$rc" = 0 ] && echo "Browser-Tests bestanden (gesamt $((SECONDS - start)) s)." || echo "Browser-Tests fehlgeschlagen (Bilder und Traces: $E2E_OUT/results)." >&2
exit "$rc"
