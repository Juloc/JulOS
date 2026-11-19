# Claude Code and Codex CLI on your own machines: requirements, setup, safe use

Short and practical. This page answers: what does a machine need so that a JulOS **program session** ([CLI-SESSIONS.md](CLI-SESSIONS.md)) can start
`claude` (Claude Code) or `codex` (OpenAI Codex CLI), how do you sign in on a machine that has no browser, what is safe to allow on a home server, and
what JulOS should build to make all of it easier (section "JulOS support", a plan, nothing of it is built yet).

Which of your machines should get the tools, with the exact commands per machine: [AI-CLI-DEVICES.md](AI-CLI-DEVICES.md).
The setup script that does the boring part: [`tools/julos-setup-ai-cli.sh`](../tools/julos-setup-ai-cli.sh).

Facts below were read from the vendors' documentation on 2026-11-19 (sources at the end) and, where noted, from the tools' own `--help` on a
machine that has them. Both tools change quickly: **check the page of the tool when something here no longer matches.**

## 1. What a machine needs

| | Claude Code | Codex CLI |
|---|---|---|
| Operating system | macOS 13+, Ubuntu 20.04+, Debian 10+, Alpine 3.19+, Windows 10 1809+ (use WSL for the sandbox) | macOS, Linux, Windows (install script for macOS/Linux, a separate one for Windows) |
| Architecture | x86_64 or arm64 | x86_64 or arm64 (Linux builds are static musl binaries) |
| Memory | 4 GB or more; the installer needs about 512 MB free (OOM-killed on tiny VPS: add swap) | not stated; plan for 2 GB or more |
| Node.js | **not needed** for the native install (a single binary). Only the npm package needs Node 22+ (`npm install -g @anthropic-ai/claude-code`) | **not needed** for the install script or the release binary. The npm package exists (`npm install -g @openai/codex`) |
| Recommended install | native installer, auto-updates in the background | OpenAI's install script (SHA-256 verified), update by `codex update` or re-running the script |
| Where it lands | launcher `~/.local/bin/claude`, versions in `~/.local/share/claude/versions/` | `~/.local/bin/codex`, versions under `~/.codex/packages/` |
| Sign-in data | `~/.claude/.credentials.json` (Linux, mode 0600; macOS: Keychain) | `~/.codex/auth.json` (or the OS keyring, setting `cli_auth_credentials_store`) |
| Config | `~/.claude/settings.json`, project `.claude/` | `~/.codex/config.toml`, project `.codex/config.toml` (only for trusted projects) |
| Instruction file | `CLAUDE.md` (project root, and `~/.claude/CLAUDE.md`) | `AGENTS.md` (project root, and `~/.codex/AGENTS.md`) |
| Network (outbound HTTPS only) | `api.anthropic.com`, `claude.ai`, `platform.claude.com`, `downloads.claude.ai` (install and updates), optional `registry.npmjs.org`, `github.com` for plugins | `chatgpt.com`, `auth.openai.com`, `api.openai.com`, `releases.openai.com` (install; falls back to GitHub releases). HTTP proxy variables are honored (`HTTPS_PROXY`); a SOCKS proxy is not supported by Claude Code |
| Account | Pro, Max, Ultra, Team or Enterprise plan, or a Console (API) account. **The free plan has no Claude Code access.** | ChatGPT plan (Plus, Pro, Business, Edu, Enterprise) or an API key |

Also on the machine, the same for both tools:

- **A login shell that finds the tool.** JulOS checks `command -v claude` first in the plain SSH exec channel and then in `$SHELL -lc` (see CLI-SESSIONS.md).
  `~/.local/bin` must be on the `PATH` of a **login** shell. The script writes a small block to `~/.profile` (and to `~/.zprofile` for zsh, or to
  `~/.bash_profile` when that file exists, because bash then ignores `~/.profile`). A line only in `~/.bashrc` after the "not interactive: stop" guard is **not** enough.
