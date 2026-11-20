#!/bin/sh
# install-agent.sh - install (or update, or remove) THE JulOS agent on a Linux machine. One agent per machine, one installer.
#
# The script decides by itself what this machine is (details: docs/HOST-AGENT.md, "One agent per machine"):
#   desktop  a graphical session and the desktop stack (python3-gi, GTK 3, Wnck) are there: the agent runs as YOUR user in YOUR session (autostart entry or systemd user
#            service), never as root and never with sudo. It does everything: window bar, program launcher, photo, file and clipboard AND the host functions (system values,
#            updates, disks, services, Docker, confirmed actions). Files go to ~/.local/share/julos (an existing installation is updated where it is).
#   host     no desktop (Docker VM, Proxmox host, server): the agent runs as its own system user "julos-agent" (no login shell) as a systemd service in /opt/julos-agent.
# In both cases the same rules apply (details in the documentation):
#   - julos-agent.py (and for a desktop julos-clip.py) come from YOUR JulOS server only (/agent-files/, SHA-256 checked against /agent-files/SHA256SUMS, no redirects, size limit),
#   - /etc/julos-agent/permissions is owned by root (the agent cannot change what it may do) and EMPTY unless you name words with --allow,
#   - the small root helper (julos-agent-helper.py, from your server, checksum checked) is installed ONLY when a word that needs root is allowed (updates, reboot, services, logs, disks, files-root):
#     a socket-activated systemd service, socket reachable only by the agent's user. The agent itself never gets sudo or root.
# The script talks to the server you name with --server and to nobody else. It never reads or asks for a password; sudo asks for its own.
#
# Usage (as root through sudo from your own account; a desktop needs sudo only for the permission file and the root helper):
#   sh install-agent.sh --server https://julos.example.org --code ABCDE-FGHIJ [options]
#   --dry-run          only show what would happen (needs no root, changes nothing, downloads nothing)
#   --desktop | --host force the kind of installation (default: detect; an existing installation decides first)
#   --user NAME        host: the service account (default julos-agent, created when missing); desktop: the user whose session runs the agent (default: the user who called sudo)
#   --allow WORDS      write these words to /etc/julos-agent/permissions, comma separated, from: updates reboot services docker logs disks files-root. NEVER a default: without this option the agent can only read.
#                      Every action also needs your confirmation in the app each time and the "Aktionen" level for this host there. updates: install updates, refresh package lists (apt only);
#                      reboot: reboot and power off; services: list/start/stop/restart systemd services; docker: start/stop/restart containers, pull images (needs Docker socket access:
#                      see --docker-group); logs: read bounded logs of services and containers; disks: read the SMART health of the disks (read only, through the root helper);
#                      files-root: the Files app may list, read, write, rename and delete files on this machine AS ROOT (only with the level "Alles" for this host in the app; every action
#                      and every read is confirmed there and recorded; the agent's own files stay protected). That is almost as much as root itself: allow it only where you accept that.
#                      The words only add; remove one by editing the file as root.
#   --allow-updates    same as --allow updates
#   --docker-group     host: let the service read the Docker socket (supplementary group "docker"; that group is root-equivalent, read the docs first).
#                      desktop: only checks whether your user is in the group "docker" and tells you; the installer never changes your groups
#   --systemd          desktop: run the agent as a systemd user service instead of an autostart entry (kept when it is already one)
#   --allow-http       test only: allow a server address without TLS
#   --no-start         install, but do not start the agent
#   --uninstall        stop and remove the agent, its files, configuration and token, the root helper and (host) the user if this script created it
#   --help
# Re-running is safe and updates an existing installation without a new code: present users, files and units are left alone unless they differ; a token stays.
# Without systemd the files are installed and the command to start the agent by hand is printed.
# POSIX sh; checked with "shellcheck -s sh".

set -eu
PATH="$PATH:/usr/local/sbin:/usr/sbin:/sbin"      # useradd, nologin and friends live there; a plain user's PATH often lacks it

ETC_DIR=/etc/julos-agent
UNIT_NAME=julos-agent.service
UNIT_FILE=/etc/systemd/system/$UNIT_NAME
HOST_APP_DIR=/opt/julos-agent
HELPER_DIR=/usr/local/libexec/julos-agent
HELPER_FILE=$HELPER_DIR/julos-agent-helper.py
HELPER_SOCKET_PATH=/run/julos-agent-helper.sock
HELPER_SOCKET_UNIT=julos-agent-helper.socket
HELPER_SERVICE_UNIT=julos-agent-helper@.service
HELPER_SOCKET_FILE=/etc/systemd/system/$HELPER_SOCKET_UNIT
HELPER_SERVICE_FILE=/etc/systemd/system/$HELPER_SERVICE_UNIT
KNOWN_WORDS="updates reboot services docker logs disks files-root"

SERVER="" CODE="" USER_OPT="" ALLOW_WORDS="" DOCKER_GROUP=0 ALLOW_HTTP=0 DRY=0 NO_START=0 UNINSTALL=0 FORCE_MODE="" WANT_SYSTEMD=0
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
    --user) [ $# -ge 2 ] || die "--user needs a value"; USER_OPT=$2; shift 2 ;;
    --allow) [ $# -ge 2 ] || die "--allow needs a value (for example updates,logs)"; ALLOW_WORDS="$ALLOW_WORDS $(printf '%s' "$2" | tr ',' ' ')"; shift 2 ;;
    --allow-updates) ALLOW_WORDS="$ALLOW_WORDS updates"; shift ;;
    --docker-group) DOCKER_GROUP=1; shift ;;
    --systemd) WANT_SYSTEMD=1; shift ;;
    --allow-http) ALLOW_HTTP=1; shift ;;
    --no-start) NO_START=1; shift ;;
    --dry-run) DRY=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    --desktop) [ "$FORCE_MODE" != host ] || die "--desktop and --host exclude each other"; FORCE_MODE=desktop; shift ;;
    --host) [ "$FORCE_MODE" != desktop ] || die "--desktop and --host exclude each other"; FORCE_MODE=host; shift ;;
    --help|-h) usage; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

