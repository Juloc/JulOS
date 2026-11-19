#!/bin/sh
# julos-setup-ai-cli.sh - prepare a machine for the program sessions of JulOS (Claude Code and Codex CLI over SSH).
#
# What it does (details and sources: docs/AI-CLI-SETUP.md):
#   1. checks the machine (OS, architecture, user, tools, PATH of a login shell, locale, disk, memory),
#   2. installs missing system packages (curl, git, tmux, ripgrep, gh, bubblewrap, ...) with sudo, when needed,
#   3. installs Claude Code with the native installer's own steps (manifest checksum, optional GPG signature check)
#      and the Codex CLI with OpenAI's official installer (it verifies SHA-256 itself),
#   4. puts ~/.local/bin (or --prefix) on the PATH of login shells (JulOS checks the tools with a login shell),
#   5. adds a small managed block to the tmux configuration (history, no Esc delay, mouse on and status off for julos-* sessions),
#   6. prints a summary (ready / missing) and the exact sign-in commands.
#
# It never reads, writes or asks for passwords, keys or tokens. Signing in is done by you, by hand, afterwards. sudo asks for
# its own password in your terminal when a system package or a new user is needed.
#
# Usage:  sh julos-setup-ai-cli.sh [options]
#   (default)        dry run: only show what would happen (reads the machine, changes nothing)
#   --yes            really do it
#   --dry-run        explicit dry run (the default)
#   --claude         Claude Code only         --codex   Codex CLI only         --all   both (default)
#   --user NAME      set up the account NAME (created with sudo if it does not exist, locked password; set one yourself with
#                    "sudo passwd NAME"). Without it: the account that runs the script (refused for root)
#   --tmux           install tmux and the tmux block (default)        --no-tmux   leave tmux alone
#   --no-extras      skip the optional packages (ripgrep, gh, bubblewrap, socat, gnupg)
#   --prefix DIR     install the tools below DIR/bin (default: ~/.local, where the Claude installer puts its launcher)
#   --no-gpg         do not verify the GPG signature of the Claude manifest (the checksum is still verified)
#   --check-network  also test that the needed hosts are reachable (a few HEAD requests)
#   --help, --version
#
# Idempotent: run it again at any time. Present tools are left alone (update them with "claude update" and "codex update").
# Supported: Debian/Ubuntu (apt), Fedora/RHEL (dnf), Alpine (apk), macOS (Homebrew must exist already), x86_64 and arm64.
# POSIX sh; checked with "shellcheck -s sh".

set -eu

SCRIPT_VERSION=1
CLAUDE_BASE=https://downloads.claude.ai/claude-code-releases
CLAUDE_KEY_URL=https://downloads.claude.ai/keys/claude-code.asc
# Fingerprint of the Claude Code release signing key as published in the official setup documentation
CLAUDE_KEY_FPR=31DDDE24DDFAB679F42D7BD2BAA929FF1A7ECACE
CODEX_INSTALL_URL=https://chatgpt.com/codex/install.sh
MARK_BEGIN='# >>> julos ai-cli >>>'
MARK_END='# <<< julos ai-cli <<<'

APPLY=0 WANT_CLAUDE=0 WANT_CODEX=0 SEL=0 TARGET_USER="" PREFIX="" TMUX_ON=1 EXTRAS=1 GPG=1 NET=0 PHASE_USER=0
TMPDIRS=""

say() { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
plan() { printf '  [would] %s\n' "$*"; }
doing() { printf '  [do]    %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

# shellcheck disable=SC2317  # called through the trap
cleanup() {
  for d in $TMPDIRS; do rm -rf "$d"; done
}
trap cleanup EXIT INT TERM

MKTMP=""
mktmp() {  # sets MKTMP (not printed: a command substitution would lose the cleanup list)
  MKTMP=$(mktemp -d "${TMPDIR:-/tmp}/julos-ai.XXXXXX") || die "cannot create a temporary directory"
  TMPDIRS="$TMPDIRS $MKTMP"
}

usage() { sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//'; }

# ---------------------------------------------------------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------------------------------------------------------
while [ $# -gt 0 ]; do
  case "$1" in
    --yes|-y) APPLY=1 ;;
    --dry-run|-n) APPLY=0 ;;
    --claude) WANT_CLAUDE=1; SEL=1 ;;
    --codex) WANT_CODEX=1; SEL=1 ;;
    --all) WANT_CLAUDE=1; WANT_CODEX=1; SEL=1 ;;
    --user) [ $# -ge 2 ] || die "--user needs a name"; TARGET_USER=$2; shift ;;
    --prefix) [ $# -ge 2 ] || die "--prefix needs a directory"; PREFIX=$2; shift ;;
    --tmux) TMUX_ON=1 ;;
    --no-tmux) TMUX_ON=0 ;;
    --no-extras) EXTRAS=0 ;;
    --no-gpg) GPG=0 ;;
    --check-network) NET=1 ;;
    --phase-user) PHASE_USER=1 ;;   # internal: the second half, run as the target account
    --help|-h) usage; exit 0 ;;
    --version) say "julos-setup-ai-cli $SCRIPT_VERSION"; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
  shift
