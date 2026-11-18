#!/usr/bin/env bash
# Durchlauf gegen die laufende Test-Instanz: Ersteinrichtung, Anmeldung, Agent (Fensterliste), Foto-Upload, Client-Log.
# Setzt dazu die Datenbank der Test-Instanz zurueck (alle Konten weg) und startet den Server neu; der Test-Desktop bleibt unberuehrt.
# Voraussetzung: eine angemeldete Sitzung am Test-Desktop (zum Beispiel einmal deploy/dev/test.sh --filter-class "*RealGuacdTests"
# laufen lassen), damit der Agent dort laeuft.
set -euo pipefail
cd "$(dirname "$0")"
BASE=http://127.0.0.1:18080
JAR="$(mktemp)"; trap 'rm -f "$JAR"' EXIT
api() { curl -sS -b "$JAR" -c "$JAR" -H 'X-JulOS: 1' -H "Origin: $BASE" "$@"; }
ok() { echo "  ok   $*"; }
fail() { echo "  FEHLT $*"; FAILED=1; }
FAILED=0

echo "Datenbank der Test-Instanz zuruecksetzen ..."
docker compose --env-file .env exec -T postgres psql -q -U julos -d julos -c 'drop schema public cascade; create schema public;' >/dev/null
docker compose --env-file .env restart julos >/dev/null
for i in $(seq 1 30); do curl -fsS -m 2 "$BASE/api/health" >/dev/null 2>&1 && break; sleep 1; done

code=$(docker compose --env-file .env logs julos 2>&1 | sed -n 's/.*Setup-Code ein: \([A-Za-z0-9-]*\).*/\1/p' | tail -1)
[ -n "$code" ] || { echo "Kein Setup-Code im Log (Datenbank nicht frisch?)"; exit 2; }
PW="$(openssl rand -hex 12)"   # Wegwerf-Passwort fuer das Test-Konto, nur in diesem Lauf

secret=$(api -X POST -H 'Content-Type: application/json' -d "{\"code\":\"$code\",\"user\":\"smoke\",\"password\":\"$PW\"}" "$BASE/api/setup/begin" | python3 -c 'import sys,json;print(json.load(sys.stdin)["secret"])')
totp() { python3 - "$secret" <<'PY'
import sys,hmac,hashlib,struct,time,base64
s=sys.argv[1].upper(); s+="="*(-len(s)%8)
k=base64.b32decode(s); c=struct.pack(">Q",int(time.time())//30)
h=hmac.new(k,c,hashlib.sha1).digest(); o=h[-1]&15
print("%06d"%((struct.unpack(">I",h[o:o+4])[0]&0x7fffffff)%1000000))
PY
}
api -X POST -H 'Content-Type: application/json' -d "{\"code\":\"$code\",\"user\":\"smoke\",\"totp\":\"$(totp)\"}" "$BASE/api/setup/finish" >/dev/null
[ "$(api "$BASE/api/auth/me" | python3 -c 'import sys,json;print(json.load(sys.stdin)["user"])')" = smoke ] && ok "Einrichtung und Anmeldung" || fail "Anmeldung"

health=$(api "$BASE/api/agent/health")
echo "$health" | grep -q '"available":true' && ok "Agent erreichbar: $health" || fail "Agent nicht erreichbar: $health"

win=$(api "$BASE/api/agent/windows" || true)
echo "$win" | python3 -c 'import sys,json;d=json.load(sys.stdin);print("  ok   Fensterliste:",len(d["windows"]),"Fenster, Modus",d["mode"])' 2>/dev/null || fail "Fensterliste: $win"
api "$BASE/api/agent/apps" | python3 -c 'import sys,json;d=json.load(sys.stdin);print("  ok   Programme:",len(d["apps"]))' 2>/dev/null || fail "Programmliste"

# Programm starten, in der Fensterliste finden, aktivieren, minimieren und schliessen (Terminal des Test-Desktops)
term=$(api "$BASE/api/agent/apps" | python3 -c 'import sys,json;a=[x["id"] for x in json.load(sys.stdin)["apps"] if "terminal" in x["id"].lower()];print(a[0] if a else "")')
if [ -n "$term" ]; then
  api -o /dev/null -X POST -H 'Content-Type: application/json' -d "{\"id\":\"$term\"}" "$BASE/api/agent/apps/launch"
  wid=""
  for i in $(seq 1 20); do
    wid=$(api "$BASE/api/agent/windows" | python3 -c 'import sys,json;w=json.load(sys.stdin)["windows"];print(w[0]["id"] if w else "")')
    [ -n "$wid" ] && break; sleep 0.5
  done
  if [ -n "$wid" ]; then
    ok "Programm gestartet ($term), Fenster $wid in der Liste"
    [ "$(api -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"maximize":true}' "$BASE/api/agent/windows/$wid/activate")" = 200 ] && ok "Fenster aktiviert" || fail "Fenster aktivieren"
    [ "$(api -o /dev/null -w '%{http_code}' -X POST "$BASE/api/agent/windows/$wid/close")" = 200 ] && ok "Fenster geschlossen" || fail "Fenster schliessen"
  else fail "Gestartetes Programm erscheint nicht in der Fensterliste"; fi
else fail "Kein Terminal-Programm in der Programmliste"; fi

# kleines gueltiges PNG (16x16, Python-Standardbibliothek) hochladen; der Helfer in der Sitzung legt es in die Zwischenablage und quittiert
png_file="$(mktemp)"
python3 - "$png_file" <<'PY'
import sys,zlib,struct
w=h=16
raw=b"".join(b"\x00"+bytes([200,60,60])*w for _ in range(h))
def chunk(t,d): c=struct.pack(">I",len(d))+t+d; return c+struct.pack(">I",zlib.crc32(t+d)&0xffffffff)
open(sys.argv[1],"wb").write(b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",w,h,8,2,0,0,0))+chunk(b"IDAT",zlib.compress(raw))+chunk(b"IEND",b""))
PY
status=$(api -o /tmp/julos-smoke-up.json -w '%{http_code}' -X POST -H 'Content-Type: image/png' --data-binary @"$png_file" "$BASE/api/upload")
rm -f "$png_file"
[ "$status" = 200 ] && ok "Foto-Upload: $(cat /tmp/julos-smoke-up.json)" || fail "Foto-Upload $status: $(cat /tmp/julos-smoke-up.json)"

[ "$(api -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"events":[{"evt":"smoke","ms":1}]}' "$BASE/api/log")" = 204 ] && ok "Client-Log" || fail "Client-Log"
docker compose --env-file .env logs julos 2>&1 | grep -q '"evt":"smoke"' && ok "Log-Zeile im Server-Log" || fail "Log-Zeile fehlt"

[ "$FAILED" = 0 ] && echo "Durchlauf bestanden." || { echo "Durchlauf mit Fehlern."; exit 1; }