if [ -n "$USER_OPT" ]; then
  case "$USER_OPT" in
    *[!A-Za-z0-9._-]*|-*|.*) die "--user: letters, digits, . - and _ only" ;;
  esac
fi

# The allowed words: only known ones, each once
WORDS=""
for w in $ALLOW_WORDS; do
  case " $KNOWN_WORDS " in
    *" $w "*) case " $WORDS " in *" $w "*) ;; *) WORDS="$WORDS $w" ;; esac ;;
    *) die "--allow: unknown word \"$w\" (known: $KNOWN_WORDS)" ;;
  esac
done
WORDS=${WORDS# }
has_word() { case " $WORDS " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
# What needs root: apt, systemctl, journalctl, reboot, files as root. Docker goes through the socket the agent already reads.
NEED_HELPER=0
for w in updates reboot services logs disks files-root; do if has_word "$w"; then NEED_HELPER=1; fi; done

IS_ROOT=0
if [ "$(id -u)" = 0 ]; then IS_ROOT=1; fi

# ----------------------------------------------------------------------------------------------- who is the desktop user, what is this machine
# The user whose session would run a desktop agent: --user, else the one who called sudo, else the plain user running this script. Plain root without sudo has none.
DESK_USER=""
if [ -n "$USER_OPT" ]; then DESK_USER=$USER_OPT
elif [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != root ]; then
  case "$SUDO_USER" in *[!A-Za-z0-9._-]*|-*|.*) ;; *) DESK_USER=$SUDO_USER ;; esac
elif [ "$IS_ROOT" = 0 ]; then DESK_USER=$(id -un)
fi
# JULOS_INSTALL_FAKE_HOME / JULOS_INSTALL_FAKE_HOST: tests only, and only in a dry run (where nothing is changed): another home folder, and "a host agent is installed"
user_home() {
  if [ "$DRY" = 1 ] && [ -n "${JULOS_INSTALL_FAKE_HOME:-}" ]; then printf '%s\n' "$JULOS_INSTALL_FAKE_HOME"; return 0; fi
  getent passwd "$1" 2>/dev/null | cut -d: -f6
}
user_exists() { id "$1" >/dev/null 2>&1; }

PYTHON=$(command -v python3 || true)

# The desktop stack of the agent (PyGObject, GTK 3, Wnck). JULOS_INSTALL_FAKE_DESKTOP=1|0 decides without looking (tests only).
desktop_stack() {
  case "${JULOS_INSTALL_FAKE_DESKTOP:-}" in 1) return 0 ;; 0) return 1 ;; *) ;; esac
  [ -n "$PYTHON" ] || return 1
  "$PYTHON" -c 'import gi; gi.require_version("Gtk", "3.0"); gi.require_version("Wnck", "3.0")' >/dev/null 2>&1
}

# A graphical session of this user (X11 or Wayland, per logind). Without one registered there (a VNC or remote session, no logind), a LOCAL display in the caller's
# environment counts (":0", ":10.0"; sudo keeps DISPLAY); a forwarded one over SSH ("localhost:10.0") does not.
graphical_session() {
  case "${JULOS_INSTALL_FAKE_DESKTOP:-}" in 1) return 0 ;; 0) return 1 ;; *) ;; esac
  if have loginctl; then
    for sid in $(loginctl list-sessions --no-legend 2>/dev/null | awk -v u="$1" '$3 == u { print $1 }'); do
      case "$(loginctl show-session "$sid" -p Type --value 2>/dev/null)" in x11|wayland|mir) return 0 ;; *) ;; esac
    done
  fi
  case "${DISPLAY:-}" in :*) return 0 ;; *) ;; esac
  [ -n "${WAYLAND_DISPLAY:-}" ]
}

# An installation that exists already decides first: a host agent as a system service, or a desktop agent that is started for this user.
HOST_EXISTS=0
if [ -e "$UNIT_FILE" ] || [ -d "$HOST_APP_DIR" ] || { [ "$DRY" = 1 ] && [ "${JULOS_INSTALL_FAKE_HOST:-}" = 1 ]; }; then HOST_EXISTS=1; fi
DESK_HOME="" DESK_GROUP=""
if [ -n "$DESK_USER" ] && user_exists "$DESK_USER"; then
  DESK_HOME=$(user_home "$DESK_USER")
  DESK_GROUP=$(id -gn "$DESK_USER")