done
if [ "$SEL" = 0 ]; then WANT_CLAUDE=1; WANT_CODEX=1; fi

case "$TARGET_USER" in
  "") ;;
  *[!a-z0-9_-]*|-*|[0-9]*) die "--user: use lowercase letters, digits, _ and - (not starting with a digit or -)" ;;
esac
case "$PREFIX" in
  "") ;;
  /*) ;;
  *) die "--prefix must be an absolute path" ;;
esac
case "$PREFIX" in
  *[!A-Za-z0-9._/+@%:,=-]*) die "--prefix: only letters, digits and . _ / + @ % : , = - are allowed" ;;
esac

# ---------------------------------------------------------------------------------------------------------------------------
# System detection
# ---------------------------------------------------------------------------------------------------------------------------
OS=$(uname -s)
ARCH_RAW=$(uname -m)
case "$ARCH_RAW" in
  x86_64|amd64) ARCH=x64 ;;
  arm64|aarch64) ARCH=arm64 ;;
  *) die "unsupported architecture: $ARCH_RAW (Claude Code and Codex need x86_64 or arm64)" ;;
esac
CUR_USER=$(id -un)
CUR_UID=$(id -u)
DISTRO=unknown
LIBC=glibc
PKG=""
case "$OS" in
  Darwin) DISTRO=macos; PKG=brew ;;
  Linux)
    # read in subshells: /etc/os-release sets VERSION and other names that must not leak into this script
    ID=""; ID_LIKE=""
    if [ -r /etc/os-release ]; then
      # shellcheck disable=SC1091
      ID=$(. /etc/os-release && printf '%s' "${ID:-}")
      # shellcheck disable=SC1091
      ID_LIKE=$(. /etc/os-release && printf '%s' "${ID_LIKE:-}")
    fi
    DISTRO="${ID:-linux}"
    case " $ID $ID_LIKE " in
      *" debian "*|*" ubuntu "*) PKG=apt ;;
      *" fedora "*|*" rhel "*|*" centos "*) PKG=dnf ;;
      *" alpine "*) PKG=apk ;;
    esac
    if [ -e /lib/libc.musl-x86_64.so.1 ] || [ -e /lib/libc.musl-aarch64.so.1 ]; then LIBC=musl; fi
    ;;
  *) die "unsupported system: $OS (Linux and macOS only; on Windows use WSL)" ;;
esac
if [ "$OS" = Linux ] && [ "$LIBC" = musl ]; then PLATFORM="linux-$ARCH-musl"
elif [ "$OS" = Linux ]; then PLATFORM="linux-$ARCH"
else PLATFORM="darwin-$ARCH"; fi

# Who gets the tools
if [ -z "$TARGET_USER" ]; then
  [ "$CUR_UID" != 0 ] || die "running as root: pass --user NAME (an unprivileged account, for example --user ai). The tools should not live in root's home."
  TARGET_USER=$CUR_USER
fi
SUDO=""
if [ "$CUR_UID" != 0 ]; then
  if have sudo; then SUDO=sudo; else SUDO=none; fi
fi

user_home() {  # $1 = account name; prints its home directory, empty if the account does not exist
  if have getent; then getent passwd "$1" | cut -d: -f6
  elif [ "$OS" = Darwin ]; then dscl . -read "/Users/$1" NFSHomeDirectory 2>/dev/null | sed 's/^NFSHomeDirectory: //'
  else grep "^$1:" /etc/passwd 2>/dev/null | cut -d: -f6; fi
}

as_root() {  # run a command with root rights (sudo asks for its own password on the terminal; this script never sees it)
  if [ "$CUR_UID" = 0 ]; then "$@"
  elif [ "$SUDO" = sudo ]; then sudo "$@"
  else die "root rights are needed for: $* (no sudo here: run this command as root yourself)"; fi
}

# ---------------------------------------------------------------------------------------------------------------------------
# Packages
# ---------------------------------------------------------------------------------------------------------------------------
# logical name -> binary to look for; package name per manager
pkg_name() {
  case "$1:$PKG" in
    gh:apk) echo github-cli ;;
    gnupg:dnf) echo gnupg2 ;;
    gnupg:brew) echo gnupg ;;
    *) echo "$1" ;;
  esac
}
bin_of() {
  case "$1" in
    ripgrep) echo rg ;;
    bubblewrap) echo bwrap ;;
    gnupg) echo gpg ;;
    *) echo "$1" ;;
  esac
}

MISSING_CORE="" MISSING_EXTRA=""
scan_packages() {
  for p in curl tar git; do have "$(bin_of "$p")" || MISSING_CORE="$MISSING_CORE $p"; done
  if [ "$OS" = Linux ] && [ ! -e /etc/ssl/certs/ca-certificates.crt ] && [ ! -e /etc/pki/tls/certs/ca-bundle.crt ] && [ ! -e /etc/ssl/cert.pem ]; then
    MISSING_CORE="$MISSING_CORE ca-certificates"
  fi
  if [ "$PKG" = apk ]; then
    have bash || MISSING_CORE="$MISSING_CORE bash"      # Claude Code needs bash or zsh; Alpine has neither by default
    [ -e /usr/lib/libstdc++.so.6 ] || MISSING_CORE="$MISSING_CORE libstdc++ libgcc"
  fi
  if [ "$TMUX_ON" = 1 ]; then have tmux || MISSING_CORE="$MISSING_CORE tmux"; fi
  if [ "$EXTRAS" = 1 ]; then
    for p in ripgrep gh gnupg; do have "$(bin_of "$p")" || MISSING_EXTRA="$MISSING_EXTRA $p"; done
    if [ "$OS" = Linux ]; then
      for p in bubblewrap socat; do have "$(bin_of "$p")" || MISSING_EXTRA="$MISSING_EXTRA $p"; done
    fi
  fi
}

map_list() {  # translate logical names to package names for this manager
  out=""
  for p in "$@"; do out="$out $(pkg_name "$p")"; done
  printf '%s' "$out"
}

install_packages() {
  [ -n "$MISSING_CORE$MISSING_EXTRA" ] || { say "  system packages: nothing to install"; return 0; }
  if [ "$PKG" = "" ]; then
    warn "unknown package manager on '$DISTRO': install these yourself:$MISSING_CORE$MISSING_EXTRA"
    return 0
  fi
  # shellcheck disable=SC2086
  core=$(map_list $MISSING_CORE)
  # shellcheck disable=SC2086
  extra=$(map_list $MISSING_EXTRA)
  case "$PKG" in
    apt) install_cmd="apt-get install -y -qq --no-install-recommends"; update_cmd="apt-get update -qq" ;;
    dnf) install_cmd="dnf install -y"; update_cmd="" ;;
    apk) install_cmd="apk add --no-cache"; update_cmd="" ;;
    brew) install_cmd="brew install"; update_cmd="" ;;
  esac
  if [ "$PKG" = brew ]; then
    have brew || { warn "Homebrew is not installed (https://brew.sh): install it yourself, then run this script again"; return 0; }
    [ "$CUR_UID" != 0 ] || die "Homebrew refuses root: run as your normal user"
    prefix_cmd=""
  elif [ "$CUR_UID" = 0 ]; then prefix_cmd=""
  elif [ "$SUDO" = sudo ]; then prefix_cmd="sudo "
  else prefix_cmd="(as root) "; fi
  if [ "$APPLY" = 0 ]; then
    [ -z "$update_cmd" ] || plan "${prefix_cmd}$update_cmd"
    [ -z "$core" ] || plan "${prefix_cmd}$install_cmd$core"
    for p in $extra; do plan "${prefix_cmd}$install_cmd $p   (optional: a failure here is only a warning)"; done
    return 0
  fi
  # A Debian-like package manager asks nothing with these settings; sudo asks for its own password on your terminal.
  DEBIAN_FRONTEND=noninteractive
  export DEBIAN_FRONTEND
  if [ "$PKG" = brew ]; then runner=""; else runner="as_root"; fi
  if [ -n "$update_cmd" ]; then
    doing "$prefix_cmd$update_cmd"
    # shellcheck disable=SC2086
    $runner $update_cmd || warn "package list update failed, continuing"
  fi
  if [ -n "$core" ]; then
    doing "$prefix_cmd$install_cmd$core"
    # shellcheck disable=SC2086
    $runner $install_cmd $core || die "installing$core failed"
  fi
  for p in $extra; do
    doing "$prefix_cmd$install_cmd $p"
    # shellcheck disable=SC2086
    $runner $install_cmd $p || warn "optional package $p could not be installed (continuing)"
  done
}

# ---------------------------------------------------------------------------------------------------------------------------
# Account
# ---------------------------------------------------------------------------------------------------------------------------
ensure_user() {
  [ "$TARGET_USER" != "$CUR_USER" ] || return 0
  if [ -n "$(user_home "$TARGET_USER")" ]; then say "  account $TARGET_USER: exists"; return 0; fi
  [ "$OS" = Linux ] || die "account $TARGET_USER does not exist; create it in System Settings (macOS) and run this script again"
  if [ "$PKG" = apk ]; then cmd="adduser -D -s /bin/bash $TARGET_USER"; else cmd="useradd --create-home --shell /bin/bash $TARGET_USER"; fi
  if [ "$APPLY" = 0 ]; then
    plan "create the account: ${SUDO:+$SUDO }$cmd   (password stays locked; set it yourself afterwards with: sudo passwd $TARGET_USER)"
    return 0
  fi
  doing "${SUDO:+$SUDO }$cmd"
  # shellcheck disable=SC2086
  as_root $cmd || die "could not create the account $TARGET_USER"
  # Alpine's adduser -D leaves the account locked on purpose; Debian's useradd without a password too.
  say "  account $TARGET_USER created with a locked password. Set one yourself:  sudo passwd $TARGET_USER"
}

# ---------------------------------------------------------------------------------------------------------------------------
# User phase (runs as the target account, HOME is the target's)
# ---------------------------------------------------------------------------------------------------------------------------
fetch() {  # $1 = url, $2 = output file
  if have curl; then curl -fsSL --retry 2 --connect-timeout 15 -o "$2" "$1"
  elif have wget; then wget -q -O "$2" "$1"
  else die "neither curl nor wget is available"; fi
}
sha256_of() {
  if have sha256sum; then sha256sum "$1" | cut -d' ' -f1
  elif have shasum; then shasum -a 256 "$1" | cut -d' ' -f1
  elif have openssl; then openssl dgst -sha256 "$1" | sed 's/^.*= //'
  else die "no sha256 tool (sha256sum, shasum or openssl) available"; fi
}
manifest_checksum() {  # $1 = manifest.json, $2 = platform name; prints the 64 hex digits
  tr -d ' \n\r\t' < "$1" | sed -n "s/.*\"$2\":{[^}]*\"checksum\":\"\([0-9a-f]\{64\}\)\".*/\1/p"
}

