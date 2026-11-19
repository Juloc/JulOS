#!/bin/sh
# install-host-agent.sh - install the JulOS host agent on a Linux machine WITHOUT a desktop (Docker VM, Proxmox host, Debian/Ubuntu server).
#
# What it does (details: docs/HOST-AGENT.md):
#   1. creates the system user "julos-agent" (no login shell, home /var/lib/julos-agent),
#   2. downloads julos-agent.py from YOUR JulOS server (/agent-files/, SHA-256 checked against /agent-files/SHA256SUMS) into /opt/julos-agent,
#   3. creates /etc/julos-agent/permissions (owned by root: the agent cannot change what it may do), with --allow-updates the line "updates",
#   4. enrols the agent with the one-time code from the JulOS app ("Agent per SSH installieren"),
#   5. installs and starts the systemd service julos-agent (restart on failure, NoNewPrivileges, ProtectSystem=strict, only the paths it needs).
# It talks to the server you name with --server and to nobody else. It never reads or asks for passwords; sudo asks for its own.
#
# Usage (as root, for example through sudo):
#   sh install-host-agent.sh --server https://julos.example.org --code ABCDE-FGHIJ [options]
#   --dry-run          only show what would happen (needs no root, changes nothing, downloads nothing)
#   --user NAME        service account (default julos-agent; created when missing)
#   --allow-updates    write "updates" to /etc/julos-agent/permissions (the agent may install updates once step 3 of the host agent exists; not built yet)
#   --docker-group     let the service read the Docker socket (supplementary group "docker"; that group is root-equivalent, read the docs first)
#   --allow-http       test only: allow a server address without TLS
#   --no-start         install, but do not start the service
#   --uninstall        stop and remove service, files, configuration, token and (if this script created it) the user
#   --help
# Re-running is safe: present users, files and units are left alone unless they differ.
# Without systemd the files are installed and the command to start the agent by hand is printed.
# POSIX sh; checked with "shellcheck -s sh".

set -eu
PATH="$PATH:/usr/local/sbin:/usr/sbin:/sbin"      # useradd, nologin and friends live there; a plain user's PATH often lacks it

APP_DIR=/opt/julos-agent
ETC_DIR=/etc/julos-agent
UNIT_NAME=julos-agent.service
UNIT_FILE=/etc/systemd/system/$UNIT_NAME

SERVER="" CODE="" USER_NAME=julos-agent ALLOW_UPDATES=0 DOCKER_GROUP=0 ALLOW_HTTP=0 DRY=0 NO_START=0 UNINSTALL=0
TMPDIRS=""

say() { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }
plan() { printf '  [would] %s\n' "$*"; }
doing() { printf '  [do]    %s\n' "$*"; }

# shellcheck disable=SC2317  # called through the trap
cleanup() {
  for d in $TMPDIRS; do rm -rf "$d"; done
}
trap cleanup EXIT INT TERM

# act "description" command args...: in a dry run only the description is shown
act() {
  desc=$1; shift
  if [ "$DRY" = 1 ]; then plan "$desc"; return 0; fi
  doing "$desc"
  "$@"
}

usage() { sed -n '2,/^$/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --server) [ $# -ge 2 ] || die "--server needs a value"; SERVER=$2; shift 2 ;;
    --code) [ $# -ge 2 ] || die "--code needs a value"; CODE=$2; shift 2 ;;
    --user) [ $# -ge 2 ] || die "--user needs a value"; USER_NAME=$2; shift 2 ;;
    --allow-updates) ALLOW_UPDATES=1; shift ;;
    --docker-group) DOCKER_GROUP=1; shift ;;
    --allow-http) ALLOW_HTTP=1; shift ;;
    --no-start) NO_START=1; shift ;;
    --dry-run) DRY=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    --help|-h) usage; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

case "$USER_NAME" in
  ''|*[!a-z0-9_-]*|-*) die "--user: lower-case letters, digits, - and _ only" ;;
esac
HOME_DIR=/var/lib/$USER_NAME

if [ "$DRY" = 0 ] && [ "$(id -u)" != 0 ]; then
  die "needs root: run it with sudo (or add --dry-run to only look)"
fi