- **`tmux`** (3.x), if the work should survive a dropped phone connection. The script adds a small managed block to the tmux config:
  `history-limit 50000`, `escape-time 10` (no delay on Esc, which both tools use), `focus-events on`, and for sessions named `julos-*` **mouse on and
  status off** (the JulOS server sets the same itself with `set-option` when it starts the session; the block only repeats it for sessions you start by hand).
  Other sessions are not touched.
- **`git`**: both tools lean on it (diffs, checkpoints, worktrees). **`gh`** (GitHub CLI) is optional but makes pull requests easy. **`ripgrep`**: bundled
  with the native Claude Code binary; installing it system-wide helps Codex and shell use. **`bubblewrap`** (and `socat` for Claude's network proxy) turn on the
  Linux sandboxes (section 5). **`gnupg`** lets the script verify the signature of the Claude manifest.
- **UTF-8 locale** (for example `C.UTF-8` or `de_DE.UTF-8`) so the terminal draws the interface correctly. **Time zone** set to yours, so session lists and logs read right.
- **Disk:** a Claude version is about 0.4 GB (the native installer keeps the newest two besides the running one), Codex about 0.1 GB, plus session history
  under `~/.claude` and `~/.codex` that grows with use. 3 GB free is a sensible floor.
- **Alpine / musl:** needs `bash` (Claude Code requires bash or zsh), `curl`, and the C++ runtime (`libgcc`, `libstdc++`). The Claude installer picks the musl build by itself.
- **Shell:** bash, zsh or fish for the program start line. For fish, add the bin directory with `fish_add_path` yourself.

## 2. Install, from the official sources only

The script does exactly this; you can do it by hand.

**Claude Code** (docs: setup page): `curl -fsSL https://claude.ai/install.sh | bash` is the official command. It downloads from
`https://downloads.claude.ai/claude-code-releases/<version>/<platform>/claude`, checks SHA-256 against `manifest.json` and runs the binary's own `install`
step. The manifest is **GPG-signed** (release key fingerprint `31DD DE24 DDFA B679 F42D 7BD2 BAA9 29FF 1A7E CACE`, from releases 2.1.89 on), which the script
verifies when `gpg` is present, with the fingerprint pinned in the script. Linux binaries themselves carry no code signature: the signed manifest is the root of trust.
Apt, dnf and apk repositories (signed) exist as an alternative; they do not auto-update.

**Codex CLI:** `curl -fsSL https://chatgpt.com/codex/install.sh | sh` (redirects to `releases.openai.com`; falls back to GitHub Releases if that fails). The
installer verifies SHA-256 of the download itself, needs no `sudo`, writes only below your home, and adds a PATH block to `~/.bashrc` or `~/.zshrc`. The
script downloads it to a file, checks that it is a `#!/bin/sh` script and runs it non-interactively (`CODEX_NON_INTERACTIVE=1`, target directory `CODEX_INSTALL_DIR`).
Release binaries on GitHub (`codex-x86_64-unknown-linux-musl.tar.gz`, `codex-aarch64-unknown-linux-musl.tar.gz`) are the manual alternative.

Why not npm: it works, but pulls a Node toolchain onto every machine, Claude Code's npm install cannot auto-update when the global directory is not writable, and
`sudo npm install -g` is explicitly discouraged by Anthropic. Use npm only where Node is already managed for you. Do not pipe a download into `sudo`.

Pin versions only when you have a reason (`curl ... | bash -s stable` for Claude's delayed channel; `CODEX_RELEASE=<version>` for Codex).

## 3. Signing in on a machine without a browser

Both tools sign in on the machine where they run, and store the result there. **JulOS never sees or stores it.** Do this once per machine, in a JulOS
**Shell** session (or a program session) on your phone, so the browser can be the phone's own.

**Claude Code**

1. In the terminal: `claude auth login` (add `--console` for API billing instead of a subscription, `--sso` to force SSO).
2. The terminal prints a **login URL**. On a machine without a browser the usual redirect to `localhost` cannot work, so use the separate URL printed above the
   prompt `Paste code here`. Copy it (see the tips below), open it in the phone's browser, sign in.
3. The browser page now shows a **code** instead of redirecting. Copy it.
4. Back in JulOS: tap **Einfügen** in the input bar (the clipboard text lands in the text field), press Enter. The code reaches the terminal as a paste.
   If the interactive prompt ignores pasted text, `claude auth login` is the variant that reads the pasted code from standard input.
5. `Login successful.` Check with `claude auth status`.

**Codex CLI**

1. In the terminal: `codex login --device-auth` (the "device code" flow, marked beta; it must be enabled in the ChatGPT security settings or by the workspace admin).
2. It prints a short **URL and a one-time code**. Open the URL on the phone, enter the code, approve. Nothing is pasted back: the terminal notices by itself.
3. Check with `codex login status`.
4. Fallbacks, in this order of preference: (a) forward the callback port (default 1455) over SSH from a machine with a browser and run `codex login` there;
   (b) sign in on a machine with a browser and copy `~/.codex/auth.json` over SSH. **Treat that file like a password.** Do (b) only between your own machines,
   never through chat, tickets or JulOS, and delete it from the source machine's backups if you copied it through a shared folder.
   (c) An API key: `printenv OPENAI_API_KEY | codex login --with-api-key` (usage-based billing). Type the key only into your own shell, not into a script or a Compose file.

Tips for the long Claude URL on a phone: the URL is several hundred characters and wraps over many lines. Make the terminal font smaller (A−, or two-finger
pinch) until the first line of the URL is visible, long-press the start, drag to the end of the URL, tap **Auswahl kopieren**, paste it into the browser's
address bar. In an interactive session Claude Code offers `c` to copy the URL, but that copies to the clipboard **of the remote machine** (useless over SSH).
The JulOS plan below makes this step a button.

**Not recommended:** `claude setup-token` prints a one-year token meant for CI, to be set as `CLAUDE_CODE_OAUTH_TOKEN`. That puts a long-lived secret in an
environment variable or a profile file, which is exactly what this page advises against, and the token cannot do everything a normal login can.

**Where the sign-in lives, and the rules:**

- Files under `~/.claude` and `~/.codex`, owner only. Claude writes `.credentials.json` with mode 0600 itself; Codex's `auth.json` is 0600 as well (checked on a
  machine that has it). The **directories** are often created 0775: the script sets `chmod 700` on `~/.claude` and `~/.codex` when they exist and are wider (content is not touched).
- **Never** put keys or tokens into JulOS (connection fields, "Eigener Befehl", the working directory), into `docker-compose.yml` environment variables, into
  `.env` files in the repository, or into shell history. `ANTHROPIC_API_KEY` set in the environment is picked up by Claude Code and **wins over the subscription
  login** once you approve it: an old key in `~/.profile` quietly changes how you are billed. If you use keys at all, prefer the tools' own stores
  (`codex login --with-api-key` from stdin; `/login` for Claude).
- Logging out: `/logout` in Claude Code, `codex logout`. Both revoke or remove what the machine holds.
- On macOS Claude uses the Keychain, which can be locked in an SSH session; Claude then falls back to the 0600 file. Codex can use the keyring too (`auto`).
- The sign-in expires (Claude shows "Your login expires in 3 days" at start; Codex refreshes tokens while you use it). A machine you do not touch for a long
  time needs a new sign-in. Claude: `claude auth status` shows it before a request fails.

## 4. Instruction files

- **`CLAUDE.md`** in the project folder (Claude Code reads it at start; `/init` drafts one) and optionally `~/.claude/CLAUDE.md` for all projects.
- **`AGENTS.md`** in the project folder (Codex reads it; `/init` drafts one) and optionally `~/.codex/AGENTS.md`.
- Put the same facts in both, or make one the master: a line in `CLAUDE.md` such as `Read AGENTS.md for the project rules.` keeps one source of truth.
  JulOS itself does exactly that kind of split: `CLAUDE.md` is the short rule list.
- What belongs in them: how to build and test, what must never be touched (secrets, the production compose, other machines), the branch/commit rules, "no
  secrets in the repo". They are instructions, not enforcement: **limits that matter go into the permission settings and the account rights (section 5, 6)**.

## 5. Permissions and sandbox: what is sensible on a home server

Both tools can run commands on the machine as the signed-in user. The permission mode decides how much they ask first. Defaults first.

**Claude Code modes** (`⇧Tab` cycles them in the session; JulOS has a quick key for it): `default` asks for everything beyond reads; `acceptEdits` allows file edits
and simple file commands inside the working directory; `plan` researches and proposes, edits stay blocked until you approve the plan; `auto` lets a classifier
approve ("reduces prompts but does not guarantee safety"); `dontAsk` denies whatever would ask (for scripts); `bypassPermissions`
(`--dangerously-skip-permissions`) allows everything: Anthropic says to use it **only in isolated containers or VMs without internet access**, it "offers no
protection against prompt injection", and Claude Code refuses it as root.
**Sandbox** (off by default): `/sandbox` or `"sandbox": {"enabled": true}` in `~/.claude/settings.json` restricts what Bash commands can write to the working
directory and temp, and sends network traffic through a proxy with an allow-list of hosts. It needs `bubblewrap` and `socat` on Linux. Reads stay broad
(`~/.ssh` is readable!), so the sandbox is not a substitute for a restricted account.

**Codex modes:** `sandbox_mode`: `read-only`, `workspace-write` (edit inside the workspace and run routine local commands; the low-friction default), `danger-full-access`
(no boundary). `approval_policy`: `on-request` (works inside the sandbox, asks to go beyond) or `never`. Full access is `danger-full-access` with `never`
(`--dangerously-bypass-approvals-and-sandbox`, "extremely dangerous" in the tool's own help). The documented lower-risk combination is
`workspace-write` with `on-request` (`--sandbox workspace-write --ask-for-approval on-request`). `sandbox_workspace_write.writable_roots` adds writable folders.
On Linux it uses `bubblewrap` (a bundled helper otherwise, which needs unprivileged user namespaces; Ubuntu 24.04 may need an AppArmor profile).
Project config in `.codex/config.toml` is only loaded for trusted projects, so a repository cannot widen its own permissions.

Recommended, for every machine in this plan:

| Setting | Claude Code | Codex |
|---|---|---|
| Start mode | `default`; allow `acceptEdits` only inside a project folder you can recreate from git | `workspace-write` + `on-request` |
| Sandbox | turn on `/sandbox` where `bubblewrap` works | the default sandbox; keep it |
| Never | `--dangerously-skip-permissions`, `permissions.defaultMode: bypassPermissions` | `danger-full-access` with `never`, `--dangerously-bypass-approvals-and-sandbox` |
| Network | leave the sandbox allow-list short | leave network off in `workspace-write` unless a task needs it |

**Why "never" on a machine that can reach everything:** a bypass or full-access session on a machine that holds SSH keys, a Docker socket or the family
photos turns any prompt injection (a README, an issue text, a web page the agent reads) into your user's rights. If you want unattended runs, do them in a
**throwaway container or VM that holds nothing** and has no route to the rest of the network. On a shared box, give the agents their own account.

## 6. Account, directories, rights

- **A dedicated account (`ai`) per machine** that holds the tools and the working copies: no `sudo`, **not in the `docker` group** (membership is root-equivalent),
  no access to other users' homes, no SSH keys or `.env` files of other projects. The script creates it with `--user ai` (locked password; you set one with
  `sudo passwd ai`, or add an SSH key; JulOS stores the password for the connection, see CLI-SESSIONS.md).
- **Working directory = a project clone** (for example `~/work/<project>`), not `~` and not `/`. JulOS passes it as the working directory of the program
  session and refuses to start the program in a directory that does not exist.
- **Work on clones, push from there.** Give the account a deploy key or a fine-grained token limited to the repositories it needs, kept in the account's
  own `~/.ssh` or `gh auth`, not shared with your own account. The agent can then break a clone, not the machine.
- **Never run the tools as root.** The script refuses root unless you name an account.
- Machines whose job is something else (hypervisor, NAS, Home Assistant) are covered in [AI-CLI-DEVICES.md](AI-CLI-DEVICES.md): short answer, keep the tools off them.

## 7. Updates

| | Automatic | By hand |
|---|---|---|
| Claude Code (native install) | yes, in the background, takes effect at the next start; channel `latest` (default) or `stable` (about a week behind) via `autoUpdatesChannel` in `~/.claude/settings.json` | `claude update`; `claude doctor` shows the last attempt. `DISABLE_AUTOUPDATER=1` in the `env` block of the settings stops the background check; `DISABLE_UPDATES` blocks every update path |
| Claude Code (apt/dnf/apk, Homebrew, WinGet) | no | `sudo apt update && sudo apt upgrade claude-code`, `brew upgrade claude-code` |
| Codex CLI | the docs name none for the CLI | `codex update` (present in 0.166), or re-run the install script |

Update while no session runs: a running session reads parts of its executable from disk. Old Claude versions stay until the installer prunes them. `codex doctor` and
`claude doctor` print read-only diagnostics.

## 8. Cost and limits, factually

- Claude Code is included in the paid claude.ai plans (not the free one) with usage limits that depend on the plan, or billed per use through a Console account.
- Codex is included in ChatGPT plans with limits depending on the plan, or billed per use with an API key (standard API rates, not plan credits).
- Long sessions with large codebases use the allowance quickly; both tools show remaining usage in the session (`/status` in both). Details change with the plans:
  read the plan pages, this page keeps no numbers.
- Two machines signed in with the same account share one allowance.

## 9. The setup script

```sh
curl -fsSLO https://<your JulOS server>/agent-files/julos-setup-ai-cli.sh   # planned; until then copy tools/julos-setup-ai-cli.sh
sh julos-setup-ai-cli.sh                      # dry run (default): shows what is there and what would happen, changes nothing
sh julos-setup-ai-cli.sh --yes                # do it, for the account that runs it
sh julos-setup-ai-cli.sh --yes --user ai      # do it for the account "ai" (created with sudo if missing; run as an admin or root)
```

Options: `--claude`, `--codex`, `--all` (default), `--user NAME`, `--tmux`/`--no-tmux`, `--no-extras`, `--prefix DIR`, `--no-gpg`, `--check-network`.
Exit status 0 = everything wanted is ready, 3 = something is missing or not on the login `PATH`, 1 = error.

What it does: detects Debian/Ubuntu (apt), Fedora/RHEL (dnf), Alpine (apk), macOS (Homebrew must exist); installs only **missing** system packages, with `sudo` (which asks
for its own password in your terminal; the script never reads or stores one); installs Claude Code with manifest checksum and GPG check and Codex with OpenAI's installer;
adds the PATH and tmux blocks (between `# >>> julos ai-cli >>>` markers, rewritten in place, so running it twice changes nothing); prints a ready/missing summary and the exact sign-in commands.
What it does **not** do: touch any credential file, set a password, change SSH or sudo configuration, install Docker, or install Homebrew.

Tested 2026-11-19: dry run on the development machine (Debian 13, read-only, nothing installed there); full runs in throwaway containers: Debian 13 as root with `--user ai` (both tools, GPG and SHA-256 checks passed, second run changed nothing), Ubuntu 24.04 as a normal user with `sudo` (Codex only, for the current account and for a second account), Alpine (musl) as root with `--user ai` (Codex only, the Claude musl build was not installed there).

## 10. JulOS support (plan, not built)

Goal: a phone-only flow. Add a host, tap "KI-Werkzeuge", paste one line into any terminal on that machine, sign in with the phone, tap "Claude". Everything below
stays inside the rules of this project: the server builds commands from fixed templates, secrets never pass through JulOS, every API needs a session.

| # | Piece | What it is | Effort |
|---|---|---|---|
| 1 | **Serve the script** | Add `julos-setup-ai-cli.sh` to the files under `/agent-files/` (same whitelist, `Dockerfile` copy, same "version/sum read from the bytes" idea as `AgentRelease`), plus `GET /agent-files/SHA256SUMS`. Nothing else changes. | S |
| 2 | **One-liner in the host dialog** ("KI-Werkzeuge einrichten") | A copyable block per host: `curl -fsSLO <server>/agent-files/julos-setup-ai-cli.sh && echo "<sha256>  julos-setup-ai-cli.sh" \| sha256sum -c - && sh julos-setup-ai-cli.sh` and the same with `--yes`. The checksum is shown by the authenticated app, so a truncated or tampered download (cache, proxy, flaky Wi-Fi) is caught. (It does not protect against a compromised JulOS server; the script is as trusted as the server.) | S |
| 3 | **Detect over the exec channel** | New read-only probe `POST /api/connections/{id}/tools/probe` using `ISshSession.ExecAsync` (5 s, rate-limited, like the existing start check). One fixed command prints `name=version` for `claude`, `codex`, `tmux`, `node`, `git`, `gh`, `rg`, the OS/arch, and `ok`/`missing` for the existence of `~/.claude` and `~/.codex/auth.json` and `~/.claude/.credentials.json` (**existence only; never `cat`, never a token**). "Signed in?" is therefore "sign-in file present"; the exact check stays `claude auth status` / `codex login status`, shown as the command to run. Result stored with the host (small table, migration) and shown in the host dialog under "KI-Werkzeuge". | M |
| 4 | **Tiles appear by themselves** | After a probe finds `claude` or `codex`, offer (or, with a setting, create) the tile through the existing `POST /api/connections/{id}/program`. No new launch code. | S |
| 5 | **Status badge** | per tool: `bereit` (found, signed in), `nicht eingerichtet` (not found: show the one-liner), `nicht angemeldet` (found, no sign-in file: show the sign-in command), `Update verfügbar`. The last one needs a reference version. Cheapest honest way: no server lookup, instead an "Update" button that runs `claude update` / `codex update` in a program session. A real "update available" needs the server to fetch `.../claude-code-releases/latest` and the Codex release feed (new outbound calls, to be decided). | M |
| 6 | **Sign-in helper in the terminal** | The terminal watches output for `https://` URLs (also across wrapped lines, which a phone causes) and for the prompts `Paste code here` and the Codex "enter this code" line. It shows a bar: **Open**, **Copy**, **QR** (a small plain-JS QR encoder, no dependency), and for the paste prompt it focuses the input bar with the hint "paste the code from the browser". Pure client side; no data leaves the browser. Pieces: URL detection + Open/Copy S, prompt hint S, QR M. | M |
| 7 | **Permission presets per host** | A list of working directories per host (the host dialog already owns the folder idea) and a **fixed, short list** of launch modes the server maps to flags: Claude `default` / `plan` / `acceptEdits`, Codex `workspace-write` + `on-request` / `read-only`. **No** bypass or full-access option, ever; a custom command stays the only way and keeps its warning. Needs an allow-list in `SshLaunch`, a column or table, UI. | M |
| 8 | **Merge with "Agent einrichten einfacher machen"** | One mechanism for both: the host dialog gets a **single** one-liner for a new machine, `julos-setup.sh --server <url> --code <code>` with modules (`--agent` for the desktop helper on a machine with a display, `--ai-cli` for the tools, both optional). The one-time code (10 minutes, one use, already built for the agent) is minted when the dialog opens. The script downloads its parts from `/agent-files/` of **this** server with the sums from `SHA256SUMS`. On a headless machine only `--ai-cli` runs; on a desktop, both. This replaces the three-file download in the current dialog. | M |

Recommended order (each step is useful alone):

1. **#1 + #2** (S): the script is served, the one-liner is copyable. This alone removes the manual copying.
2. **#3 + #4** (M + S): detection and automatic tiles; the host dialog finally says what is installed.
3. **#6, first half** (S): URL detection with Open/Copy and the paste hint. Fixes the worst moment (the long Claude URL on a phone). QR later (M).
4. **#5** (M): badges; start with "Update" as a button, decide about outbound version lookups separately.
5. **#7** (M): per-host directories and the fixed mode list.
6. **#8** (M): unify with the agent setup once the agent one-liner design is settled. Do this after 1 to 3, because it changes the agent dialog that works today.

Total: about one L, in six small releases. Risks to decide before building: the probe runs commands on the user's machine (fixed text, no input, same trust as the
existing start check); the sign-in helper must never log or store the URL or code (they are single-use secrets); #8 must keep the agent working for those who installed it the old way.