login_path_has() {  # is "$1" found by a login shell of this account? (this is how the JulOS check looks)
  sh_bin=${SHELL:-/bin/sh}
  [ -x "$sh_bin" ] || sh_bin=/bin/sh
  "$sh_bin" -lc "command -v $1" 2>/dev/null | tail -n 1
}

tool_version() {  # $1 = path to the program
  "$1" --version 2>/dev/null | head -n 1 || true
}

verify_manifest_signature() {  # $1 = dir holding manifest.json and manifest.json.sig, $2 = version
  if [ "$GPG" = 0 ]; then say "  GPG signature check skipped (--no-gpg); SHA-256 is still verified"; return 0; fi
  if ! have gpg; then
    warn "gpg is not installed: the manifest signature is not checked (only HTTPS plus SHA-256). Install gnupg and run again to verify."
    return 0
  fi
  gh_dir="$1/gnupg"
  mkdir -m 700 "$gh_dir"
  fetch "$CLAUDE_KEY_URL" "$1/claude-code.asc" || die "could not download the Claude Code release key"
  fetch "$CLAUDE_BASE/$2/manifest.json.sig" "$1/manifest.json.sig" || die "no signature for the manifest of $2 (use --no-gpg to skip the signature check)"
  GNUPGHOME=$gh_dir gpg --batch --quiet --import "$1/claude-code.asc" 2>/dev/null || die "could not import the release key"
  fpr=$(GNUPGHOME=$gh_dir gpg --batch --with-colons --fingerprint 2>/dev/null | sed -n 's/^fpr:::::::::\([0-9A-F]*\):$/\1/p' | tr '\n' ' ')
  case " $fpr" in *" $CLAUDE_KEY_FPR "*) ;; *) die "the downloaded release key has an unexpected fingerprint ($fpr): not trusting it" ;; esac
  GNUPGHOME=$gh_dir gpg --batch --quiet --verify "$1/manifest.json.sig" "$1/manifest.json" 2>/dev/null || die "the GPG signature of the manifest is invalid"
  say "  GPG signature of the manifest verified (key $CLAUDE_KEY_FPR)"
}