# ----------------------------------------------------------------------------------------------- uninstall
uninstall() {
  say "Removing the JulOS host agent."
  if have systemctl && [ -d /run/systemd/system ]; then
    act "stop and disable $UNIT_NAME" systemctl disable --now "$UNIT_NAME" || true
  fi
  [ ! -e "$UNIT_FILE" ] || act "remove $UNIT_FILE" rm -f "$UNIT_FILE"
  if have systemctl && [ -d /run/systemd/system ]; then act "systemctl daemon-reload" systemctl daemon-reload; fi
  [ ! -d "$APP_DIR" ] || act "remove $APP_DIR" rm -rf "$APP_DIR"
  created=""
  [ ! -f "$ETC_DIR/.created-user" ] || created=$(cat "$ETC_DIR/.created-user")
  [ ! -d "$ETC_DIR" ] || act "remove $ETC_DIR" rm -rf "$ETC_DIR"
  [ ! -d "$HOME_DIR" ] || act "remove $HOME_DIR (log and the stored token)" rm -rf "$HOME_DIR"
  if [ "$created" = "$USER_NAME" ] && id "$USER_NAME" >/dev/null 2>&1; then
    if have userdel; then act "remove the user $USER_NAME (created by this script)" userdel "$USER_NAME"
    elif have deluser; then act "remove the user $USER_NAME (created by this script)" deluser "$USER_NAME"
    fi
  fi
  say "Done. Also remove the agent in the JulOS app (host dialog, \"Agent entfernen\") so the token is dead on the server too."
}

if [ "$UNINSTALL" = 1 ]; then uninstall; exit 0; fi

# ----------------------------------------------------------------------------------------------- checks
[ -n "$SERVER" ] || die "--server is required (the address of your JulOS server, for example https://julos.example.org)"
SERVER=${SERVER%/}
case "$SERVER" in
  https://*) ;;
  http://localhost|http://localhost:*|http://127.0.0.1|http://127.0.0.1:*) ;;
  http://*) [ "$ALLOW_HTTP" = 1 ] || die "a server address without TLS (http) is refused: the token would travel in the clear. Test only: --allow-http" ;;
  *) die "--server must start with https://" ;;
esac
case "$SERVER" in
  *[!A-Za-z0-9._:/-]*) die "--server contains characters that do not belong in a server address" ;;
esac
if [ -n "$CODE" ]; then
  printf '%s' "$CODE" | grep -Eq '^[A-Za-z2-7]{5}-?[A-Za-z2-7]{5}$' || die "--code: expected the one-time code from the JulOS app, like ABCDE-FGHIJ"
fi

PYTHON=$(command -v python3 || true)
if [ -z "$PYTHON" ]; then
  if [ "$DRY" = 1 ]; then warn "python3 is missing (install it first: apt install python3)"; else die "python3 is missing: apt install python3 (3.8 or newer)"; fi
  PYTHON=/usr/bin/python3
elif ! "$PYTHON" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)'; then
  die "python3 is older than 3.8"
fi
FETCH=""
if have curl; then FETCH=curl; elif have wget; then FETCH=wget; fi
if [ -z "$FETCH" ]; then
  if [ "$DRY" = 1 ]; then warn "neither curl nor wget found"; else die "neither curl nor wget found (apt install curl)"; fi
fi
SYSTEMD=0
if have systemctl && [ -d /run/systemd/system ]; then SYSTEMD=1; fi
NOLOGIN=/usr/sbin/nologin
for n in /usr/sbin/nologin /sbin/nologin /usr/bin/nologin /bin/false; do
  if [ -x "$n" ]; then NOLOGIN=$n; break; fi
done

say "JulOS host agent: server $SERVER, service account $USER_NAME, files in $APP_DIR"
[ "$DRY" = 0 ] || say "Dry run: nothing is changed."

# ----------------------------------------------------------------------------------------------- helpers
sha256_of() {
  if have sha256sum; then sha256sum "$1" | cut -d' ' -f1
  elif have shasum; then shasum -a 256 "$1" | cut -d' ' -f1
  elif have openssl; then openssl dgst -sha256 "$1" | sed 's/^.*= //'
  else die "no sha256sum, shasum or openssl to check the download"
  fi
}

# Only from the server named with --server, no redirects, bounded size
fetch() {  # url dest
  case "$FETCH" in
    curl) curl -fsS --max-redirs 0 --max-filesize 2097152 --connect-timeout 15 --max-time 60 -o "$2" "$1" ;;
    wget) wget -q --max-redirect=0 --timeout=30 -O "$2" "$1" ;;
  esac
}

# run_as command args...: as the service account, without a shell login
run_as() {
  if have runuser; then runuser -u "$USER_NAME" -- "$@"
  elif have setpriv; then setpriv --reuid="$USER_NAME" --regid="$USER_NAME" --init-groups "$@"
  else
    line=""
    for a in "$@"; do line="$line '$(printf '%s' "$a" | sed "s/'/'\\\\''/g")'"; done
    su -s /bin/sh -c "exec$line" "$USER_NAME"
  fi
}

