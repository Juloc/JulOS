# Host agent: manage machines without a desktop

The JulOS agent (`tools/julos-agent.py`) has two modes. The **desktop mode** runs inside the graphical session of a machine (window bar, program launcher, photo, file and
clipboard, see [AGENT_API.md](AGENT_API.md)). The **host mode** is for machines **without a desktop**: a Docker VM, a Proxmox host, any Debian or Ubuntu server. It reports
the machine to JulOS and answers a few **read-only** questions. It is the same file, the same enrolment, the same outbound connection; nothing listens on the machine.

What you get per host, at a glance (host card on the desktop and on the phone, plus the dialog "Host-Details"):

| Area | Shown |
| --- | --- |
| Load | CPU load (1, 5, 15 minutes), memory, swap, disks with fill level, uptime, network counters and rate, "reboot required" |
| Updates | number of available updates, how many are security updates, kept-back and held packages, age of the package lists |
| Disk health | SMART short status (when `smartctl` exists and the service may read the disks), software RAID from `/proc/mdstat`, ZFS from `zpool status -x`, full disks |
| Services and containers | failed systemd units, Docker containers (running, total, restarting) |
| Identity | host name, operating system, kernel, addresses, virtualised or not, Proxmox host detected |

Everything is **read only** in this version. Installing updates, restarting and controlling services or containers is planned (see [Roadmap](#roadmap)); the message form, the
permission file and the check on the server already exist so that those steps can be added without redoing the security design.

## Modes

| | Desktop mode | Host mode |
| --- | --- | --- |
| For | a machine with a graphical session (X11, XFCE tested) | a machine without one (Docker VM, Proxmox, servers) |
| Needs | Python 3.8+, PyGObject, GTK 3, Wnck, a display | Python 3.8+ and nothing else (standard library) |
| Runs as | your user, in your session (autostart or user service) | its own system user `julos-agent` without login shell, as a systemd system service |
| Platform in `hello` | `linux-x11` | `linux-host` |
| Requests | `/v1/windows`, `/v1/apps`, `/v1/mode`, clipboard | `/v1/host/*` (below) |

The mode is chosen when the agent starts: `--host` forces the host mode, `--desktop` forces the desktop mode, and without either the agent uses the host mode **when the desktop
stack (PyGObject, GTK, Wnck) is missing**. If the stack is there but `DISPLAY` is not (for example a user service that starts before the login), the agent refuses to start with a clear
message instead of silently becoming a host: a desktop machine must not change its role because of a start order. `julos-agent.py check` and `status` say which mode applies.

One host has one agent. A machine with a desktop **and** an interesting server side (a workstation that also runs Docker) uses the desktop agent; the host-mode requests need the host agent,
so give such a machine two hosts if you want both (two agents cannot share a host; this is a deliberate limit, see "What is deliberately missing").

## Install from JulOS over SSH

In the app: open the host, choose **Agent per SSH installieren**. JulOS creates a one-time code, opens the SSH connection of this host in the terminal and **puts the finished command
into the input bar without sending it**. You read it, press Enter, and type your `sudo` password **in the terminal yourself**. JulOS never stores, sees or sends the sudo password.
If the host has no terminal connection, the dialog shows the command to copy (**Befehl kopieren**) and you run it on the host.

The command downloads the installer into a file (so you can look at it) and runs it with sudo:

```sh
curl -fsSL https://SERVER/agent-files/install-host-agent.sh -o julos-install-host-agent.sh && sudo sh julos-install-host-agent.sh --server https://SERVER --code ABCDE-FGHIJ
```

The installer (`tools/install-host-agent.sh`, POSIX `sh`, `shellcheck`-clean, idempotent):

1. creates the system user `julos-agent` (no login shell, home `/var/lib/julos-agent`),
2. downloads `julos-agent.py` **from your own JulOS server only** (`/agent-files/`, no redirects, at most 2 MB), checks it against `/agent-files/SHA256SUMS` and as valid Python, installs it in `/opt/julos-agent`,
3. creates `/etc/julos-agent/permissions` (owned by root, see [Security](#security)),
4. enrols the agent with the code (as `julos-agent`; the token lands in `/var/lib/julos-agent/.config/julos/agent.json`, mode 0600),
5. installs and starts the systemd service `julos-agent` (`Restart=on-failure`, `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome=read-only`, `PrivateTmp`, no capabilities, only `/var/lib/julos-agent` and `/opt/julos-agent` writable).

Options: `--dry-run` (shows every step and the unit file, changes and downloads nothing, needs no root), `--user NAME`, `--allow-updates` (writes the line `updates` to the permission file; has no effect before step 3 of the roadmap),
`--docker-group` (see below), `--allow-http` (test instance only), `--no-start`, `--uninstall`. Without systemd the files are installed and the command to start the agent by hand is printed.
The checksum protects against a damaged or truncated download; it does **not** protect against a hostile server (both come from the same server). That is the same trust as the existing desktop agent update.

Requirements on the host: `python3` (3.8 or newer), `curl` or `wget`, `sudo`/root. Nothing else is installed.

### Updating

Use **Jetzt aktualisieren** (or the automatic update) in the host dialog, exactly like the desktop agent ([AGENT_API.md](AGENT_API.md) "Updating the agent"): the agent downloads
`/agent-files/julos-agent.py` from its own server, checks the SHA-256, replaces its file atomically and restarts itself. For this the service user owns `/opt/julos-agent`.
Re-running the installer does the same by hand.

### Uninstall

```sh
sudo sh install-host-agent.sh --uninstall           # or: --dry-run --uninstall first
```

It stops and removes the service, `/opt/julos-agent`, `/etc/julos-agent`, `/var/lib/julos-agent` (log and token) and the user (only if the installer created it). Then use **Agent entfernen** in the
host dialog so the token is also dead on the server. The agent file on the host is the only thing JulOS ever installs.

## What the agent reads

Only fixed requests, answered with JSON, never a file or a log. Each capability appears in the `hello` (`caps`) only if the machine can do it.

| Capability | Request | Reads |
| --- | --- | --- |
| `sysinfo` | `GET /v1/host/sysinfo` | `/proc/meminfo`, `/proc/uptime`, `/proc/loadavg`, `/proc/mounts` + `statvfs`, `/proc/net/dev`, `/etc/os-release`, `ip -o addr`, `/var/run/reboot-required`, `systemd-detect-virt`, Proxmox markers (`pveversion`), health (below) |
| `updates` | `GET /v1/host/updates` | `apt-get -s dist-upgrade` (a simulation, no root), `apt-mark showhold`, age of `/var/lib/apt/lists`; `dnf -C check-update`, `apk list -u`, `checkupdates` in the simplest form (no security flag there) |
| `services` | `GET /v1/host/services` | `systemctl --failed`, `systemctl is-system-running` |
| `docker` | `GET /v1/host/docker` | one request on the local Docker socket: `GET /containers/json?all=1` |

**Updates.** Security updates are the ones whose origin carries `security` (Debian `Debian-Security`, Ubuntu `-security` and ESM). The agent never refreshes the package lists: that needs
root. It shows their **age** instead ("package lists were updated 3 days ago: `sudo apt update` on the host"), so "no updates" is never a silent lie. Most systems refresh them by a timer (`apt-daily`).

**Disk health.** SMART needs `smartctl` and permission to read the disks; a plain service user usually has none, then the card says "not read" and why. Giving the service access to the raw disks
(group `disk`) is a decision you make on the host (it lets the service read every disk); the installer does not do it. RAID state comes from `/proc/mdstat` (degraded, rebuilding with percent, inactive),
ZFS from `zpool status -x`. Disk fill level is part of the disks list; a disk at 90 % or more is marked. The first read of the SMART data runs in the background (it can take seconds); later reads use a
ten-minute memory. Btrfs and LVM are not read yet (see the roadmap).

**Docker.** The agent only ever sends `GET /containers/json?all=1` (a read), but **access to the Docker socket is access to root on that machine**: the `docker` group is root-equivalent. So the
default is "no access", and the card says so. Two ways: run a read-only socket proxy and give the agent its socket (`--docker-socket` or `JULOS_AGENT_DOCKER_SOCKET`; recommended, see
[INTEGRATIONS.md](INTEGRATIONS.md) 2.1), or accept the risk for this host with `install-host-agent.sh --docker-group` (adds the supplementary group `docker` to the unit). Containers that keep restarting are counted
(`restarting`); the per-container restart counter is not in the list answer and is a roadmap item.

## Server side

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/hosts/{id}/status` | system values (`info`), session needed, 404 for a host that is not yours |
| GET | `/api/hosts/{id}/updates` | list, counts, age of the package lists |
| GET | `/api/hosts/{id}/services` | failed units |
| GET | `/api/hosts/{id}/docker` | containers |
| GET | `/agent-files/install-host-agent.sh`, `/agent-files/SHA256SUMS` | public like the other agent files; `SHA256SUMS` is `sha256sum` format over exactly the served bytes |

Answer shape: `{"state", "at", "stale", "error"?, "info"|"updates"|"services"|"docker"?}`. `state` is `ok`, `offline` (agent enrolled, not connected), `no_agent`, `unsupported` (the agent lacks the capability: the agent is not even asked) or `error`
(`error` is a short code such as `timeout`, `apt_failed`, `invalid_answer`; free text of the agent is never passed on). On `offline` and `error` the last good data is still returned with `stale: true`.

- **Cache, no polling.** The server asks the agent only when a browser asks it (someone is looking), keeps each answer in memory for **60 seconds** per host and kind, and lets simultaneous requests share one question.
  `?fresh=1` (the "Neu lesen" button) may read again, but not more often than every **15 seconds**. The browser asks only while the Hosts view or the Server page is drawn.
- **Allowlist.** The server sends exactly four paths (`/v1/host/sysinfo|updates|services|docker`), chosen by the endpoint, never by the browser, and only when the agent announced the capability.
- **Limits.** Answers are at most 600 kB (link limit), packages 500, containers 100, other lists 64 entries. Every field is read one by one: texts are cut and stripped of control characters, numbers clamped, wrong types dropped,
  IP addresses must parse. Hostile or broken answers cannot reach the page as markup (the front end also escapes everything).
- **Rights.** Every route needs the session cookie (ApiGuard) and shows only your own hosts; 90 requests per minute and user.
- **`GET /api/hosts`** gains, inside `agent`: `kind` (`host` or `desktop`), `caps`, `allow` (what the agent allows locally for write actions), `control` (the server-side level). Old fields are unchanged.
- **Migration 017** adds `agents.control` (`read`, `updates`, `full`; default `read`). Nothing sets it yet.

## Security

The design follows [INTEGRATIONS.md](INTEGRATIONS.md) section 3. For this agent specifically:

- **Outbound only.** The agent connects to the server over `wss://` (system certificate store, no "insecure" switch); a plain `http://` server is refused except `localhost` or `--allow-http` for tests. Nothing listens on the machine.
- **Own user, no shell, locked down.** `julos-agent` has no login shell and no password; the unit has `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome=read-only`, `PrivateTmp`, an empty capability set, and writes only to its own two folders.
- **Read only by construction.** Commands are fixed argument lists without a shell; the only `apt-get` call is `-s` (simulate); there is no code path that installs, restarts or writes outside the agent's own state.
- **The agent can't give itself rights.** `/etc/julos-agent/permissions` counts only when **root owns it and it is not writable by group or others** (otherwise it is ignored and a warning is logged). Alternatively `--allow-updates` / `--allow-reboot` on the command line (set in the unit, which root owns).
  The words in the file are `updates` and `reboot`. Today they unlock nothing: a write request answers `403 not_permitted` without the right and `501 not_implemented` with it.
- **Two keys.** A write action will need the server (`agents.control`, checked by `AgentControl.Permitted`) **and** the agent (the local file) to agree. The agent reports its local rights in `hello` (`allow`) so the app can show why an action is greyed out.
- **The agent is not the server.** Its answers are untrusted input (see "Limits" above). It cannot ask the server for anything: the server only sends fixed requests.
- **Token.** 256 bits, stored on the server as SHA-256 only, on the host in a 0600 file of the service user; removing the agent in the app kills it at once.
- **The agent file belongs to the service user** (so it can replace itself on update). Someone who already is that user could change the agent; that user has no rights beyond what is listed here, which is why it must stay unprivileged (no `docker` group by default, no `sudo` rules).

## Proxmox

A Proxmox VE (or Backup Server, Mail Gateway) host is a **Debian machine**: the host agent installs and runs there like anywhere else and shows load, memory, disks, **ZFS state** (`zpool status -x`),
RAID, the Debian updates of the node, failed units and the reboot flag. The agent detects Proxmox (`pveversion`, `/etc/pve`) and the card names it ("Proxmox VE 8.x"). Use SSH once to install, as above.

What the agent cannot give you is the Proxmox view: guests, cluster state, storage pools by name, backup jobs, tasks. That comes **in addition, later**, from the Proxmox API with a read-only token (milestone M2 in
[INTEGRATIONS.md](INTEGRATIONS.md) 2.2) and links guests to hosts (M6a). The two do not replace each other: the agent tells you the *node* is fine, the API tells you what runs on it.

## Home Assistant OS

Home Assistant OS is an appliance: there is **no normal shell and no package manager you may use**, and a Python agent installed by hand would be wiped by an update. So no host agent runs there, and none is planned.
The plan is, in this order of preference:

1. **Home Assistant API** (milestone M3 in [INTEGRATIONS.md](INTEGRATIONS.md) 2.3): a long-lived token with the least rights, selected entities only. Many of the host values are available as entities (the *System Monitor* and *Supervisor* integrations expose CPU, memory,
   disk, uptime, available updates, add-on state). This needs no software on the appliance.
2. **A Home Assistant add-on** (later, only if the API is not enough): an add-on is the supported way to run your own code there. It would be a small container running this host agent in a restricted mode, with the add-on's own read-only access. Not started; it needs its own plan
   (repository, signing, updates through the add-on store).
3. A **Home Assistant Container or Supervised** install on a normal Debian machine is just a Linux host: use the host agent.

## What is deliberately missing

- **Everything that writes**: install updates, reboot or power off, start/stop/restart services or containers, read logs, update images. See the roadmap.
- **Windows** agent (planned, see [AGENT_API.md](AGENT_API.md)), macOS, BSD. The host mode needs Linux.
- **Refreshing package lists** (`apt-get update`): needs root, and an agent that does it silently is the first step toward an agent that can do anything. It will be an explicit action of stage 3.
- **Both agents on one host.** One host has one agent; a machine that needs both modes becomes two hosts (for example "workstation" and "workstation (server)").
- **Btrfs and LVM health**, per-container restart counters, Docker stats (CPU/memory per container), logs.
- **A history.** Values are a snapshot; there are no graphs and no alerts yet (see "Later").
- **dnf/apk/pacman details**: no security flag, no list age, and `dnf` only from its cache.

## Roadmap

The user's decisions for the host agent (answers collected before the build) are binding for the following stages.

### Stage 1 and 2 (this version): read and show, install over SSH

Done in this version: the host mode, the four read capabilities, disk health, the endpoints with cache, the cards and the dialog, the SSH install, the permission file and the `control` level (unused).

### Stage 3: actions (write), every one confirmed, never silent

Planned actions: install updates (single package or all, with progress and a reboot hint), reboot and power off, list/start/stop/restart **systemd services**, start/stop/restart **Docker containers**, read **logs** (bounded, on demand), update **images**.

Binding rules:

1. **Always confirmed.** A two-step confirmation on the server: the browser asks, the server answers with a short-lived, single-use confirmation naming exactly what will happen (host, action, target), the user confirms in a second request. No action runs without it, no action is ever started by a schedule or by the agent.
2. **Two keys.** The server checks `agents.control` (and the user's session, rate limit, a pause switch); the agent checks its **local permission per machine and per kind of action** (`/etc/julos-agent/permissions`, owned by root; words `updates`, `reboot`, later `services`, `docker`, `logs`). Either one missing means refused.
3. **Audit.** Every request, refusal and result writes an audit entry (who, which host, which action, which target, outcome); no secret in it.
4. **Narrow requests.** New fixed paths (`/v1/host/...`) with checked values: package names from the update list only, unit names from the list the agent itself reported, container ids from the container list. No free command, no shell.
5. **Progress and the way back.** Long actions (updates) report progress over the link (`progress` messages with the request id) and a final result; a lost link does not abort the action, the result is read back when it returns. A reboot hint appears when `reboot-required` is set afterwards.
6. **Message form** (already fixed): the server sends `{"t":"req","id":N,"method":"POST","path":"/v1/host/<action>","body":{...}}`; the agent answers `403 not_permitted` (right missing), `501 not_implemented` (not built) or the result. The agent reports `allow` in `hello`.

Effort: updates and reboot M, services S to M, containers M (depends on the socket decision above), logs S, images M. Tests: fakes for every verb, a request recorder that proves nothing else is sent, a refusal test per missing key.

### Stage 4: Docker and Proxmox depth

Docker through the planned socket proxy and the Docker integration (M1, M5a in [INTEGRATIONS.md](INTEGRATIONS.md)): per-container stats, restart counters, logs, label gate; Proxmox through its API (M2, M5b): guests, storage, backups, start/stop with the typed-name confirmation. The host agent stays the "node health" source.
Effort: as listed there (M to L).

### Stage 5: convenience (each its own plan)

| Item | Notes | Effort |
| --- | --- | --- |
| Notifications to the phone for full disks, failed services, new security updates | needs push (Web Push, VAPID) and a small rule table; the data is already there | M |
| Schedule for automatic updates in a maintenance window, with a report | stage 3 first; unattended by definition, so it needs its own opt-in, a window, a "what changed" report and an off switch per host | M to L |
| Install programs on the host by button (Claude, Codex, ...) | links the host dialog to `tools/julos-setup-ai-cli.sh` ([AI-CLI-SETUP.md](AI-CLI-SETUP.md)); runs it in the SSH terminal like the agent install, the user types the sudo password | S |
| Saved terminal shortcuts per host | commands stored per host and pasted into the input bar, never sent automatically | S |

## Tests

- `tests/agent/test_host.py`: parsers with fixtures (apt with security, new and kept-back packages, empty list, Ubuntu origins; dnf, apk, pacman; mdstat, zpool, smartctl; failed units; Docker over a real Unix socket), `sysinfo` from fake `/proc` files, permissions (owner and mode rules), the request table, the mode decision, a real `Link` against a small WebSocket server.
- `tests/agent/test_installer.py`: the installer in dry-run (no root, no network) and its argument checks.
- `tests/JulOS.Backend.Tests/HostInfoTests.cs`: endpoints with a fake agent: all four reads, cache and refresh limits, simultaneous requests, offline and stale, no agent, unsupported, error codes, hostile data, session and ownership, migration 017 and the control check.
- `tests/frontend/hostinfo.test.mjs`, `hosts.test.mjs`: formatters, card lines, HTML escaping, the store, the install command, the choice of the terminal connection.
- By hand: the installer in a throw-away `debian:trixie` container without desktop against a test instance; the agent connects, status and updates show up (see the CHANGELOG entry for what was verified).