install_claude() {
  if [ -x "$BIN/claude" ] || [ -x "$HOME/.local/bin/claude" ]; then
    p=$BIN/claude; [ -x "$p" ] || p=$HOME/.local/bin/claude
    say "  claude: present ($(tool_version "$p")); update it with: claude update"
    return 0
  fi
  if [ "$APPLY" = 0 ]; then
    gpg_note=""
    [ "$GPG" = 0 ] || gpg_note=" and its GPG signature"
    plan "download the Claude Code binary for $PLATFORM from $CLAUDE_BASE (latest), verify SHA-256 from manifest.json$gpg_note, run its own 'install' step (launcher in ~/.local/bin/claude, versions in ~/.local/share/claude)"
    return 0
  fi
  mem=$(awk '/^MemAvailable:/ {print int($2/1024)}' /proc/meminfo 2>/dev/null || true)
  if [ -n "${mem:-}" ] && [ "$mem" -lt 600 ]; then warn "only ${mem} MB of memory available: the Claude installer needs about 512 MB (add swap if it gets killed)"; fi
  mktmp; work=$MKTMP
  chmod 700 "$work"
  doing "downloading the Claude Code manifest ($PLATFORM)"
  ver=$(curl -fsSL "$CLAUDE_BASE/latest" 2>/dev/null || wget -q -O - "$CLAUDE_BASE/latest")
  case "$ver" in [0-9]*.[0-9]*.[0-9]*) ;; *) die "unexpected version string from $CLAUDE_BASE/latest" ;; esac
  fetch "$CLAUDE_BASE/$ver/manifest.json" "$work/manifest.json" || die "could not download the manifest"
  verify_manifest_signature "$work" "$ver"
  want=$(manifest_checksum "$work/manifest.json" "$PLATFORM")
  [ -n "$want" ] || die "no checksum for $PLATFORM in the manifest of $ver"
  doing "downloading claude $ver for $PLATFORM (several hundred MB)"
  fetch "$CLAUDE_BASE/$ver/$PLATFORM/claude" "$work/claude" || die "download failed"
  got=$(sha256_of "$work/claude")
  [ "$got" = "$want" ] || die "SHA-256 mismatch for claude $ver (expected $want, got $got): not installing"
  say "  SHA-256 verified: $got"
  chmod +x "$work/claude"
  doing "claude install (sets up the launcher)"
  "$work/claude" install || die "the Claude Code install step failed"
  if [ "$BIN" != "$HOME/.local/bin" ] && [ -x "$HOME/.local/bin/claude" ]; then
    mkdir -p "$BIN"
    ln -sf "$HOME/.local/bin/claude" "$BIN/claude"
    say "  linked $BIN/claude -> ~/.local/bin/claude"
  fi
}

