# Host agent: look after a machine, with or without a desktop

The JulOS agent (`tools/julos-agent.py`) is **one agent per machine** with two groups of abilities. The **desktop abilities** work inside the graphical session of a machine (window bar, program launcher, photo, file and
clipboard, see [AGENT_API.md](AGENT_API.md)). The **host abilities** report the machine to JulOS and answer a few **read-only** questions (system values, updates, disks, services, Docker), plus the confirmed actions below.
A machine **without a desktop** (a Docker VM, a Proxmox host, any Debian or Ubuntu server) runs the agent in **host mode**: only the host abilities. A machine **with a desktop** runs it in **desktop mode** (since agent 0.7.0): the desktop abilities **and** the host abilities,
in one process. It is the same file, the same enrolment, the same outbound connection; nothing listens on the machine.

What you get per host, at a glance (host card on the desktop and on the phone, plus the page "Host-Details"):

| Area | Shown |
| --- | --- |
| Load | CPU load (1, 5, 15 minutes), memory, swap, disks with fill level, uptime, network counters and rate, "reboot required" |
| Updates | number of available updates, how many are security updates, kept-back and held packages, age of the package lists |
| Disk health | SMART short status (when `smartctl` exists and the service may read the disks), software RAID from `/proc/mdstat`, ZFS from `zpool status -x`, full disks |
| Services and containers | failed systemd units, Docker containers (running, total, restarting) |
| Identity | host name, operating system, kernel, addresses, virtualised or not, Proxmox host detected |

