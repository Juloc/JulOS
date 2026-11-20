#!/bin/sh
# install-linux-agent.sh - kept so that existing commands keep working. There is ONE installer for the JulOS agent now: install-agent.sh (desktop and host in one,
# docs/HOST-AGENT.md). This file only forwards to it with --desktop in front of your options, so everything it understood still works:
#   sh install-linux-agent.sh --server https://julos.example.org --code ABCDE-FGHIJ [options]    ==    sh install-agent.sh --desktop --server ... --code ... [options]
# The installer is taken from the folder of this file; when it is not there, from the server you name with --server (/agent-files/install-agent.sh,
# SHA-256 checked against /agent-files/SHA256SUMS, TLS required, no redirects). That download happens even with --dry-run: it is only the installer, it is not run
# beyond what you asked for. No password is read here; sudo (if you use it) asks for its own.
# POSIX sh; checked with "shellcheck -s sh".

set -eu
FLAG=--desktop
dir=$(cd "$(dirname "$0")" && pwd)
printf 'note: install-linux-agent.sh is now install-agent.sh (one installer for desktop and host); forwarding with %s\n' "$FLAG" >&2

if [ -f "$dir/install-agent.sh" ]; then
  exec sh "$dir/install-agent.sh" "$FLAG" "$@"
fi

# Not next to this file (it was downloaded alone): fetch it from the named server
SERVER="" HTTP=0 prev=""
for a in "$@"; do
  if [ "$prev" = --server ]; then SERVER=$a; fi
  if [ "$a" = --allow-http ]; then HTTP=1; fi
  prev=$a
done
[ -n "$SERVER" ] || { echo "error: install-agent.sh is not next to this file: give --server so it can be downloaded from your JulOS server" >&2; exit 1; }
SERVER=${SERVER%/}
case "$SERVER" in
  https://*) ;;
  http://localhost|http://localhost:*|http://127.0.0.1|http://127.0.0.1:*) ;;
  http://*) [ "$HTTP" = 1 ] || { echo "error: a server address without TLS (http) is refused. Test only: --allow-http" >&2; exit 1; } ;;
  *) echo "error: --server must start with https://" >&2; exit 1 ;;
esac
case "$SERVER" in
  *[!A-Za-z0-9._:/-]*) echo "error: --server contains characters that do not belong in a server address" >&2; exit 1 ;;
esac
tmp=$(mktemp -d)
# shellcheck disable=SC2317  # called through the trap
cleanup() { rm -rf "$tmp"; }
trap cleanup EXIT INT TERM

fetch() {  # url dest
  if command -v curl >/dev/null 2>&1; then curl -fsS --max-redirs 0 --max-filesize 2097152 --connect-timeout 15 --max-time 60 -o "$2" "$1"
  elif command -v wget >/dev/null 2>&1; then wget -q --max-redirect=0 --timeout=30 -O "$2" "$1"
  else echo "error: neither curl nor wget found" >&2; return 1
  fi
}
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1
  elif command -v openssl >/dev/null 2>&1; then openssl dgst -sha256 "$1" | sed 's/^.*= //'
  else echo "error: no sha256sum, shasum or openssl to check the download" >&2; return 1
  fi
}
fetch "$SERVER/agent-files/install-agent.sh" "$tmp/install-agent.sh" || { echo "error: download of install-agent.sh failed (is $SERVER your JulOS server, and is it new enough?)" >&2; exit 1; }
fetch "$SERVER/agent-files/SHA256SUMS" "$tmp/SHA256SUMS" || { echo "error: download of SHA256SUMS failed" >&2; exit 1; }
want=$(awk '$2 == "install-agent.sh" || $2 == "*install-agent.sh" { print $1 }' "$tmp/SHA256SUMS" | head -n 1)
[ -n "$want" ] || { echo "error: SHA256SUMS does not list install-agent.sh" >&2; exit 1; }
got=$(sha256_of "$tmp/install-agent.sh") || exit 1
[ "$want" = "$got" ] || { echo "error: checksum mismatch for install-agent.sh (server says $want, download is $got): nothing done" >&2; exit 1; }
rc=0
sh "$tmp/install-agent.sh" "$FLAG" "$@" || rc=$?
exit "$rc"