install_codex() {
  if [ -x "$BIN/codex" ] || [ -x "$HOME/.local/bin/codex" ]; then
    p=$BIN/codex; [ -x "$p" ] || p=$HOME/.local/bin/codex
    say "  codex: present ($(tool_version "$p")); update it with: codex update"
    return 0
  fi
  if [ "$APPLY" = 0 ]; then
    plan "download OpenAI's installer from $CODEX_INSTALL_URL and run it non-interactively (it verifies SHA-256 and installs $BIN/codex and ~/.codex/packages)"
    return 0
  fi
  mktmp; work=$MKTMP
  chmod 700 "$work"
  doing "downloading the Codex installer"
  fetch "$CODEX_INSTALL_URL" "$work/install.sh" || die "could not download the Codex installer"
  head -n 1 "$work/install.sh" | grep -q '^#!/bin/sh' || die "the Codex installer does not look like a shell script: not running it"
  mkdir -p "$BIN"
  doing "running the Codex installer into $BIN"
  CODEX_NON_INTERACTIVE=1 CODEX_INSTALL_DIR=$BIN sh "$work/install.sh" || die "the Codex installer failed"
}

write_block() {  # $1 = file, $2 = block text (without markers); replaces or appends the managed block, keeps the rest
  f=$1
  mktmp; new=$MKTMP/new
  if [ -f "$f" ]; then
    sed "/^$MARK_BEGIN\$/,/^$MARK_END\$/d" "$f" > "$new"
  else
    : > "$new"
  fi
  { cat "$new"; printf '%s\n%s\n%s\n' "$MARK_BEGIN" "$2" "$MARK_END"; } > "$new.2"
  if [ -f "$f" ] && cmp -s "$new.2" "$f"; then
    say "  $f: already up to date"
    return 0
  fi
  if [ "$APPLY" = 0 ]; then plan "write the managed block to $f"; return 0; fi
  doing "writing the managed block to $f"
  mkdir -p "$(dirname "$f")"
  cat "$new.2" > "$f"
}