Reading is the default and needs nothing. **Actions** (install updates, reboot, control services and containers, read logs, pull images) exist since agent 0.6.0, but they are **off until you switch them on per host**
and every single one is **confirmed by you in the app, every time**: see [Actions](#actions-stage-3). Nothing is ever started by a schedule or by the agent itself.

### Where you see it

The **host card** (list of hosts, phone and desktop; see [HOSTS.md](HOSTS.md)) shows state, kind of agent, load, memory, the main disk, and notes only when there is something; the notes are buttons (the updates note reads "Updates ansehen und installieren"), and the card menu has the simple actions (below). **Details** opens the **page "Host-Details"**: no dialog, on the desktop a view in the Start window (view Hosts, side list of sections, arrow back to the list), on the phone its own page with a back arrow and tabs. Every host has the same page with the same sections; what an agent does not deliver, the section says in one sentence with the button **Agent aktualisieren / einrichten**. What is shown follows the **capabilities the agent reports** (`caps` and `allow` in its hello) and the server's `agent.hostSupport`, not the name of its platform: `yes` (the agent has the word `host`) is read, `update` (an old desktop agent before 0.7.0) says "liefert noch keine Systemwerte" with the button **Agent aktualisieren / einrichten**, `none` (new enough, not Linux) says so without that button, `unknown` (not connected) says the agent is not connected. Every section is a few cards:

| Section | Cards |
| --- | --- |
| Überblick | what needs attention (each with "Ansehen" to the right section), the three key figures, the machine (system, kernel, name, virtualisation, uptime, reboot) |
| Auslastung | load for 1, 5 and 15 minutes per core, memory (without cache) and swap, each with a bar |
| Datenträger | one bar per mount point with percent, size and file system; **health** with one plain statement ("Platten in Ordnung", "Problem bei den Platten" with the reason, or "nicht lesbar" and why), then SMART per disk, RAID and ZFS |
| Netzwerk | the real interfaces first (IPv4 prominent, IPv6, link-local `fe80` addresses only as a count, traffic since boot, the rate from two readings); Docker and bridge interfaces (`docker0`, `br-…`, `veth…`, `virbr…`, …) folded into one group |
| Updates | security updates first, then the others, kept-back and held packages, the age of the lists; the buttons below |
| Dienste, Docker | failed services and the containers, with their buttons |
| Agent | state of the agent, **Jetzt aktualisieren**, automatic update, **Agent entfernen**, and the one flow **Agent einrichten** (ticks, command to copy or install over SSH); the way out of every empty state ("Agent aktualisieren / einrichten") |
| Logs, Aktionen, Verlauf | for an agent that reports capabilities, from 0.6.0: [Logs](#logs-from-a-list), the level, the pause switch and the local permission, restart and power off, the history |

The numbers in the navigation (updates, failed services, restarting containers, `!` for a disk problem) show where to look. Everything the agent sends (host names, packages, addresses, output) is escaped; the markup is built in one place (`hostview.js`).

**Simple actions on the card.** The card menu offers **Paketlisten aktualisieren**, **Updates installieren** (all), **Neu starten**, **Ausschalten** and **Logs ansehen** without opening the page first. Each one goes through the usual two-step confirmation (a small sheet with host, action, target and, where required, the typed host name); nothing is executed by the menu itself. An entry that the keys do not allow is **greyed out with the reason and the way to unlock it**: "Aktionen sind für diesen Host aus" (level off), "Alle Aktionen sind pausiert" (pause), "Auf dem Rechner nicht erlaubt (Wort reboot): Agent mit --allow reboot neu einrichten" (local word), or that the agent is not connected, too old or reports nothing; the first group leads to the section **Aktionen** when tapped. Without an agent the menu has no actions. **Logs** opens the page at the section Logs, where you pick the service.

The hint "Die Listen aktualisiert nur root, auf dem Host: sudo apt update." appears only when the button **Paketlisten aktualisieren** is not available (level off, the word `updates` missing on the host, pause); with the button there only the age of the lists is shown.

## Modes

| | Desktop mode (since 0.7.0: both groups) | Host mode |
| --- | --- | --- |
| For | a machine with a graphical session (X11, XFCE tested) | a machine without one (Docker VM, Proxmox, servers) |
| Needs | Python 3.8+, PyGObject, GTK 3, Wnck, a display (the host abilities need only the standard library) | Python 3.8+ and nothing else (standard library) |
| Runs as | **your user**, in your session (autostart or user service); never root, never sudo | its own system user `julos-agent` without login shell, as a systemd system service |
| Platform in `hello` | `linux-x11` | `linux-host` |
| `caps` in `hello` | `desktop`, `host`, and what the machine can: `sysinfo`, `updates`, `services`, `docker` | `host` and the same four (as far as the machine can) |
| Requests | `/v1/windows`, `/v1/apps`, `/v1/mode`, clipboard, **and** `/v1/host/*` (below) | `/v1/host/*` (below) |

The mode is chosen when the agent starts: `--host` forces the host mode, `--desktop` forces the desktop mode, and without either the agent uses the host mode **when the desktop
stack (PyGObject, GTK, Wnck) is missing**. If the stack is there but `DISPLAY` is not (for example a user service that starts before the login), the agent refuses to start with a clear
message instead of silently becoming a host: a desktop machine must not change its role because of a start order. `julos-agent.py check` and `status` say which mode applies.

### One agent per machine

A machine with a desktop **and** an interesting server side (a workstation that also runs Docker, a development VM) needs no second agent: the desktop agent answers the host requests as well. It uses the same code as the host mode
(`host_api`, not a copy) and the same checks: the same fixed paths, the same local permission file `/etc/julos-agent/permissions`, the same root helper, the same validation of every name. A refusal without the word is `403 not_permitted`, exactly as in host mode.
The desktop agent answers `/v1/host/*` only over the **link to the server** (never over the local Unix socket) and in worker threads (never in the GTK thread).

**`caps` in the `hello`** (agent 0.7.0 and later) says what the agent can do; the server asks and shows by these words and **not by the platform name**:

| Word | Means |
| --- | --- |
| `desktop` | window bar, programs, mode, photo, file, clipboard |
| `host` | answers the `/v1/host/*` requests (set when at least one of the following is there) |
| `sysinfo`, `updates`, `services`, `docker` | the four read abilities (see "What the agent reads"); each is announced only if the machine can do it |

`allow` (the words of the local permission file) is reported the same way for both modes. For agents before 0.7.0 the server fills in the words so the app can ask uniformly: a host agent 0.5/0.6 gets `host`, an old desktop agent gets `desktop`.
An **old desktop agent (0.5.x/0.6.0)** is still valid; it has no system values. `GET /api/hosts` shows this as `agent.hostSupport`: `yes` (the agent answers host requests), `update` (a desktop agent before 0.7.0: "liefert noch keine Systemwerte", the update to 0.7.0 brings them; the status calls answer `state: unsupported`, never an error),
`none` (new enough but no host abilities, for example not Linux) or `unknown` (not connected and nothing known about it). The self-update of such an agent **never switches a word on**: it replaces only `julos-agent.py`; the permission file is root's and is not touched (without it the new agent can read, nothing more).

A desktop agent differs from a host agent in one honest way: it is **your user's process** without the sandbox of the system service, so the root helper's socket belongs to your user (mode 0600), and every program of that user can ask the helper for what the permission file allows (see [Security](#security) and `docs/SECURITY-REVIEW.md`). Choose the words for a desktop machine with that in mind (`updates,logs` are the modest choice).

## Install: one installer for every machine

In the app: open the host page at the section **Agent** (**Agent verwalten** in the card menu, or **Agent einrichten** on a host without one). **One flow for every machine**, no separate "desktop agent" and "host agent" any more: choose what the agent may do with the ticks (**Updates installieren, Logs lesen, Dienste, Container, Neu starten und ausschalten**; none is ticked by default, see [Actions](#actions-stage-3)), then **Agent per SSH installieren** or **Befehl kopieren**. The ticks become `--allow a,b` (always in the order updates, logs, services, docker, reboot; only these words ever reach the command) and, only with Docker ticked and the extra tick "Docker-Zugriff geben (Gruppe docker: entspricht root auf dem Rechner)", `--docker-group`. JulOS creates a one-time code, opens the SSH connection of this host in the terminal and **puts the finished command into the input bar without sending it**. You read it, press Enter, and type your `sudo` password **in the terminal yourself**. JulOS never stores, sees or sends the sudo password.
If the host has no terminal connection, the page shows the command to copy and you run it on the host.

The command downloads the installer into a file (so you can look at it) and runs it with sudo. It is the same line on every machine; the installer finds out what the machine is:

```sh
curl -fsSL https://SERVER/agent-files/install-agent.sh -o julos-install-agent.sh && sudo sh julos-install-agent.sh --server https://SERVER --code ABCDE-FGHIJ [--allow updates,logs,services,docker,reboot] [--docker-group] [--allow-http] [--dry-run] [--uninstall]
```

`tools/install-agent.sh` (POSIX `sh`, `shellcheck`-clean, idempotent) decides:

- **Desktop**: the user who called `sudo` (`SUDO_USER`, or `--user`) has a graphical session (per `loginctl`; without it the display variables) **and** the desktop stack (python3-gi, GTK 3, Wnck) is installed. The installer says **"Desktop detected"** and installs the agent **as that user, in the user's session**: files in `~/.local/share/julos`
  (an existing installation is updated **where it already is**), enrolment as that user, an autostart entry (or with `--systemd` a systemd **user** service; one that exists stays), then it stops an old agent of that user and starts the new one in the session (when `DISPLAY` is known; otherwise at the next login).
  The agent stays a user process: **no root, no sudo, no service account**. Besides `julos-agent.py` the desktop gets `julos-clip.py` (the clipboard helper), as before.
- **Host**: no desktop (or none that can be used): the host installation of the next list, as it always was.
- An **installation that exists decides first** (a host agent as a system service stays a host agent, an autostart entry or user service of the user stays a desktop agent), so updating needs no new code and never switches the kind by accident. A graphical session without the desktop stack is **not** guessed to be a host: the installer says what to install (or you add `--host`).
- `--desktop` and `--host` force the kind. **One machine, one agent**: forcing `--desktop` while a host agent is installed is refused with the way out (remove the host agent, enrol with a new code), and the other way round it only warns.

In both kinds the same rules hold: the permission file is root's and **empty of words unless you name some with `--allow`**, the root helper is installed **only** if one of `updates`, `reboot`, `services`, `logs` was chosen (its socket is reachable only by the agent's user: the service account's own group for a host, the user's own socket with mode 0600 for a desktop), the downloads come only from your server and are checked, and a password is never read or typed.
The old commands keep working: `install-host-agent.sh` and `install-linux-agent.sh` are thin callers that print a note and forward to `install-agent.sh` (with `--host` and `--desktop` in front); when the new script is not next to them they fetch it from `--server` the same checked way.

The host installation, step by step:

1. creates the system user `julos-agent` (no login shell, home `/var/lib/julos-agent`),
2. downloads `julos-agent.py` **from your own JulOS server only** (`/agent-files/`, no redirects, at most 2 MB), checks it against `/agent-files/SHA256SUMS` and as valid Python, installs it in `/opt/julos-agent`,
3. creates `/etc/julos-agent/permissions` (owned by root, see [Security](#security)); it is **empty of words** unless you named some with `--allow`,
4. enrols the agent with the code (as `julos-agent`; the token lands in `/var/lib/julos-agent/.config/julos/agent.json`, mode 0600),
5. installs and starts the systemd service `julos-agent` (`Restart=on-failure`, `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome=read-only`, `PrivateTmp`, no capabilities, only `/var/lib/julos-agent` and `/opt/julos-agent` writable),
6. **only if you allowed something that needs root** (`updates`, `reboot`, `services`, `logs`): downloads `julos-agent-helper.py` the same way (own server, checksum), installs it as root in `/usr/local/libexec/julos-agent/` and sets up
   the socket-activated services `julos-agent-helper.socket` and `julos-agent-helper@.service` (see [The root helper](#the-root-helper)). Re-running the installer updates the helper; the agent's self-update never touches it.

Options: `--dry-run` (shows every step and the unit files, changes and downloads nothing, needs no root), `--desktop` / `--host`, `--user NAME` (host: the service account; desktop: the user), `--allow WORDS` (comma separated from `updates reboot services docker logs`, written to the
permission file; **never a default**, see [Actions](#actions-stage-3); `--allow-updates` is the same as `--allow updates`), `--docker-group` (host: see below; desktop: it only checks whether your user is in the group `docker` and tells you, the installer **never changes your groups**), `--systemd` (desktop: user service instead of autostart), `--allow-http` (test instance only), `--no-start`, `--uninstall`.
Without systemd the files are installed and the command to start the agent by hand is printed.
The checksum protects against a damaged or truncated download; it does **not** protect against a hostile server (both come from the same server). That is the same trust as the existing desktop agent update.

Requirements on the host: `python3` (3.8 or newer), `curl` or `wget`, `sudo`/root. For a desktop also python3-gi, GTK 3 and Wnck (the installer names the packages). Nothing else is installed.

### Moving from older installations

| You have | Do | What happens |
| --- | --- | --- |
| a **desktop agent 0.5.x/0.6.0** (autostart entry or user service from `install-linux-agent.sh`) | nothing, or **Jetzt aktualisieren** in the section Agent of the host page | the self-update brings 0.7.0; the host reads (system values, updates, services, Docker) appear without any new right. |
| the same, and you want actions | run the one command above **without a new code** (`--server` only, or with a fresh code, either works) plus `--allow ...` | the agent files are updated where they are, the token stays, the autostart entry is rewritten, the old agent is replaced, the permission file and (if needed) the root helper are set up. |
| a **host agent** on a server | re-run the command as before (or `install-host-agent.sh`, which forwards) | updated like before; nothing changes about its user, service or words. |
| a host agent on a machine that **has** a desktop | `sudo sh install-agent.sh --host --uninstall`, remove the agent in the app, then the new command with a new code | one agent, as the desktop agent; the old host token dies with the removal. |

### Updating

Use **Jetzt aktualisieren** (or the automatic update) in the section Agent of the host page, exactly like the desktop agent ([AGENT_API.md](AGENT_API.md) "Updating the agent"): the agent downloads
`/agent-files/julos-agent.py` from its own server, checks the SHA-256, replaces its file atomically and restarts itself. For this the service user owns `/opt/julos-agent`.
Re-running the installer does the same by hand.

### Uninstall

```sh
sudo sh install-agent.sh --uninstall           # or: --dry-run --uninstall first; --host or --desktop to remove only one kind
```

Without `--host` or `--desktop` it removes whatever is installed here. A **host** installation: it stops and removes the service, the root helper and its two units, `/opt/julos-agent`, `/etc/julos-agent`, `/var/lib/julos-agent` (log, token, results of actions) and the user (only if the installer created it).
A **desktop** installation: `julos-agent.py uninstall --yes` as the user (stops the agent, removes the autostart entry or user service, the files, token and log; `~/julos-uploads` stays), and with root also the root helper and `/etc/julos-agent`. Then use **Agent entfernen** in the
section Agent of the host page so the token is also dead on the server. The agent file on the machine is the only thing JulOS ever installs.

## What the agent reads

Only fixed requests, answered with JSON, never a file or a log. Each capability appears in the `hello` (`caps`) only if the machine can do it.

| Capability | Request | Reads |
| --- | --- | --- |
| `sysinfo` | `GET /v1/host/sysinfo` | `/proc/meminfo`, `/proc/uptime`, `/proc/loadavg`, `/proc/mounts` + `statvfs`, `/proc/net/dev`, `/etc/os-release`, `ip -o addr`, `/var/run/reboot-required`, `systemd-detect-virt`, Proxmox markers (`pveversion`), health (below) |
| `updates` | `GET /v1/host/updates` | `apt-get -s dist-upgrade` (a simulation, no root), `apt-mark showhold`, age of `/var/lib/apt/lists`; `dnf -C check-update`, `apk list -u`, `checkupdates` in the simplest form (no security flag there) |
| `services` | `GET /v1/host/services` | `systemctl --failed`, `systemctl is-system-running` |
| `docker` | `GET /v1/host/docker` | one request on the local Docker socket: `GET /containers/json?all=1` (also the Compose project label of each container, so the app can warn about JulOS' own group) |
| `services` + right `services` | `GET /v1/host/units` | `systemctl list-units --type=service --all`: the list of loaded services, the only source of service names for actions; needs the local word `services` (`403 not_permitted` without it) |
| (always) | `GET /v1/host/jobs` | the last 20 actions with state, progress and result, so the server can fetch a result after a lost connection (see [Progress and the way back](#progress-and-the-way-back)) |

**Updates.** Security updates are the ones whose origin carries `security` (Debian `Debian-Security`, Ubuntu `-security` and ESM). The agent never refreshes the package lists: that needs
root. It shows their **age** instead ("package lists were updated 3 days ago: `sudo apt update` on the host"), so "no updates" is never a silent lie. Most systems refresh them by a timer (`apt-daily`).

**Disk health.** SMART needs `smartctl` and permission to read the disks; a plain service user usually has none, then the card says "not read" and why. Giving the service access to the raw disks
(group `disk`) is a decision you make on the host (it lets the service read every disk); the installer does not do it. RAID state comes from `/proc/mdstat` (degraded, rebuilding with percent, inactive),
ZFS from `zpool status -x`. Disk fill level is part of the disks list; a disk at 90 % or more is marked. The first read of the SMART data runs in the background (it can take seconds); later reads use a
ten-minute memory. Btrfs and LVM are not read yet (see the roadmap).

**Docker.** For reading the agent only sends `GET /containers/json?all=1`, but **access to the Docker socket is access to root on that machine**: the `docker` group is root-equivalent. So the
default is "no access", and the card says so. Two ways: run a socket proxy and give the agent its socket (`--docker-socket` or `JULOS_AGENT_DOCKER_SOCKET`; recommended, see
[INTEGRATIONS.md](INTEGRATIONS.md) 2.1), or accept the risk for this host with `install-host-agent.sh --docker-group` (adds the supplementary group `docker` to the unit). A **read-only** proxy blocks the container actions: for them
the proxy must allow `POST /containers/<id>/start|stop|restart`, `GET /containers/<id>/json|logs` and the image pull (`POST /images/create`, `GET /images/<name>/json`); the agent answers `docker_denied` otherwise. Containers that keep restarting are counted
(`restarting`); the per-container restart counter is not in the list answer and is a roadmap item.

## Server side

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/hosts/{id}/status` | system values (`info`), session needed, 404 for a host that is not yours |
| GET | `/api/hosts/{id}/updates` | list, counts, age of the package lists |
| GET | `/api/hosts/{id}/services` | failed units |
| GET | `/api/hosts/{id}/docker` | containers |
| GET | `/api/hosts/{id}/units` | loaded systemd services (targets of actions); only when the server level of the host allows `services`, else `403 control_off` |
| POST | `/api/hosts/{id}/actions/prepare` | step 1 of an action: checks everything and returns the one-time confirmation (below) |
| POST | `/api/hosts/{id}/actions/run` | step 2: runs the confirmed action, answers `202` with the job id at once |
| GET | `/api/hosts/{id}/actions/{job}` | state, progress and result of a job (also fetches a lost result from the agent) |
| GET | `/api/hosts/{id}/audit?limit=` | the history (audit) of this host, newest first, at most 200 |
| PUT | `/api/hosts/{id}/agent/control` | `{level}`: `read` (default, no actions), `updates` (updates and logs) or `full` |
| PUT | `/api/host-actions/pause` | `{paused}`: the pause switch (below) |
| GET | `/agent-files/install-agent.sh` (and the thin callers `install-host-agent.sh`, `install-linux-agent.sh`), `/agent-files/julos-agent-helper.py`, `/agent-files/SHA256SUMS` | public like the other agent files; `SHA256SUMS` is `sha256sum` format over exactly the served bytes |

Answer shape: `{"state", "at", "stale", "error"?, "info"|"updates"|"services"|"docker"?}`. `state` is `ok`, `offline` (agent enrolled, not connected), `no_agent`, `unsupported` (the agent lacks the capability: the agent is not even asked) or `error`
(`error` is a short code such as `timeout`, `apt_failed`, `invalid_answer`; free text of the agent is never passed on). On `offline` and `error` the last good data is still returned with `stale: true`.

- **Cache, no polling.** The server asks the agent only when a browser asks it (someone is looking), keeps each answer in memory for **60 seconds** per host and kind, and lets simultaneous requests share one question.
  `?fresh=1` (the "Neu lesen" button) may read again, but not more often than every **15 seconds**. The browser asks only while the Hosts view or the Server page is drawn.
- **Allowlist.** The server sends exactly four paths (`/v1/host/sysinfo|updates|services|docker`), chosen by the endpoint, never by the browser, and only when the agent announced the capability.
- **Limits.** Answers are at most 600 kB (link limit), packages 500, containers 100, other lists 64 entries. Every field is read one by one: texts are cut and stripped of control characters, numbers clamped, wrong types dropped,
  IP addresses must parse. Hostile or broken answers cannot reach the page as markup (the front end also escapes everything).
- **Rights.** Every route needs the session cookie (ApiGuard) and shows only your own hosts; 90 requests per minute and user.
- **`GET /api/hosts`** gains, inside `agent`: `kind` (`host` or `desktop`: the platform), `caps` (the words above: `desktop`, `host`, `sysinfo`, ...), `allow` (what the agent allows locally for actions), `control` (the server-side level), `hostSupport` (`yes`, `update`, `none`, `unknown`, see "One agent per machine"); at the top `actionsPaused`. Old fields are unchanged.
  **Ask `caps`, not `kind`**: a desktop agent 0.7.0 has `kind: desktop` and also `caps: [host, sysinfo, ...]`; everything the app shows for the host abilities (values, updates, actions) follows the words.
- **Migration 017** adds `agents.control` (`read`, `updates`, `full`; default `read`); **migration 018** adds the table `host_audit` (016 is an intentional gap).

## Security

The design follows [INTEGRATIONS.md](INTEGRATIONS.md) section 3. For this agent specifically:

- **Outbound only.** The agent connects to the server over `wss://` (system certificate store, no "insecure" switch); a plain `http://` server is refused except `localhost` or `--allow-http` for tests. Nothing listens on the machine.
- **The desktop agent is a user process.** It runs as the user of the session (never root, never with `sudo`), has none of the sandbox below, and answers the host requests with the same checks. The permission file, the root helper and the words are the protection: **a word is a right for every program of that user**, because the helper's socket belongs to the user. The default is no word. Keep `reboot` and `services` off where a hostile program of your own user must not be able to reboot the machine or stop its services; `updates` and `logs` are the modest choice. `--docker-group` is not done for a desktop: the user's membership in the group `docker` is the user's own decision.
- **Own user, no shell, locked down.** `julos-agent` has no login shell and no password; the unit has `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome=read-only`, `PrivateTmp`, an empty capability set, and writes only to its own two folders.
- **No shell, no free command.** Programs are fixed argument lists (`subprocess` with a list, never a shell string); names are checked against patterns **and** against the lists the agent itself reported; the agent never gets `sudo` and keeps `NoNewPrivileges`.
  Reading uses only `apt-get -s` (simulate); everything that writes is in the [Actions](#actions-stage-3) section below and behind the keys described there.
- **The agent can't give itself rights.** `/etc/julos-agent/permissions` counts only when **root owns it and it is not writable by group or others** (otherwise it is ignored and a warning is logged). The words are `updates`, `reboot`, `services`, `docker`, `logs`; the file is empty of words after the installation.
  (The command-line flags `--allow-updates` / `--allow-reboot` still add to the **agent's** answer, but the root helper reads only the file.)
- **Two keys, plus your confirmation.** An action needs the server (`agents.control`, session, rate limit, pause switch) **and** the agent (the local file) to agree, **and** a one-time confirmation from you. The agent reports its local words in `hello` (`allow`) so the app can show why an action is not offered.
- **The agent is not the server.** Its answers are untrusted input (see "Limits" above). It cannot ask the server for anything: the server only sends fixed requests.
- **Token.** 256 bits, stored on the server as SHA-256 only, on the host in a 0600 file of the service user; removing the agent in the app kills it at once.
- **The agent file belongs to the service user** (so it can replace itself on update). Someone who already is that user could change the agent; that user has no rights beyond what is listed here, which is why it must stay unprivileged (no `docker` group by default, no `sudo` rules).
  That is also why the root helper is a **separate file owned by root**: a root process never runs code the service user can write. What that user can still do with the helper is exactly what the permission file allows (see [The root helper](#the-root-helper)).

## Actions (stage 3)

Actions are the first thing in JulOS that changes a machine, so they are built to be hard to trigger by accident, by a stolen browser tab, or by a hostile agent.

| Action (id) | Right | What the agent does | Target |
| --- | --- | --- | --- |
| `updates.refresh` | `updates` | `apt-get update` (root helper) | none |
| `updates.install` | `updates` | one package: `apt-get install --only-upgrade --no-remove`; all: `apt-get upgrade` (never removes or adds packages; kept-back packages stay for manual work) | `all` or one package **of the update list** |
| `power.reboot`, `power.poweroff` | `reboot` | answers "accepted" first, then `systemctl reboot` / `poweroff` after a short delay (root helper) | none |
| `service.start`, `service.stop`, `service.restart` | `services` | `systemctl <verb> -- <unit>` (root helper); never the agent or its helper | a service **of the reported list** (`GET /v1/host/units`) |
| `docker.start`, `docker.stop`, `docker.restart` | `docker` | one `POST /containers/<id>/<verb>` on the Docker socket the read functions already use | a container **of the container list** |
| `docker.pull` | `docker` | fetches the image of that container again (`POST /images/create`) and says whether it changed; the container keeps running the old image until it is recreated | a container of the list; not for images that are only an id or a pinned digest |
| `logs.unit`, `logs.container` | `logs` | last 10 to 200 lines (default 100, each at most 400 characters) of `journalctl --unit=<unit>` (root helper) or of the Docker logs | a service or container of the lists |

#### Logs from a list

The section **Logs** of the host page offers the services of the agent's own list (`GET /api/hosts/{id}/units`, the list `GET /v1/host/units` above) and a number of lines from 10 to 200; the button opens the usual two-step confirmation, nothing is read before it. There is **no free input** of a name: the list is the only source, and the server checks the target against the same list (`unknown_target` otherwise). Logs of **containers** and of the **failed services** keep their button at the entry.

Limit, without loosening the keys: the list needs the local word `services` on the host **and** the level "alles" on the server (`GET /units` is a service query). With only `logs` (level "Updates und Logs", or no word `services`) there is no list, and the dialog says so; the logs of failed services and containers stay available from their lists. Reading logs does not give services control.

**Output with line breaks.** The output of a program (for example `apt`) keeps its line breaks end to end (the agent sends them, the server keeps them: control characters are dropped, a tab becomes a space, at most one blank line in a row, at most 4000 characters) and the dialog shows it as text with `white-space: pre-wrap`, never as HTML.

Paths are fixed: `POST /v1/host/updates/refresh|install`, `/reboot`, `/poweroff`, `/service/start|stop|restart`, `/docker/start|stop|restart|pull`, `/logs/unit|container`. Body: `{"job": "<32 hex>", ...target}`. Answers: `200 {ok, job, action, ...}`, `400 invalid_request`,
`403 not_permitted`, `404 unknown_target`, `409 busy|duplicate_job|protected_unit|no_registry_image`, `500 <short code>` (`apt_locked`, `apt_failed`, `systemctl_failed`, `docker_failed`, ...), `501 not_implemented`, `503 helper_unavailable|docker_unavailable`.

### The confirmation (two steps, on the server)

1. `POST .../actions/prepare {action, target}` checks everything (below) and answers with a confirmation: a random token of 256 bits (the server keeps **only its SHA-256, in memory**, for **60 seconds**) and the exact content: host, action, target (with the container's name),
   warnings, and `typeToConfirm` for **reboot, power off and "all updates"**: the machine name (the name the agent reports) that you must type. Nothing has happened yet.
2. `POST .../actions/run {token, action, target, typed}` runs it. The confirmation is **single use** (it is removed on first sight, also when something does not match), bound to **your session, this host, this action and this target**, and expires. A wrong typed name does not burn it
   (the server checks the name too, not only the page). Before running, **every check is repeated**: between the steps the pause switch, the level or the connection may have changed.

The page shows a small sheet with host, action and target (and the typing field where required); after "Ausführen" it shows progress and result. Closing the dialog does not cancel a running action.

### Who must agree (the keys)

Server: the **pause switch** (`JULOS_HOST_ACTIONS=off` in the environment, or "Alle Aktionen pausieren" on the page; the environment wins), the **level** per host (`agents.control`, default `read` = no action; `updates` = updates and logs; `full` = everything), your **session**,
the **rate limit** (30 requests per minute and user, at most 20 open confirmations), an **agent of version 0.6.0 or later that is connected and announced the capability**, and the agent's **`allow`** from its hello. Agent: the **local word** in `/etc/julos-agent/permissions`
(`updates`, `reboot`, `services`, `docker`, `logs`), checked again for every request (`403 not_permitted`). The root helper checks the file **a third time**. Missing any one: refused, and the refusal is written to the history.

### One action at a time

Per host only one writing action runs (`409 busy`), on the server **and** in the agent. A job id (`job`) is used once: the same request twice does nothing (`409 duplicate_job`). After a reboot or power off the agent stays "busy" for two minutes.

### Progress and the way back

Long actions send `{"t":"progress","id":N,"text":"...","done":k,"total":n}` messages over the link (rate limited to about two per second; for updates `done` counts "Setting up" lines against the number of packages). The page polls `GET .../actions/{job}` every 1.5 s.
A lost link **does not abort the action**: the agent keeps working and stores the result (last 20 jobs in `~/.local/state/julos/jobs.json`, mode 0600, never the content of logs). The server marks the job `lost`, and as soon as the agent is back (or when the page asks) fetches `GET /v1/host/jobs`
and completes the job and the history entry. A job that was still running when the agent process died is reported as `interrupted`, never as success. Reboot and power off report **"accepted"** to the server first and run after a delay of five seconds, so the answer still arrives.
After updates the result says `rebootRequired` when `reboot-required` is set, and the page shows a hint.

### The history (audit)

Table `host_audit` (migration 018): time, user, host (id and name of that time), action, target (the container as `name (id)`), `event` and a short `detail` code. Events: `prepared` (a confirmation was requested), `denied` (with the reason: `control_off`, `paused`,
`agent_not_permitted`, `confirmation_invalid|expired|mismatch`, `typed_mismatch`, `unknown_target`, ...), `started`, `ok`, `failed` (error code), `accepted`, `lost`, and `control.set` when you change the level. **No secret and no content**: no token, no log lines, no command output.
The entry `started` is written **before** the action is sent; if it cannot be written, the action is not run (`audit_failed`). The last 500 entries per host are kept; the history survives deleting the host. It is shown in the dialog Host-Details under "Verlauf" (`GET .../audit`).

### The root helper

`tools/julos-agent-helper.py` is a small program (standard library only, about 200 lines) that runs **as root, one request per connection**, started by systemd's socket activation (`julos-agent-helper.socket`, `/run/julos-agent-helper.sock`, mode 0660, group = the service account; for a desktop agent the socket belongs to the user with mode 0600; `Accept=yes`).
It exists because the agent is deliberately an unprivileged user with `NoNewPrivileges` and a read-only file system: it cannot run `apt-get` itself, and `sudo` would not work under that sandbox (and would be a much bigger door). The helper:

- reads **one JSON line**, accepts only five verbs (`apt.refresh`, `apt.upgrade`, `power`, `service`, `journal`), checks the matching word in `/etc/julos-agent/permissions` (root-owned, not writable by others), validates every value (package pattern **without a trailing `-`**,
  which would mean "remove" to apt; unit pattern; never `julos-agent*`) and runs **exactly one fixed program as an argument list**: no shell, no `sudo`, no environment from the caller (`DEBIAN_FRONTEND=noninteractive`, `needrestart` only reports);
- streams the program's output lines back as progress and ends with a short result code; when the agent hangs up, the action **runs to the end** (a half-finished `apt` is worse than an unread result);
- is **not replaced by the agent's self-update**: it is installed and updated only by re-running `install-agent.sh` as root. The installer installs it only when you allow `updates`, `reboot`, `services` or `logs`. Without systemd it is not installed and those actions answer `helper_unavailable`.

Honest limit: the permission file is the access control for the helper, and anyone who can write to the agent's user (or to the helper's socket group) can ask for **exactly the actions the file allows, with values the helper validates**, without your confirmation in the app. That is why the user stays
unprivileged, the words are never a default, and `reboot`/`services` should be allowed only on machines where this is acceptable.

### Limits

- Installing updates and refreshing lists: **apt only**. `dpkg` may stop with a question the helper cannot answer (it keeps the existing configuration files, `--force-confold`); a held package or a broken state shows up as `apt_failed` with the output tail.
- `apt-get upgrade` does what `upgrade` does: packages that need new dependencies or removals stay "kept back" and need a manual `dist-upgrade`.
- Stopping a service that your SSH or network depends on is possible (and only a confirmation away); the agent's own units are protected.
- The JulOS server's own containers (Compose project `julos` or an image with `julos` in its name) show a **warning** in the confirmation when you stop or restart them; they are not blocked.
- The server keeps confirmations and job states **in memory**: a restart of the server forgets open confirmations (harmless) and job states (the history entry `started` then has no result entry; the agent still has it).
- Real systemd, real `apt` and real Docker are not exercised by the automated tests (they use a recorder and fakes); see the CHANGELOG for what was verified by hand.

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

- **Actions for dnf, apk, pacman** (installing updates and refreshing lists is apt only: `501 not_implemented`), **recreating containers** after an image pull (the pull only fetches the image; recreate with Compose yourself), **registry logins** for private images, **stopping the agent or its helper through itself** (`protected_unit`), **unit files** that are not loaded (only `systemctl list-units` shows what can be controlled), an **"undo"** for any action.
- **Anything unattended**: no schedule, no automatic update, no action started by the agent. That is stage 5 and needs its own opt-in.
- **Windows** agent (planned, see [AGENT_API.md](AGENT_API.md)), macOS, BSD. The host mode needs Linux.
- **Refreshing package lists silently**: `apt-get update` needs root; it is the explicit, confirmed action "Paketlisten aktualisieren" of stage 3 and never runs by itself.
- **Two agents on one host.** One host has one agent, and since 0.7.0 that one agent can do both (desktop abilities and host abilities); there is no reason for two. A machine with a desktop and a host agent installed is refused by the installer.
- **Btrfs and LVM health**, per-container restart counters, Docker stats (CPU/memory per container), log search or streaming (logs are bounded, on request).
- **A history.** Values are a snapshot; there are no graphs and no alerts yet (see "Later").
- **dnf/apk/pacman details**: no security flag, no list age, and `dnf` only from its cache.

## Roadmap

The user's decisions for the host agent (answers collected before the build) are binding for the following stages.

### Stage 1 and 2 (this version): read and show, install over SSH

Done in this version: the host mode, the four read capabilities, disk health, the endpoints with cache, the host cards and the host page with its sections, the SSH install, the permission file and the `control` level (unused).

### Stage 3: actions (write), every one confirmed, never silent: built, see [Actions](#actions-stage-3)

The binding rules of the user's decisions (kept here because later stages must keep them): always confirmed in two steps on the server and never by a schedule or the agent; two keys (server level and local permission per machine and kind of action);
an audit entry for every request, refusal and result; narrow fixed requests with checked values; progress and a way back after a lost link; the fixed message form `{"t":"req","id":N,"method":"POST","path":"/v1/host/<action>","body":{...}}` answered `403 not_permitted`, `501 not_implemented`
or the result, with `allow` in the `hello`. What was built, how, and its limits are in the [Actions](#actions-stage-3) section.

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
- `tests/agent/test_installer.py`: `install-agent.sh` in dry-run (no root, no network): the host branch as before, the desktop branch (detection by faked desktop, forced kinds, existing installation decides, socket owned by the user, no service account or sudo for a desktop, docker note), and the thin old installers (forwarding, fetching the installer with a checksum from a local server); argument checks and `shellcheck`.
- `tests/agent/test_unified.py`: the desktop agent answers `/v1/host/*` through the real link with the same checks (route table, `hello` with `caps` and `allow`, refusal without the word and nothing run, the allowed action sends exactly its request), the local socket does not know the host paths, and the self-update never touches the permission file.
- `tests/agent/test_actions.py`: every action against a **request recorder** (it proves that exactly the expected requests reach the helper and the Docker socket, and that any real program aborts the test), one refusal test per missing right, injection tests (package names like `x; rm -rf /`, trailing `-`, `=`, newlines; unit names; container ids; job ids), busy and duplicate jobs, progress, results that survive a restart,
  the helper's verbs with an injectable runner, and the agent talking to the real helper code over a real Unix socket.
- `tests/JulOS.Backend.Tests/HostActionTests.cs`: the two-step confirmation (expired, reused, wrong session, wrong host, changed action or target, typed name), the keys (level, local permission, version, offline, pause switch from the setting and from the environment), rate limit, history entries and ownership, target checks, one action per host, progress, failure codes,
  lost connection and fetching the result, failing closed when the history cannot be written.
- `tests/frontend/hostactions.test.mjs`: when buttons appear, the confirmation text, typing the machine name, progress and result, error texts, the history, HTML escaping, and that the browser knows no other way than prepare then run.
- `tests/JulOS.Backend.Tests/HostInfoTests.cs`: endpoints with a fake agent: all four reads, cache and refresh limits, simultaneous requests, offline and stale, no agent, unsupported, error codes, hostile data, session and ownership, migration 017 and the control check.
- `tests/frontend/hostinfo.test.mjs`, `hosts.test.mjs`: formatters, card lines, HTML escaping, the store, the install command, the choice of the terminal connection.
- `tests/JulOS.Backend.Tests/UnifiedAgentTests.cs`: a desktop agent with `caps` host shows values and allows actions with the same keys; an old desktop agent (0.5.x/0.6.0) stays valid, gets no host request and shows `hostSupport: update`; the support rule; the installer is served with a checksum.
- By hand: the installer in a throw-away `debian:trixie` container without desktop against a test instance; the agent connects, status and updates show up (see the CHANGELOG entry for what was verified).