## Not verified

- Not run: the **full install on a machine with the real target setup** (see the device checklist); the interactive sign-in of either tool (needs a person with a browser); `codex login --device-auth` itself.
- Read from documentation only, not exercised: Claude's `auth status`, `/sandbox`, the sandbox settings keys; Codex's `sandbox_workspace_write` keys, trusted-project config, device-code availability for your account; the claim that the sign-in directories need chmod 700 (it is hygiene, not a documented requirement).
- Exercised in throwaway containers (Debian 13, Ubuntu 24.04, Alpine): the dry run, install for a new account as root, install via `sudo` as a normal user, second run (idempotence), PATH detection by `bash -lc`, the tmux block with a real tmux (mouse on/status off only for `julos-*` sessions). **Not exercised:** Fedora (dnf) and macOS (Homebrew) paths, `--prefix`, the arm64 builds, the Claude install on Alpine/musl, a machine behind an HTTP proxy. The script is shellcheck-clean as POSIX `sh` and ran under dash and busybox ash.
- The `CLAUDE.md`/`AGENTS.md` locations (`~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`) are from the tools' documentation conventions; the fetched pages only confirmed `CLAUDE.md` in the project and `/init` for `AGENTS.md`.
- Effort sizes in section 10 are estimates from reading the existing code (`SshLaunch`, `AgentRelease`, `ConnectionEndpoints`, the terminal client), not from a spike.