setup_path() {
  case "$BIN" in
    "$HOME"/*) shown="\$HOME/${BIN#"$HOME"/}" ;;
    *) shown=$BIN ;;
  esac
  block="case \":\$PATH:\" in *\":$shown:\"*) ;; *) PATH=\"$shown:\$PATH\"; export PATH ;; esac"
  write_block "$HOME/.profile" "$block"
  sh_name=$(basename "${SHELL:-sh}")
  case "$sh_name" in
    zsh) write_block "$HOME/.zprofile" "$block" ;;
    bash)
      for f in "$HOME/.bash_profile" "$HOME/.bash_login"; do   # bash reads the first of these INSTEAD of ~/.profile
        if [ -f "$f" ]; then write_block "$f" "$block"; break; fi
      done
      ;;
    fish) warn "login shell is fish: add $BIN to its PATH yourself (fish_add_path $BIN); the JulOS check uses a login shell" ;;
  esac
}

setup_tmux() {
  [ "$TMUX_ON" = 1 ] || return 0
  conf=$HOME/.tmux.conf
  if [ ! -f "$conf" ] && [ -f "${XDG_CONFIG_HOME:-$HOME/.config}/tmux/tmux.conf" ]; then conf=${XDG_CONFIG_HOME:-$HOME/.config}/tmux/tmux.conf; fi
  # The JulOS server sets mouse and status per session itself (set-option ... on julos-* sessions); the hook repeats it for
  # sessions you start by hand with such a name. Other sessions keep their settings. History and Esc delay are global.
  tmux_block="# managed by julos-setup-ai-cli.sh: change things outside this block
set -g history-limit 50000
set -sg escape-time 10
set -g focus-events on
set-hook -g after-new-session 'if-shell -F \"#{m:julos-*,#{session_name}}\" \"set-option mouse on ; set-option status off\"'"
  write_block "$conf" "$tmux_block"
}

tighten_config_dirs() {  # modes only; never touches the content
  for d in "$HOME/.claude" "$HOME/.codex"; do
    [ -d "$d" ] || continue
    mode=$(stat -c '%a' "$d" 2>/dev/null || stat -f '%Lp' "$d" 2>/dev/null || echo "")
    case "$mode" in 700|"") ;; *)
      if [ "$APPLY" = 0 ]; then plan "chmod 700 $d (now $mode; it holds sign-in data)"; else doing "chmod 700 $d (was $mode)"; chmod 700 "$d"; fi ;;
    esac
  done
}

mem_mb() {
  if [ -r /proc/meminfo ]; then awk '/^MemTotal:/ {print int($2/1024)}' /proc/meminfo
  elif have sysctl; then echo $(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1048576 ))
  else echo 0; fi
}

SUMMARY=""
note() {  # status text
  SUMMARY="$SUMMARY
  $(printf '%-10s %s' "$1" "$2")"
}
NOT_READY=0

summarize() {
  SUMMARY=""
  for t in claude codex; do
    want=$WANT_CLAUDE; [ "$t" = codex ] && want=$WANT_CODEX
    [ "$want" = 1 ] || continue
    p=$(login_path_has "$t" || true)
    if [ -n "$p" ]; then note "[ready]" "$t: $p ($(tool_version "$p"))"
    elif [ -x "$BIN/$t" ] || [ -x "$HOME/.local/bin/$t" ]; then
      q=$BIN/$t; [ -x "$q" ] || q=$HOME/.local/bin/$t
      note "[PATH]" "$t: installed at $q, but a login shell does not find it (open a new login shell; JulOS checks with one)"; NOT_READY=1
    else note "[missing]" "$t: not installed"; NOT_READY=1; fi
  done
  if [ "$TMUX_ON" = 1 ]; then
    if have tmux; then
      blocked=""; grep -qs "$MARK_BEGIN" "$HOME/.tmux.conf" "${XDG_CONFIG_HOME:-$HOME/.config}/tmux/tmux.conf" 2>/dev/null || blocked=" (JulOS block not in the tmux config yet)"
      note "[ready]" "tmux: $(tmux -V 2>/dev/null)$blocked"
    else note "[missing]" "tmux: not installed (sessions will not survive a dropped connection)"; NOT_READY=1; fi
  fi
  for t in git curl; do
    if have "$t"; then note "[ready]" "$t"; else note "[missing]" "$t"; NOT_READY=1; fi
  done
  for t in rg gh bwrap gpg; do
    if have "$t"; then note "[ready]" "$t (optional)"; else note "[info]" "$t not installed (optional)"; fi
  done
  case "${LC_ALL:-${LC_CTYPE:-${LANG:-}}}" in
    *[Uu][Tt][Ff]-8*|*[Uu][Tt][Ff]8*) note "[ready]" "locale: ${LC_ALL:-${LC_CTYPE:-$LANG}}" ;;
    *) note "[warn]" "locale is not UTF-8 (LANG=${LANG:-unset}): the terminal may show broken characters. Fix: export LANG=C.UTF-8 in ~/.profile" ;;
  esac
  tz=${TZ:-}
  [ -z "$tz" ] && [ -r /etc/timezone ] && tz=$(cat /etc/timezone)
  [ -z "$tz" ] && [ -L /etc/localtime ] && tz=$(readlink /etc/localtime | sed 's|.*/zoneinfo/||')
  note "[info]" "time zone: ${tz:-unknown} (session logs and 'claude --resume' use local time)"
  free=$(df -Pk "$HOME" 2>/dev/null | awk 'NR==2 {print int($4/1048576)}')
  if [ -n "$free" ] && [ "$free" -lt 3 ]; then note "[warn]" "only ${free} GB free in $HOME (Claude needs ~0.4 GB per version, Codex ~0.1 GB, plus work files)"; else note "[ready]" "disk: ${free:-?} GB free in $HOME"; fi
  m=$(mem_mb)
  if [ "$m" -gt 0 ] && [ "$m" -lt 3500 ]; then note "[warn]" "memory ${m} MB: Claude Code officially needs 4 GB or more"; else note "[ready]" "memory: ${m} MB"; fi
  # Sign-in state without reading any secret: only whether the usual files exist
  if [ -f "$HOME/.claude/.credentials.json" ]; then note "[ready]" "claude: sign-in file present (verify: claude auth status)"
  elif [ "$WANT_CLAUDE" = 1 ]; then note "[todo]" "claude: not signed in yet"; fi
  if [ -f "$HOME/.codex/auth.json" ]; then note "[ready]" "codex: sign-in file present (verify: codex login status)"
  elif [ "$WANT_CODEX" = 1 ]; then note "[todo]" "codex: not signed in yet"; fi
  printf '%s\n' "$SUMMARY"
}