fi
DESK_CONFIG_DIR=""
if [ -n "$DESK_HOME" ]; then DESK_CONFIG_DIR=$DESK_HOME/.config; fi
# Where an existing desktop agent starts from (autostart entry or user unit), when that folder belongs to the user and is not a git checkout.
existing_desk_dir() {
  [ -n "$DESK_CONFIG_DIR" ] || return 1
  for f in "$DESK_CONFIG_DIR/autostart/julos-agent.desktop" "$DESK_CONFIG_DIR/systemd/user/julos-agent.service"; do
    [ -f "$f" ] || continue
    p=$(grep -o '/[^" ]*julos-agent\.py' "$f" | head -n 1 || true)
    [ -n "$p" ] || continue
    d=${p%/*}
    [ -d "$d" ] || continue
    [ ! -e "$d/.git" ] || continue
    [ "$(stat -c %U "$d" 2>/dev/null || true)" = "$DESK_USER" ] || continue
    printf '%s\n' "$d"
    return 0
  done
  return 1
}
DESK_EXISTS=0
if [ -n "$DESK_CONFIG_DIR" ] && { [ -f "$DESK_CONFIG_DIR/autostart/julos-agent.desktop" ] || [ -f "$DESK_CONFIG_DIR/systemd/user/julos-agent.service" ]; }; then DESK_EXISTS=1; fi

MODE=$FORCE_MODE
WHY=""
if [ -z "$MODE" ] && [ "$UNINSTALL" = 0 ]; then
  if [ "$HOST_EXISTS" = 1 ] && [ "$DESK_EXISTS" = 0 ]; then MODE=host; WHY="a host agent is installed here already (it is updated)"
  elif [ "$DESK_EXISTS" = 1 ] && [ "$HOST_EXISTS" = 0 ]; then MODE=desktop; WHY="a desktop agent is installed for $DESK_USER already (it is updated)"
  elif [ "$DESK_EXISTS" = 1 ] && [ "$HOST_EXISTS" = 1 ]; then die "a host agent AND a desktop agent are installed here: one machine, one agent. Remove one (sh install-agent.sh --host --uninstall, or --desktop --uninstall) or say which one to update with --host or --desktop"
  elif [ -z "$DESK_USER" ]; then MODE=host; WHY="no user to run a desktop agent for (plain root without sudo)"
  elif desktop_stack && graphical_session "$DESK_USER"; then MODE=desktop; WHY="a graphical session of $DESK_USER and the desktop stack (GTK, Wnck) are there"
  elif graphical_session "$DESK_USER"; then
    die "a graphical session of $DESK_USER was found, but the desktop stack is missing (so this would become a host agent without window bar and clipboard). Install it and run again: sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0   (or add --host to install the host agent anyway)"
  else MODE=host; WHY="no graphical session of a user was found"
  fi
fi

if [ "$MODE" = desktop ]; then
  [ -n "$DESK_USER" ] || die "--desktop: no user for the agent. Run it with sudo from your own account (the agent runs in your session), or name the user with --user"
  user_exists "$DESK_USER" || die "--desktop: the user \"$DESK_USER\" does not exist"
  if [ "$HOST_EXISTS" = 1 ] && [ "$UNINSTALL" = 0 ]; then
    die "a host agent is installed here (service $UNIT_NAME). One machine, one agent: remove it first (sh install-agent.sh --host --uninstall; the old token dies in the app with \"Agent entfernen\", then enrol with a new code), or keep it and add --host"
  fi
  RUN_USER=$DESK_USER
  RUN_HOME=$DESK_HOME
  RUN_GROUP=$DESK_GROUP
  APP_DIR=$(existing_desk_dir || true)
  [ -n "$APP_DIR" ] || APP_DIR=$DESK_HOME/.local/share/julos
  CONFIG_FILE=$DESK_HOME/.config/julos/agent.json
else
  RUN_USER=${USER_OPT:-julos-agent}
  RUN_HOME=/var/lib/$RUN_USER
  RUN_GROUP=$RUN_USER
  APP_DIR=$HOST_APP_DIR
  CONFIG_FILE=$RUN_HOME/.config/julos/agent.json
  if [ "$UNINSTALL" = 0 ] && [ -n "$DESK_CONFIG_DIR" ] && [ "$DESK_EXISTS" = 1 ] && [ "$FORCE_MODE" = host ]; then
    warn "a desktop agent is installed for $DESK_USER as well: one machine, one agent. Remove one of them (--desktop --uninstall)"
  fi
fi

if [ "$DRY" = 0 ] && [ "$IS_ROOT" = 0 ] && [ "$UNINSTALL" = 0 ]; then
  if [ "$MODE" = host ]; then die "needs root: run it with sudo (or add --dry-run to only look)"; fi
  if [ "$NEED_HELPER" = 1 ] || [ -n "$WORDS" ]; then die "needs root for --allow (the permission file and the root helper belong to root): run it with sudo (or add --dry-run to only look)"; fi
  [ "$DESK_USER" = "$(id -un)" ] || die "needs root to set up the agent for another user: run it with sudo (or add --dry-run to only look)"
fi

# ----------------------------------------------------------------------------------------------- run as the agent's user
# as_user command args...: as the agent's user (the desktop user, or the service account), without a shell login
as_user() {
  if [ "$IS_ROOT" = 0 ]; then "$@"
  elif have runuser; then runuser -u "$RUN_USER" -- "$@"
  elif have setpriv; then setpriv --reuid="$RUN_USER" --regid="$RUN_GROUP" --init-groups "$@"
  else
    line=""
    for a in "$@"; do line="$line '$(printf '%s' "$a" | sed "s/'/'\\\\''/g")'"; done
    su -s /bin/sh -c "exec$line" "$RUN_USER"
  fi
}
# Folders and files that belong to the user (a desktop) or to the service account (a host): owner only when root does it
own() { if [ "$IS_ROOT" = 1 ]; then chown "$RUN_USER":"$RUN_GROUP" "$@"; fi; }

NOLOGIN=/usr/sbin/nologin
for n in /usr/sbin/nologin /sbin/nologin /usr/bin/nologin /bin/false; do
  if [ -x "$n" ]; then NOLOGIN=$n; break; fi
done
SYSTEMD=0
if have systemctl && [ -d /run/systemd/system ]; then SYSTEMD=1; fi

# ----------------------------------------------------------------------------------------------- uninstall
uninstall_root_parts() {   # the root helper, its units and the permission file: shared by both kinds
  if have systemctl && [ -d /run/systemd/system ] && [ -e "$HELPER_SOCKET_FILE" ]; then
    act "stop and disable $HELPER_SOCKET_UNIT" systemctl disable --now "$HELPER_SOCKET_UNIT" || true
  fi
  [ ! -e "$HELPER_SOCKET_FILE" ] || act "remove $HELPER_SOCKET_FILE" rm -f "$HELPER_SOCKET_FILE"
  [ ! -e "$HELPER_SERVICE_FILE" ] || act "remove $HELPER_SERVICE_FILE" rm -f "$HELPER_SERVICE_FILE"
  [ ! -d "$HELPER_DIR" ] || act "remove $HELPER_DIR (the root helper)" rm -rf "$HELPER_DIR"
  if have systemctl && [ -d /run/systemd/system ]; then act "systemctl daemon-reload" systemctl daemon-reload; fi
}

uninstall_host() {
  say "Removing the JulOS host agent."
  if have systemctl && [ -d /run/systemd/system ]; then
    act "stop and disable $UNIT_NAME" systemctl disable --now "$UNIT_NAME" || true
  fi
  [ ! -e "$UNIT_FILE" ] || act "remove $UNIT_FILE" rm -f "$UNIT_FILE"
  uninstall_root_parts
  [ ! -d "$HOST_APP_DIR" ] || act "remove $HOST_APP_DIR" rm -rf "$HOST_APP_DIR"
  created=""
  [ ! -f "$ETC_DIR/.created-user" ] || created=$(cat "$ETC_DIR/.created-user")
  [ ! -d "$ETC_DIR" ] || act "remove $ETC_DIR" rm -rf "$ETC_DIR"
  [ ! -d "$RUN_HOME" ] || act "remove $RUN_HOME (log and the stored token)" rm -rf "$RUN_HOME"
  if [ "$created" = "$RUN_USER" ] && id "$RUN_USER" >/dev/null 2>&1; then
    if have userdel; then act "remove the user $RUN_USER (created by this script)" userdel "$RUN_USER"
    elif have deluser; then act "remove the user $RUN_USER (created by this script)" deluser "$RUN_USER"
    fi
  fi
  say "Done. Also remove the agent in the JulOS app (host dialog, \"Agent entfernen\") so the token is dead on the server too."
}

uninstall_desktop() {
  say "Removing the JulOS desktop agent of $DESK_USER."
  agent_py=$APP_DIR/julos-agent.py
  if [ -f "$agent_py" ] && [ -n "$PYTHON" ]; then
    act "stop the agent and remove its autostart entry or user service, files, configuration, token and log (julos-agent.py uninstall)" as_user env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$DESK_HOME" "$PYTHON" "$agent_py" uninstall --yes
  else
    for f in "$DESK_CONFIG_DIR/autostart/julos-agent.desktop" "$DESK_CONFIG_DIR/autostart/julos-clip.desktop"; do
      [ ! -e "$f" ] || act "remove $f" rm -f "$f"
    done
  fi
  say "The folder $DESK_HOME/julos-uploads (photos and files for the clipboard) stays."
  if [ "$IS_ROOT" = 1 ]; then
    uninstall_root_parts
    [ ! -d "$ETC_DIR" ] || act "remove $ETC_DIR" rm -rf "$ETC_DIR"
  elif [ -e "$HELPER_SOCKET_FILE" ] || [ -d "$ETC_DIR" ]; then
    say "Run it with sudo as well to remove the root helper and $ETC_DIR."
  fi
  say "Done. Also remove the agent in the JulOS app (host dialog, \"Agent entfernen\") so the token is dead on the server too."
}

if [ "$UNINSTALL" = 1 ]; then
  # Without --desktop or --host: whatever is installed here (both, if both are); nothing installed: the host cleanup, which is harmless
  did=0
  if [ "$FORCE_MODE" = desktop ] || { [ -z "$FORCE_MODE" ] && [ "$DESK_EXISTS" = 1 ]; }; then
    if [ -z "$DESK_USER" ] || [ -z "$DESK_HOME" ]; then die "--uninstall: no desktop user (run it with sudo from your own account, or name the user with --user)"; fi
    if [ "$DRY" = 0 ] && [ "$IS_ROOT" = 0 ] && [ "$DESK_USER" != "$(id -un)" ]; then die "needs root: run it with sudo (or add --dry-run to only look)"; fi
    APP_DIR=$(existing_desk_dir || true)
    [ -n "$APP_DIR" ] || APP_DIR=$DESK_HOME/.local/share/julos
    RUN_USER=$DESK_USER RUN_GROUP=$DESK_GROUP RUN_HOME=$DESK_HOME
    uninstall_desktop
    did=1
  fi
  if [ "$FORCE_MODE" = host ] || { [ -z "$FORCE_MODE" ] && { [ "$HOST_EXISTS" = 1 ] || [ "$did" = 0 ]; }; }; then
    if [ "$DRY" = 0 ] && [ "$IS_ROOT" = 0 ]; then die "needs root: run it with sudo (or add --dry-run to only look)"; fi
    RUN_USER=${USER_OPT:-julos-agent}
    RUN_GROUP=$RUN_USER RUN_HOME=/var/lib/$RUN_USER
    uninstall_host
  fi
  exit 0
fi

# ----------------------------------------------------------------------------------------------- checks
if [ -z "$SERVER" ] && [ "$MODE" = desktop ]; then
  here=$(cd "$(dirname "$0")" && pwd)
  [ -f "$here/julos-agent.py" ] || die "--server is required (the address of your JulOS server, for example https://julos.example.org)"
  LOCAL_SRC=$here          # no server: a desktop on the same machine as the server uses the files next to this script (the local socket way)
else
  LOCAL_SRC=""
  [ -n "$SERVER" ] || die "--server is required (the address of your JulOS server, for example https://julos.example.org)"
fi
SERVER=${SERVER%/}
if [ -n "$SERVER" ]; then
  case "$SERVER" in
    https://*) ;;
    http://localhost|http://localhost:*|http://127.0.0.1|http://127.0.0.1:*) ;;
    http://*) [ "$ALLOW_HTTP" = 1 ] || die "a server address without TLS (http) is refused: the token would travel in the clear. Test only: --allow-http" ;;
    *) die "--server must start with https://" ;;
  esac
  case "$SERVER" in
    *[!A-Za-z0-9._:/-]*) die "--server contains characters that do not belong in a server address" ;;
  esac
fi
if [ -n "$CODE" ]; then
  [ -n "$SERVER" ] || die "--code needs --server"
  printf '%s' "$CODE" | grep -Eq '^[A-Za-z2-7]{5}-?[A-Za-z2-7]{5}$' || die "--code: expected the one-time code from the JulOS app, like ABCDE-FGHIJ"
fi

if [ -z "$PYTHON" ]; then
  if [ "$DRY" = 1 ]; then warn "python3 is missing (install it first: apt install python3)"; else die "python3 is missing: apt install python3 (3.8 or newer)"; fi
  PYTHON=/usr/bin/python3
elif ! "$PYTHON" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)'; then
  die "python3 is older than 3.8"
fi
FETCH=""
if have curl; then FETCH=curl; elif have wget; then FETCH=wget; fi
if [ -z "$FETCH" ] && [ -n "$SERVER" ]; then
  if [ "$DRY" = 1 ]; then warn "neither curl nor wget found"; else die "neither curl nor wget found (apt install curl)"; fi
fi
if [ "$MODE" = desktop ] && ! desktop_stack; then
  msg="the desktop stack is missing: sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0"
  if [ "$DRY" = 1 ]; then warn "$msg"; else die "$msg"; fi
fi

if [ "$MODE" = desktop ]; then
  say "JulOS desktop agent for $DESK_USER: server ${SERVER:-(none, local socket)}, files in $APP_DIR"
  say "Desktop detected (${WHY:---desktop given}): the agent runs in the session of $DESK_USER, not as root, and also answers the host requests (system values, updates, actions)."
else
  say "JulOS host agent: server $SERVER, service account $RUN_USER, files in $APP_DIR"
  say "No desktop installation (${WHY:---host given}): the host agent runs as the service account $RUN_USER."
fi
if [ -n "$WORDS" ]; then say "Actions you allow on this machine (each one still needs your confirmation in the app): $WORDS"; else say "No action words: the agent can only read (add --allow to change that, see --help)."; fi
if has_word docker && [ "$DOCKER_GROUP" = 0 ] && [ "$MODE" = host ]; then warn "docker is allowed, but the agent has no access to the Docker socket yet (--docker-group, or give it a socket proxy that allows start/stop; see docs/HOST-AGENT.md)"; fi
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

# fetch_checked NAME DIR: NAME from /agent-files/ into DIR, compared with SHA256SUMS and checked as Python; prints the checksum
SUMS_DIR=""
fetch_checked() {
  name=$1; dest=$2
  if [ -z "$SUMS_DIR" ]; then
    SUMS_DIR=$(mktemp -d); TMPDIRS="$TMPDIRS $SUMS_DIR"
    fetch "$SERVER/agent-files/SHA256SUMS" "$SUMS_DIR/SHA256SUMS" || die "download of SHA256SUMS failed (is $SERVER the address of your JulOS server?)"
  fi
  fetch "$SERVER/agent-files/$name" "$dest/$name" || die "download of $name failed (is $SERVER the address of your JulOS server, and is it new enough?)"
  want=$(awk -v n="$name" '$2 == n || $2 == "*" n { print $1 }' "$SUMS_DIR/SHA256SUMS" | head -n 1)
  [ -n "$want" ] || die "SHA256SUMS does not list $name"
  got=$(sha256_of "$dest/$name")
  [ "$want" = "$got" ] || die "checksum mismatch for $name (server says $want, download is $got): nothing installed"
  "$PYTHON" -c 'import sys; compile(open(sys.argv[1], "rb").read(), sys.argv[2], "exec")' "$dest/$name" "$name" || die "the download of $name is not valid Python"
  say "  checksum ok: $name $got"
}

install_user() {
  if [ "$MODE" = desktop ]; then
    say "  the agent runs as the existing user $RUN_USER"
    return 0
  fi
  if id "$RUN_USER" >/dev/null 2>&1; then
    say "  user $RUN_USER exists"
    return 0
  fi
  if have useradd; then act "create the system user $RUN_USER (no login shell)" useradd --system --user-group --home-dir "$RUN_HOME" --create-home --shell "$NOLOGIN" "$RUN_USER"
  elif have adduser; then act "create the system user $RUN_USER (no login shell)" adduser -S -D -h "$RUN_HOME" -s "$NOLOGIN" "$RUN_USER"
  elif [ "$DRY" = 1 ]; then warn "neither useradd nor adduser found"
  else die "neither useradd nor adduser found"
  fi
  if [ "$DRY" = 0 ]; then mkdir -p "$ETC_DIR"; printf '%s\n' "$RUN_USER" > "$ETC_DIR/.created-user"; fi
}

# The agent's files. Host: /opt/julos-agent, owned by the service account (the agent replaces its own file on an update). Desktop: owned by the user, same reason.
# julos-clip.py (photos and files for the clipboard) belongs to the desktop only.
install_files() {
  files="julos-agent.py"
  [ "$MODE" = desktop ] && files="julos-agent.py julos-clip.py"
  if [ "$MODE" = desktop ]; then
    act "create $APP_DIR (owned by $RUN_USER: the agent replaces its own file when you update it in the app)" as_user mkdir -p "$APP_DIR"
    act "create the state and upload folders of $RUN_USER (log, ~/julos-uploads)" as_user mkdir -p "$RUN_HOME/julos-uploads" "$RUN_HOME/.local/state/julos"
  else
    act "create $APP_DIR (owned by $RUN_USER: the agent replaces its own file when you update it in the app)" mkdir -p "$APP_DIR"
    act "create $RUN_HOME (token, log; only $RUN_USER)" mkdir -p "$RUN_HOME"
  fi
  if [ "$DRY" = 1 ]; then
    if [ -n "$LOCAL_SRC" ]; then
      plan "copy $files from $LOCAL_SRC (no server named: the files next to this script) to $APP_DIR"
    else
      plan "download $SERVER/agent-files/julos-agent.py and $SERVER/agent-files/SHA256SUMS, compare the SHA-256, install as $APP_DIR/julos-agent.py"
      [ "$MODE" = desktop ] && plan "download $SERVER/agent-files/julos-clip.py the same way (the clipboard helper)"
    fi
    return 0
  fi
  tmp=$(mktemp -d); TMPDIRS="$TMPDIRS $tmp"
  if [ -n "$LOCAL_SRC" ]; then
    for f in $files; do [ -f "$LOCAL_SRC/$f" ] || continue; cp "$LOCAL_SRC/$f" "$tmp/$f"; done
  else
    doing "download $files and SHA256SUMS from $SERVER"
    for f in $files; do fetch_checked "$f" "$tmp"; done
  fi
  for f in $files; do
    [ -f "$tmp/$f" ] || continue
    got=$(sha256_of "$tmp/$f")
    if [ -f "$APP_DIR/$f" ] && [ "$(sha256_of "$APP_DIR/$f")" = "$got" ]; then
      say "  $APP_DIR/$f is already this version"
    else
      install -m 0755 "$tmp/$f" "$APP_DIR/$f"
    fi
  done
  if [ "$MODE" = desktop ]; then
    own "$APP_DIR/julos-agent.py"
    [ ! -f "$APP_DIR/julos-clip.py" ] || own "$APP_DIR/julos-clip.py"
    own "$APP_DIR"
    chmod 0700 "$RUN_HOME/.local/state/julos" 2>/dev/null || true
  else
    chown -R "$RUN_USER":"$RUN_GROUP" "$APP_DIR" "$RUN_HOME"
    chmod 0750 "$RUN_HOME"
  fi
}

install_permissions() {
  if [ "$IS_ROOT" = 0 ] && [ "$DRY" = 0 ]; then
    say "  not root: $ETC_DIR/permissions is left as it is (without that file the agent can only read); run it with sudo to set words"
    return 0
  fi
  if [ "$DRY" = 1 ]; then
    if [ -n "$WORDS" ]; then
      plan "create $ETC_DIR/permissions (root-owned, 0644) and add the words: $WORDS"
    else
      plan "create $ETC_DIR/permissions (root-owned, 0644) with no word: the agent can only read"
    fi
    return 0
  fi
  doing "configure $ETC_DIR/permissions (owned by root: the agent cannot give itself rights)"
  mkdir -p "$ETC_DIR"
  chmod 0755 "$ETC_DIR"
  if [ ! -f "$ETC_DIR/permissions" ]; then
    cat > "$ETC_DIR/permissions" <<'PERMS'
# What this agent may do on this machine, one word per line (the server must also allow it, and every single action needs your confirmation in the app).
# Reading (system values, update list, failed services, containers) needs no entry. By default NOTHING below is set: the agent can only read.
# Words (remove a line to take the right away; the agent and its root helper read this file again at every request):
#   updates    install updates, refresh package lists, repair half configured packages (apt)
#   reboot     reboot and power off
#   services   list, start, stop, restart systemd services
#   docker     start, stop, restart containers, pull images (also needs access to the Docker socket)
#   logs       read bounded logs of services and containers
#   disks      read the SMART health of the disks (read only, through the root helper)
#   files-root the Files app may list, read, write, rename and delete files as ROOT (needs the level "Alles" for this host in the app)
PERMS
  fi
  chown root:root "$ETC_DIR/permissions" 2>/dev/null || true
  chmod 0644 "$ETC_DIR/permissions"
  for w in $WORDS; do
    if ! grep -Eq "^[[:space:]]*${w}[[:space:]]*(#.*)?\$" "$ETC_DIR/permissions"; then printf '%s\n' "$w" >> "$ETC_DIR/permissions"; fi
  done
}

# Who may talk to the root helper: the agent's user and nobody else. Host: the service account's own group, mode 0660. Desktop: the socket belongs to the
# user, mode 0600 (a user's primary group may be shared with others, so the group is not what protects it).
helper_socket_unit_text() {
  if [ "$MODE" = desktop ]; then s_user=$RUN_USER s_mode=0600; else s_user=root s_mode=0660; fi
  cat <<UNIT
[Unit]
Description=JulOS agent helper (root side of confirmed actions)

[Socket]
ListenStream=$HELPER_SOCKET_PATH
SocketUser=$s_user
SocketGroup=$RUN_GROUP
SocketMode=$s_mode
Accept=yes
MaxConnections=4

[Install]
WantedBy=sockets.target
UNIT
}

helper_service_unit_text() {
  cat <<UNIT
[Unit]
Description=JulOS agent helper (one confirmed action, as root)

[Service]
Type=simple
ExecStart=$PYTHON $HELPER_FILE
StandardInput=socket
StandardOutput=socket
StandardError=journal
TimeoutStopSec=30
ProtectKernelModules=yes
LockPersonality=yes
UNIT
  # The sandbox would hide /home and give the helper its own /tmp. For files as root that would be a lie (the Files app would show an empty /home and a private /tmp),
  # so with files-root the helper sees the real file system; without that word the stricter view stays.
  if ! has_word files-root; then printf 'ProtectHome=yes\nPrivateTmp=yes\n'; fi
}

# The root helper for actions that need root. Only installed when such a word was named with --allow; re-running this script updates it.
install_helper() {
  [ "$NEED_HELPER" = 1 ] || return 0
  if [ "$SYSTEMD" = 0 ]; then
    warn "no systemd on this machine: the root helper is not installed, so actions that need root (updates, reboot, services, logs, disks, files-root) will not work"
    return 0
  fi
  if [ "$DRY" = 1 ]; then
    if [ -n "$LOCAL_SRC" ]; then plan "copy julos-agent-helper.py from $LOCAL_SRC, install as $HELPER_FILE (root-owned, the agent cannot change it)"
    else plan "download $SERVER/agent-files/julos-agent-helper.py, compare the SHA-256 with SHA256SUMS, install as $HELPER_FILE (root-owned, the agent cannot change it)"; fi
    plan "write $HELPER_SOCKET_FILE (socket $HELPER_SOCKET_PATH, only $RUN_USER can reach it) and $HELPER_SERVICE_FILE (runs the helper as root, one request per connection):"
    helper_socket_unit_text | grep -v '^$' | sed 's/^/        | /'
    helper_service_unit_text | grep -v '^$' | sed 's/^/        | /'
    plan "systemctl daemon-reload; enable and start $HELPER_SOCKET_UNIT"
    return 0
  fi
  tmp=$(mktemp -d); TMPDIRS="$TMPDIRS $tmp"
  if [ -n "$LOCAL_SRC" ]; then
    [ -f "$LOCAL_SRC/julos-agent-helper.py" ] || die "julos-agent-helper.py is not next to this script"
    cp "$LOCAL_SRC/julos-agent-helper.py" "$tmp/julos-agent-helper.py"
  else
    doing "download julos-agent-helper.py and SHA256SUMS from $SERVER"
    fetch_checked julos-agent-helper.py "$tmp"
  fi
  doing "install the helper as $HELPER_FILE (root-owned)"
  mkdir -p "$HELPER_DIR"
  chown root:root "$HELPER_DIR" 2>/dev/null || true
  chmod 0755 "$HELPER_DIR"
  install -m 0755 "$tmp/julos-agent-helper.py" "$HELPER_FILE"
  chown root:root "$HELPER_FILE" 2>/dev/null || true
  helper_socket_unit_text | grep -v '^$' > "$HELPER_SOCKET_FILE"; chmod 0644 "$HELPER_SOCKET_FILE"
  helper_service_unit_text | grep -v '^$' > "$HELPER_SERVICE_FILE"; chmod 0644 "$HELPER_SERVICE_FILE"
  systemctl daemon-reload
  systemctl enable "$HELPER_SOCKET_UNIT" >/dev/null 2>&1 || true
  systemctl restart "$HELPER_SOCKET_UNIT"
}

# Enrolment: with a code; without one an existing token is kept (updating needs no new code)
enrol() {
  [ -n "$SERVER" ] || return 0
  have_config=0
  if [ -f "$CONFIG_FILE" ]; then have_config=1; fi
  if [ -z "$CODE" ]; then
    if [ "$have_config" = 1 ]; then say "  already enrolled (no --code given, token kept)"; else warn "no --code given and not enrolled yet: the agent will not connect until you enrol (see docs/HOST-AGENT.md)"; fi
    return 0
  fi
  if [ "$DRY" = 1 ]; then plan "enrol this machine with the one-time code (as $RUN_USER)"; return 0; fi
  doing "enrol with the one-time code"
  if [ "$MODE" = desktop ]; then flag=--desktop; else flag=--host; fi
  http=""
  if [ "$ALLOW_HTTP" = 1 ]; then http=--allow-http; fi
  # shellcheck disable=SC2086  # $http is empty or one fixed word
  as_user env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$RUN_HOME" "$PYTHON" "$APP_DIR/julos-agent.py" $flag $http --server "$SERVER" --code "$CODE" && return 0
  if [ "$have_config" = 1 ]; then warn "enrolling failed (code used up or expired?); the earlier token is kept"; return 0; fi
  die "enrolling failed: the code is valid 10 minutes and works once; create a new one in the app"
}

# ----------------------------------------------------------------------------------------------- host: the systemd service
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
User=$RUN_USER
Group=$RUN_GROUP
$groups
ExecStart=$PYTHON $APP_DIR/julos-agent.py --host$flags
Restart=on-failure
RestartSec=10
# exit 2: not enrolled, exit 3: prerequisites missing; trying again would not help
RestartPreventExitStatus=2 3
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=$RUN_HOME $APP_DIR
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
    warn "no systemd on this machine: the service is not installed. Start the agent by hand (or from your init system) as $RUN_USER:"
    say "    su -s /bin/sh $RUN_USER -c 'HOME=$RUN_HOME $PYTHON $APP_DIR/julos-agent.py --host'"
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
  if [ ! -f "$CONFIG_FILE" ]; then
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

# ----------------------------------------------------------------------------------------------- desktop: autostart or user service, start in the session
autostart_text() {   # name (julos-agent or julos-clip)
  extra=""
  [ "$1" != julos-agent ] || extra=" --desktop"
  cat <<DESKTOP
[Desktop Entry]
Type=Application
Name=JulOS ${1#julos-}
Exec=$PYTHON "$APP_DIR/$1.py"$extra
X-GNOME-Autostart-enabled=true
NoDisplay=true
DESKTOP
}

user_unit_text() {
  escaped=$(printf '%s' "$APP_DIR/julos-agent.py" | sed 's/%/%%/g')
  cat <<UNIT
[Unit]
Description=JulOS Agent (window bar, program launcher, clipboard, system values and actions)
After=graphical-session.target

[Service]
# Without a graphical session (DISPLAY missing) the agent ends with a clear message in the log and is tried again later
ExecStart=$PYTHON "$escaped" --desktop
Restart=on-failure
RestartSec=30

[Install]
WantedBy=default.target
UNIT
}

# systemctl --user for the user (the user manager is reached through its runtime directory when root does it)
user_systemctl() {
  uid=$(id -u "$RUN_USER")
  as_user env XDG_RUNTIME_DIR="/run/user/$uid" DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$uid/bus" systemctl --user "$@"
}

write_user_file() {   # dest, content on stdin: as the user, atomic
  dest=$1
  d=$(dirname "$dest")
  as_user mkdir -p "$d"
  t=$(mktemp)
  cat > "$t"; chmod 0644 "$t"
  if [ -f "$dest" ] && cmp -s "$t" "$dest"; then say "  $dest unchanged"; rm -f "$t"; return 0; fi
  cp "$t" "$dest"; own "$dest"; rm -f "$t"
}

stop_old_agent() {
  as_user env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$RUN_HOME" "$PYTHON" "$APP_DIR/julos-agent.py" stop >/dev/null 2>&1 || true
  for name in julos-agent julos-clip; do      # older installations without a pid file, and the clipboard helper, by the exact path of this installation
    for p in $(ps -eo pid,user,args | awk -v u="$RUN_USER" -v n="python3 $APP_DIR/$name.py" '$2 == u && index($0, n) { print $1 }'); do kill "$p" 2>/dev/null || true; done
  done
}

install_desktop_start() {
  use_unit=$WANT_SYSTEMD
  if [ -n "$DESK_CONFIG_DIR" ] && [ -f "$DESK_CONFIG_DIR/systemd/user/julos-agent.service" ]; then use_unit=1; fi     # an existing user service stays one
  unit_path=$DESK_CONFIG_DIR/systemd/user/julos-agent.service
  auto_path=$DESK_CONFIG_DIR/autostart
  if [ "$use_unit" = 1 ] && [ "$SYSTEMD" = 0 ]; then
    if [ "$DRY" = 1 ]; then warn "--systemd: no systemd on this machine, an autostart entry would be used"; else warn "--systemd: no systemd on this machine, using an autostart entry"; fi
    use_unit=0
  fi
  if [ "$DRY" = 1 ]; then
    plan "write $auto_path/julos-clip.desktop (starts the clipboard helper at login)"
    if [ "$use_unit" = 1 ]; then
      plan "write $unit_path (systemd user service), remove the autostart entry of the agent; enable and start it:"
      user_unit_text | grep -v '^$' | sed 's/^/        | /'
    else
      plan "write $auto_path/julos-agent.desktop (starts the agent in the session at login: python3 $APP_DIR/julos-agent.py --desktop)"
    fi
    [ "$NO_START" = 1 ] || plan "stop an old agent of $RUN_USER, then start the new one in the current session (it also starts at the next login)"
    return 0
  fi
  doing "stop a running old agent of $RUN_USER (it is replaced)"
  stop_old_agent
  doing "write the autostart entries / user service of $RUN_USER"
  autostart_text julos-clip | write_user_file "$auto_path/julos-clip.desktop"
  if [ "$use_unit" = 1 ]; then
    rm -f "$auto_path/julos-agent.desktop"                       # do not start twice
    user_unit_text | write_user_file "$unit_path"
    user_systemctl daemon-reload || warn "the user service manager of $RUN_USER is not reachable from here (not logged in?): the unit is written and starts at the next login"
    if [ "$NO_START" = 1 ]; then user_systemctl enable julos-agent.service || true
    else user_systemctl enable --now julos-agent.service || warn "could not start the user service now: it starts at the next login"; fi
  else
    autostart_text julos-agent | write_user_file "$auto_path/julos-agent.desktop"
    if [ -f "$unit_path" ]; then
      user_systemctl disable --now julos-agent.service 2>/dev/null || true; rm -f "$unit_path"; user_systemctl daemon-reload 2>/dev/null || true
    fi
  fi
  if [ "$NO_START" = 1 ]; then say "  set up, nothing started (--no-start). Start: python3 $APP_DIR/julos-agent.py --desktop"; return 0; fi
  sleep 1
  [ "$use_unit" = 1 ] || start_in_session
  start_clip_helper
  sleep 2
  as_user env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$RUN_HOME" "$PYTHON" "$APP_DIR/julos-agent.py" status || true
}

# The agent has to start inside the graphical session: it needs DISPLAY. sudo usually keeps DISPLAY and XAUTHORITY; without them it starts at the next login.
display_for_start() {
  if [ -n "${DISPLAY:-}" ]; then printf '%s\n' "$DISPLAY"; return 0; fi
  return 1
}
start_in_session() {
  if ! disp=$(display_for_start); then
    say "  no display here (a terminal over SSH?): the agent starts at the next login of $RUN_USER in the graphical session"
    return 0
  fi
  doing "start the agent in the session $disp"
  xa=${XAUTHORITY:-$RUN_HOME/.Xauthority}
  # shellcheck disable=SC2016  # the single-quoted script is run by the user's own sh: $HOME and "$@" are meant to expand there
  as_user env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$RUN_HOME" DISPLAY="$disp" XAUTHORITY="$xa" sh -c 'cd "$HOME" && (setsid nohup "$@" >/dev/null 2>&1 </dev/null &)' sh "$PYTHON" "$APP_DIR/julos-agent.py" --desktop
}
start_clip_helper() {
  [ -f "$APP_DIR/julos-clip.py" ] || return 0
  disp=$(display_for_start) || return 0
  # shellcheck disable=SC2016  # as above
  as_user env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME="$RUN_HOME" DISPLAY="$disp" XAUTHORITY="${XAUTHORITY:-$RUN_HOME/.Xauthority}" sh -c 'cd "$HOME" && (setsid nohup "$@" >"$HOME/.local/state/julos/clip.log" 2>&1 </dev/null &)' sh "$PYTHON" "$APP_DIR/julos-clip.py"
}

# Docker for a desktop: the agent is the user's own process, so it can use the Docker socket only when the user is in the group "docker". Group membership is the user's decision.
desktop_docker_note() {
  [ "$DOCKER_GROUP" = 1 ] || has_word docker || return 0
  if id -nG "$RUN_USER" 2>/dev/null | tr ' ' '\n' | grep -qx docker; then
    say "  $RUN_USER is in the group docker: the agent can read the Docker socket (that group is root-equivalent)"
  else
    warn "$RUN_USER is not in the group docker, so the agent sees no containers. Adding the user makes the user root-equivalent on this machine; if you want that: sudo usermod -aG docker $RUN_USER, then log out and in. The installer does not change groups."
  fi
}

# ----------------------------------------------------------------------------------------------- go
install_user
install_files
install_permissions
install_helper
enrol
if [ "$MODE" = desktop ]; then
  desktop_docker_note
  install_desktop_start
else
  install_service
fi

if [ "$DRY" = 1 ]; then
  say "Dry run finished. Run it again without --dry-run (with sudo) to install."
elif [ "$MODE" = desktop ]; then
  say "Done. The host should show up as connected in the JulOS app within a few seconds (window bar, clipboard, and the host values)."
  say "Log: $RUN_HOME/.local/state/julos/agent.log   State: python3 $APP_DIR/julos-agent.py status   Remove: sudo sh install-agent.sh --uninstall"
else
  say "Done. The host should show up as connected in the JulOS app within a few seconds."
  say "Log: journalctl -u $UNIT_NAME   Remove: sh install-agent.sh --uninstall"
fi