install_user() {
  if id "$USER_NAME" >/dev/null 2>&1; then
    say "  user $USER_NAME exists"
    return 0
  fi
  if have useradd; then act "create the system user $USER_NAME (no login shell)" useradd --system --user-group --home-dir "$HOME_DIR" --create-home --shell "$NOLOGIN" "$USER_NAME"
  elif have adduser; then act "create the system user $USER_NAME (no login shell)" adduser -S -D -h "$HOME_DIR" -s "$NOLOGIN" "$USER_NAME"
  elif [ "$DRY" = 1 ]; then warn "neither useradd nor adduser found"
  else die "neither useradd nor adduser found"
  fi
  if [ "$DRY" = 0 ]; then mkdir -p "$ETC_DIR"; printf '%s\n' "$USER_NAME" > "$ETC_DIR/.created-user"; fi
}

install_files() {
  act "create $APP_DIR (owned by $USER_NAME: the agent replaces its own file when you update it in the app)" mkdir -p "$APP_DIR"
  act "create $HOME_DIR (token, log; only $USER_NAME)" mkdir -p "$HOME_DIR"
  if [ "$DRY" = 1 ]; then
    plan "download $SERVER/agent-files/julos-agent.py and $SERVER/agent-files/SHA256SUMS, compare the SHA-256, install as $APP_DIR/julos-agent.py"
    return 0
  fi
  tmp=$(mktemp -d); TMPDIRS="$TMPDIRS $tmp"
  doing "download julos-agent.py and SHA256SUMS from $SERVER"
  fetch "$SERVER/agent-files/julos-agent.py" "$tmp/julos-agent.py" || die "download of julos-agent.py failed (is $SERVER the address of your JulOS server?)"
  fetch "$SERVER/agent-files/SHA256SUMS" "$tmp/SHA256SUMS" || die "download of SHA256SUMS failed"
  want=$(awk '$2 == "julos-agent.py" || $2 == "*julos-agent.py" { print $1 }' "$tmp/SHA256SUMS" | head -n 1)
  [ -n "$want" ] || die "SHA256SUMS does not list julos-agent.py"
  got=$(sha256_of "$tmp/julos-agent.py")
  [ "$want" = "$got" ] || die "checksum mismatch for julos-agent.py (server says $want, download is $got): nothing installed"
  say "  checksum ok: $got"
  "$PYTHON" -c 'import sys; compile(open(sys.argv[1], "rb").read(), "julos-agent.py", "exec")' "$tmp/julos-agent.py" || die "the download is not valid Python"
  if [ -f "$APP_DIR/julos-agent.py" ] && [ "$(sha256_of "$APP_DIR/julos-agent.py")" = "$got" ]; then
    say "  $APP_DIR/julos-agent.py is already this version"
  else
    install -m 0755 "$tmp/julos-agent.py" "$APP_DIR/julos-agent.py"
  fi
  chown -R "$USER_NAME":"$(id -gn "$USER_NAME")" "$APP_DIR" "$HOME_DIR"
  chmod 0750 "$HOME_DIR"
}

install_permissions() {
  if [ "$DRY" = 1 ]; then
    plan "create $ETC_DIR/permissions (root-owned, 0644)$([ "$ALLOW_UPDATES" = 1 ] && printf ' with the line "updates"')"
    return 0
  fi
  doing "configure $ETC_DIR/permissions (owned by root: the agent cannot give itself rights)"
  mkdir -p "$ETC_DIR"
  chmod 0755 "$ETC_DIR"
  if [ ! -f "$ETC_DIR/permissions" ]; then
    cat > "$ETC_DIR/permissions" <<'PERMS'
# What this agent may do on this machine, one word per line (the server must also allow it).
# Reading (system values, update list, failed services, containers) needs no entry.
# Writing is not built yet (planned: install updates, reboot); the words are kept for that:
#   updates
#   reboot
PERMS
  fi
  chown root:root "$ETC_DIR/permissions" 2>/dev/null || true
  chmod 0644 "$ETC_DIR/permissions"
  if [ "$ALLOW_UPDATES" = 1 ] && ! grep -Eq '^[[:space:]]*updates[[:space:]]*(#.*)?$' "$ETC_DIR/permissions"; then
    printf 'updates\n' >> "$ETC_DIR/permissions"
  fi
}