check_network() {
  [ "$NET" = 1 ] || return 0
  say "Network (HEAD requests, any HTTP answer counts as reachable):"
  have curl || { warn "curl is missing, skipping the network check"; return 0; }
  for u in "$CLAUDE_BASE/latest" https://api.anthropic.com https://claude.ai https://platform.claude.com \
           https://releases.openai.com/codex/install.sh https://api.openai.com https://auth.openai.com https://chatgpt.com https://github.com; do
    code=$(curl -s -o /dev/null -I -m 8 -w '%{http_code}' "$u" 2>/dev/null || true)
    case "$code" in 000|"") printf '  [blocked]  %s\n' "$u" ;; *) printf '  [reachable] %s (HTTP %s)\n' "$u" "$code" ;; esac
  done
}

next_steps() {
  cat <<EOF

Next steps (you do these by hand; nothing here is stored by this script or by JulOS):
EOF
  n=1
  if [ "$WANT_CLAUDE" = 1 ]; then
    cat <<EOF
  $n. Sign in to Claude Code (a browser on another device is fine, for example your phone):
       claude auth login
     The terminal prints a login URL. Open it on any device, sign in, and paste the code the page shows into the terminal at
     the prompt "Paste code here". (In an interactive session the same is: claude, then /login.)
     Check:  claude auth status
EOF
    n=$((n + 1))
  fi
  if [ "$WANT_CODEX" = 1 ]; then
    cat <<EOF
  $n. Sign in to Codex with your ChatGPT account (device code, no browser needed on this machine):
       codex login --device-auth
     Open the printed URL on any device and enter the printed code. (Device code login must be enabled in your ChatGPT
     security or workspace settings.) Check:  codex login status
EOF
    n=$((n + 1))
  fi
  cat <<EOF
  $n. In JulOS: add a connection of type Terminal (SSH) to this machine as $TARGET_USER, choose the program "Claude Code" or
     "Codex", set the working directory (a project folder, not your whole home) and switch "keep the session alive (tmux)" on.
     Keep the permission prompts of both tools on their defaults; see docs/AI-CLI-SETUP.md for what is safe on a home server.
EOF
  if [ "$TARGET_USER" != "$CUR_USER" ] && [ "$APPLY" = 1 ]; then
    cat <<EOF
  *. The account $TARGET_USER has a locked password. Set one yourself:  sudo passwd $TARGET_USER
EOF
  fi
}

