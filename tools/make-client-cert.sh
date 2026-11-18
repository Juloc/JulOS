#!/usr/bin/env bash
# Erzeugt (falls noetig) eine eigene kleine CA und ein Client-Zertifikat fuer dein Handy (mTLS).
# Caddy verlangt dann dieses Zertifikat, ohne es kommt niemand an die Seite heran.
# Aufruf:  tools/make-client-cert.sh [name]      (Standard: pixel)
# Die Export-Passphrase fuer die .p12 tippst DU selbst ein (openssl fragt danach).
set -euo pipefail
cd "$(dirname "$0")/.."
name="${1:-pixel}"
mkdir -p pki && chmod 700 pki && cd pki
[ -f ca.key ] || { openssl ecparam -genkey -name prime256v1 -noout -out ca.key; chmod 600 ca.key; }
[ -f ca.crt ] || openssl req -x509 -new -key ca.key -sha256 -days 3650 -subj "/CN=JulOS CA" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -out ca.crt
openssl ecparam -genkey -name prime256v1 -noout -out "$name.key"
openssl req -new -key "$name.key" -subj "/CN=julos-$name" -out "$name.csr"
printf 'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature\nextendedKeyUsage=clientAuth\n' > "$name.ext"
openssl x509 -req -in "$name.csr" -CA ca.crt -CAkey ca.key -CAcreateserial -sha256 -days 730 -extfile "$name.ext" -out "$name.crt"
echo
echo "Jetzt die Passphrase fuer die .p12 waehlen (wird beim Import auf dem Handy abgefragt):"
openssl pkcs12 -export -inkey "$name.key" -in "$name.crt" -certfile ca.crt -name "JulOS $name" -out "$name.p12"
rm -f "$name.csr" "$name.ext" "$name.key"
chmod 600 "$name.p12"
echo
echo "Fertig in $(pwd):"
echo "  ca.crt     -> auf den Caddy-Rechner (als Vertrauensanker fuer Client-Zertifikate)"
echo "  $name.p12  -> aufs Handy (Einstellungen > Sicherheit > Verschluesselung > Zertifikat installieren > VPN-/App-Nutzerzertifikat)"
echo "  ca.key     -> bleibt geheim; nach dem Ausstellen am besten offline sichern und von der VM loeschen."