## Sources (read 2026-11-19)

- Claude Code, setup and requirements: https://code.claude.com/docs/en/setup
- Claude Code, authentication and credential storage: https://code.claude.com/docs/en/authentication
- Claude Code, permission modes: https://code.claude.com/docs/en/permission-modes
- Claude Code, sandboxing: https://code.claude.com/docs/en/sandboxing
- Claude Code, network access requirements: https://code.claude.com/docs/en/network-config
- Claude Code, troubleshooting install and login (SSH sign-in, memory): https://code.claude.com/docs/en/troubleshoot-install
- Claude Code installer script (checksums, platforms): https://claude.ai/install.sh (redirects to `downloads.claude.ai/claude-code-releases/bootstrap.sh`)
- Codex CLI, repository and README (install, release assets): https://github.com/openai/codex
- Codex, CLI page and authentication (device code, credential store): https://developers.openai.com/codex/cli and https://developers.openai.com/codex/auth (both redirect to `learn.chatgpt.com`)
- Codex, sandboxing and approvals: https://developers.openai.com/codex/concepts/sandboxing
- Codex installer script (directories, checksums, variables): https://chatgpt.com/codex/install.sh (redirects to `releases.openai.com/codex/install.sh`)
- `codex --help`, `codex login --help`, `claude auth --help` on a machine that has the tools (flags and subcommands named above)