enrol() {
  have_config=0
  if [ -f "$HOME_DIR/.config/julos/agent.json" ]; then have_config=1; fi
  if [ -z "$CODE" ]; then
    if [ "$have_config" = 1 ]; then say "  already enrolled (no --code given, token kept)"; else warn "no --code given and not enrolled yet: the service will not start until you enrol (see docs/HOST-AGENT.md)"; fi
    return 0
  fi
  if [ "$DRY" = 1 ]; then plan "enrol this machine with the one-time code (as $USER_NAME)"; return 0; fi
  doing "enrol with the one-time code"
  if [ "$ALLOW_HTTP" = 1 ]; then
    run_as env HOME="$HOME_DIR" "$PYTHON" "$APP_DIR/julos-agent.py" --host --allow-http --server "$SERVER" --code "$CODE" && return 0
  else
    run_as env HOME="$HOME_DIR" "$PYTHON" "$APP_DIR/julos-agent.py" --host --server "$SERVER" --code "$CODE" && return 0
  fi
  if [ "$have_config" = 1 ]; then warn "enrolling failed (code used up or expired?); the earlier token is kept"; return 0; fi
  die "enrolling failed: the code is valid 10 minutes and works once; create a new one in the app"
}

unit_text() {
  groups=""
  if [ "$DOCKER_GROUP" = 1 ]; then groups="SupplementaryGroups=docker"; fi
  flags=""
  [ "$ALLOW_HTTP" = 0 ] || flags=" --allow-http"
  cat <<UNIT
[Unit]
Description=JulOS host agent (reports system values and updates to your JulOS server)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER_NAME
Group=$USER_NAME
$groups
ExecStart=$PYTHON $APP_DIR/julos-agent.py --host$flags
Restart=on-failure
RestartSec=10
# exit 2: not enrolled, exit 3: prerequisites missing; trying again would not help
RestartPreventExitStatus=2 3
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=$HOME_DIR $APP_DIR
PrivateTmp=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
RestrictSUIDSGID=yes
RestrictRealtime=yes
LockPersonality=yes
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
CapabilityBoundingSet=

[Install]
WantedBy=multi-user.target
UNIT
}

install_service() {
  if [ "$SYSTEMD" = 0 ]; then
    warn "no systemd on this machine: the service is not installed. Start the agent by hand (or from your init system) as $USER_NAME:"
    say "    su -s /bin/sh $USER_NAME -c 'HOME=$HOME_DIR $PYTHON $APP_DIR/julos-agent.py --host'"
    return 0
  fi
  if [ "$DOCKER_GROUP" = 1 ] && ! getent group docker >/dev/null 2>&1; then
    if [ "$DRY" = 1 ]; then warn "--docker-group: no group \"docker\" on this machine"; else die "--docker-group: no group \"docker\" on this machine"; fi
  fi
  if [ "$DRY" = 1 ]; then
    plan "write $UNIT_FILE:"
    unit_text | grep -v '^$' | sed 's/^/        | /'
    plan "systemctl daemon-reload; enable and start $UNIT_NAME"
    return 0
  fi
  doing "write $UNIT_FILE"
  new=$(mktemp); TMPDIRS="$TMPDIRS $new"
  unit_text | grep -v '^$' > "$new"
  if [ -f "$UNIT_FILE" ] && cmp -s "$new" "$UNIT_FILE"; then say "  unit unchanged"; else cp "$new" "$UNIT_FILE"; chmod 0644 "$UNIT_FILE"; fi
  systemctl daemon-reload
  if [ "$NO_START" = 1 ]; then
    systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
    say "  installed, not started (--no-start). Start: systemctl start $UNIT_NAME"
    return 0
  fi
  if [ ! -f "$HOME_DIR/.config/julos/agent.json" ]; then
    systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
    warn "not enrolled: the service is enabled but not started. Enrol with a new code from the app, then: systemctl start $UNIT_NAME"
    return 0
  fi
  doing "enable and (re)start $UNIT_NAME"
  systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
  systemctl restart "$UNIT_NAME"
  sleep 2
  systemctl --no-pager --lines=0 status "$UNIT_NAME" || true
}

install_user
install_files
install_permissions
enrol
install_service

if [ "$DRY" = 1 ]; then
  say "Dry run finished. Run it again without --dry-run (as root) to install."
else
  say "Done. The host should show up as connected in the JulOS app within a few seconds."
  say "Log: journalctl -u $UNIT_NAME   Remove: sh install-host-agent.sh --uninstall"
fi