user_phase() {
  HOME_T=${HOME:?}
  if [ -n "$PREFIX" ]; then BIN=$PREFIX/bin; else BIN=$HOME_T/.local/bin; fi
  say "Tools for $(id -un) (home $HOME_T, bin $BIN):"
  [ "$WANT_CLAUDE" = 0 ] || install_claude
  [ "$WANT_CODEX" = 0 ] || install_codex
  setup_path
  setup_tmux
  tighten_config_dirs
  say ""
  say "Summary for $(id -un):"
  PATH_SAVE=$PATH
  PATH=$BIN:$PATH
  summarize
  PATH=$PATH_SAVE
}

# ---------------------------------------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------------------------------------
if [ "$PHASE_USER" = 1 ]; then
  user_phase
  [ "$NOT_READY" = 0 ] || exit 3
  exit 0
fi

mode="DRY RUN (nothing is changed; add --yes to do it)"
[ "$APPLY" = 0 ] || mode="APPLY"
say "julos-setup-ai-cli $SCRIPT_VERSION: $mode"
say "System: $DISTRO ($OS $ARCH_RAW, $LIBC), package manager: ${PKG:-none known}, running as $CUR_USER, target account: $TARGET_USER"
tools=""
[ "$WANT_CLAUDE" = 0 ] || tools="claude"
[ "$WANT_CODEX" = 0 ] || tools="$tools codex"
onoff() { if [ "$1" = 1 ]; then printf on; else printf off; fi; }
say "Tools:$tools | tmux: $(onoff "$TMUX_ON") | extras: $(onoff "$EXTRAS")"
say ""
say "System packages:"
scan_packages
if [ -n "$MISSING_CORE$MISSING_EXTRA" ] && [ "$SUDO" = none ]; then
  warn "no sudo on this machine: ask someone with root to install:$MISSING_CORE$MISSING_EXTRA (names for your package manager)"
  [ "$APPLY" = 0 ] || die "cannot install packages without sudo or root"
fi
install_packages
say ""
say "Account:"
if [ "$TARGET_USER" = "$CUR_USER" ]; then say "  using the current account ($CUR_USER)"; else ensure_user; fi
say ""
if [ "$TARGET_USER" = "$CUR_USER" ]; then
  user_phase
  rc=0; [ "$NOT_READY" = 0 ] || rc=3
else
  if [ "$APPLY" = 0 ]; then
    if [ -z "$(user_home "$TARGET_USER")" ]; then
      say "Then, as $TARGET_USER (account does not exist yet, so nothing can be probed):"
    else
      say "Then, as $TARGET_USER:"
    fi
    [ "$WANT_CLAUDE" = 0 ] || plan "install Claude Code (checksum, GPG signature) into ~$TARGET_USER/.local"
    [ "$WANT_CODEX" = 0 ] || plan "install the Codex CLI with the official installer into ~$TARGET_USER/.local/bin"
    plan "PATH block in the login shell files, tmux block, tighten ~/.claude and ~/.codex to mode 700"
    rc=0
  else
    [ -r "$0" ] || die "to set up another account the script must be run from a file (download it first, do not pipe it into sh)"
    mktmp; copy=$MKTMP
    chmod 755 "$copy"
    cp "$0" "$copy/setup.sh"
    chmod 644 "$copy/setup.sh"
    set -- --phase-user --yes
    [ "$WANT_CLAUDE" = 0 ] || set -- "$@" --claude
    [ "$WANT_CODEX" = 0 ] || set -- "$@" --codex
    [ "$TMUX_ON" = 1 ] || set -- "$@" --no-tmux
    [ "$EXTRAS" = 1 ] || set -- "$@" --no-extras
    [ "$GPG" = 1 ] || set -- "$@" --no-gpg
    [ -z "$PREFIX" ] || set -- "$@" --prefix "$PREFIX"
    rc=0
    if [ "$CUR_UID" = 0 ]; then
      # root: su keeps no environment of ours and sets HOME of the target; the arguments are simple tokens checked above
      su -s /bin/sh "$TARGET_USER" -c "sh '$copy/setup.sh' $*" || rc=$?
    else
      as_root sudo -u "$TARGET_USER" -H sh "$copy/setup.sh" "$@" || rc=$?
    fi
    [ "$rc" = 0 ] || [ "$rc" = 3 ] || die "the setup as $TARGET_USER failed (exit $rc)"
  fi
fi
check_network
next_steps
if [ "$APPLY" = 0 ]; then
  say ""
  say "This was a dry run: nothing was changed. Entries marked [missing] are what a run with --yes would install or set up."
  say "Run again with --yes to do it."
fi
exit "$rc"
