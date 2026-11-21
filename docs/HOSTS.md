# Hosts

A **host** is one machine or server you run: it has a name, its own saved connections and at most one agent. Before hosts, JulOS
saw one machine; now the desktop interface and the phone shell list several, each with its own status.

## Model

| Thing | Belongs to | Notes |
| --- | --- | --- |
| Host (`hosts`) | one user | name (1 to 60 characters), at most 20 per user |
| Connection (`connections.host_id`) | one host | RDP, SSH and so on; a connection is a **service** of its host |
| Agent (`agents.host_id`) | one host | one per host, serves all its connections; enrolment code and token are per host |
| Login (`hosts.login_*`) | one host | optional **one user and one password** for every app of the host: terminal, Files over SFTP, desktop connections (see [Login](#login-one-user-and-password-per-host)) |

- A user always has a host: the first connection (or the first `GET /api/hosts`) creates **Mein Rechner** when there is none.
  A new connection without a host goes to the user's first host.
- **Migration 008** moves the existing data without loss: every user with connections gets one default host holding all of them.
  Until now an agent hung on one connection; a user with several agents keeps the most recently seen one on the default host, and
  each further agent gets its own host (named after its connection, the connection moves there), so no token is lost and the agent
  keeps working. Open one-time codes are dropped (they live 10 minutes).
- A host with connections cannot be deleted (`409`): move or delete the connections first, so none is lost on the side. Deleting a host
  removes its agent and closes the link.
- Moving a connection to another host (`PUT /api/connections/{id}` with `hostId`) changes which agent serves it. Both hosts must be the user's own.

## Status

`GET /api/hosts` (session needed, 30 per minute and user) returns every host with its state:

```json
{ "hosts": [ { "id": "…", "name": "Heimserver", "online": true, "hostname": "werkbank-01", "os": "Debian GNU/Linux 13 (trixie)",
    "agent": { "online": true, "lastSeen": "…", "version": "0.3.0", "platform": "linux-x11", "outdated": false, "latest": "0.4.0", "updateAvailable": true, "canUpdate": false, "autoUpdate": false },
    "services": [ { "connection": "…", "kind": "terminal", "online": true } ] } ] }
```

- **Service** (`services[].online`): the server opens a TCP connection to the saved address and port (1.5 s, nothing is sent, closed at once) and
  remembers the answer for 15 s (`TcpProbe`, at most 8 probes at a time). Targets come only from the user's saved connections and must pass
  the allowed target hosts (Settings, Security; fallback `JULOS_ALLOWED_HOSTS`); a target that does not is not probed and `online` is left out (shown as "unbekannt", grey).
- **Host** (`online`): the agent link is connected **or** at least one service answers. `agent` is absent when none is enrolled;
  `agent.online` is the live link, `lastSeen` the last report.
- **Operating system and name** (`os`, `hostname`) come from the agent (its `hello` and enrolment). Values are display only: up to 64
  characters of letters, digits, spaces and `. - _ + / ( ) ,`; anything else is dropped, and the last good value stays when the agent is away.
- The browser refreshes the state every 30 s while the page is visible (the server's 15 s memory keeps this light).

The status dot is **green** (online) or **grey** (offline or unknown), per host and per service.

## Login (one user and password per host)

Before 0.16 every connection kept its own user name and password, so the terminal, the Files app and the desktop of the same machine each asked for the same thing. Now a host can have **one login**
(user name and optional password); every connection of the host uses it, unless that connection says otherwise. Root is **not** a second stored login: root actions and root files go through the agent
([HOST-AGENT.md](HOST-AGENT.md), word `files-root`) and need no password, a root terminal is `sudo -i` typed by you (decision of 2026-11-20, [FILES-EXPLORER.md](FILES-EXPLORER.md)).

**Which login a connection uses** (`LoginResolver`, one place for terminal, Files and desktop, tested):

1. the connection's **own login**, when it has the tick **Eigenen Benutzer verwenden** (`own_login`, default off): its own user and its own saved password, as before;
2. otherwise the **host login**, when the host has one: its user, and its password if one is saved. A password typed for a single attempt (the dialog, the Files question) always wins over a saved one;
3. otherwise what the connection itself holds, as before (its user, its saved password, or the question).

For a desktop (RDP, VNC) connection the host login gives the user name; the **domain** stays at the connection. The user of the connection's login dialog during a session still overrides for that attempt.

**The password belongs to the addresses of the host** (`login_targets`): it is bound to the addresses (lower case, without the port) of the connections the host had when the password was saved. A connection with
another address does **not** get it and falls back to rule 3 (the host page marks it "neue Adresse: Passwort hier neu speichern"). Without this, anyone with a session could add a connection to a foreign address
on a host and let JulOS send the host password there. Saving the password again (on the host page) binds it to the addresses of that moment; saving it from the Files question binds it to the address of that one connection.

**Changing the login:**
- A new password replaces the old one. The same user without a password in the request keeps the saved one; `forgetPassword` ("Passwort nicht speichern") keeps only the user. **Changing the user drops the
  saved password** (it belonged to the old user, as with a connection). The password is never returned: the API says `hasPassword`.
- The **first** time a host gets a login, connections with a *different* saved user name keep their own login (`own_login` is set; the answer says how many: `pinned`), so nothing that works today stops working
  by the side. Later changes of the login do not touch connections.
- Changing or removing the login **closes the pooled SFTP sessions** of that host's connections (the Files app then uses the new data). Running terminals and desktops are not closed. The **host key** pins
  are untouched (the check is not reset). Deleting the host deletes its login.
- Every change is written to the **history** (`host_audit`, action `login.set` or `login.remove`, detail only what changed: `user+password`, `password`, `user`, `password removed`, never a value) **before** it is made;
  if the entry cannot be written, nothing changes (`503`). The key `JULOS_SECRET_KEY` / `secret.key` is the same as for connection passwords; there is no key rotation here.

**Storage:** the password is encrypted exactly like a connection password (AES-256-GCM, `SecretBox`), with the **host id** as associated data (a ciphertext cannot be moved to another host). `hosts.login_user`,
`hosts.login_secret`, `hosts.login_targets`, `hosts.login_checked`, `connections.own_login` (migration `020`). Backups hold them like connection passwords (the dump carries the ciphertext, the key copy decrypts it: [SECURITY-REVIEW.md](SECURITY-REVIEW.md) F-01).

**Takeover of existing data (`HostLoginMigration`, at server start, idempotent, nothing is deleted or re-encrypted):** for every host not yet checked (`login_checked`): if all connections with a saved password and a user
name have **the same user**, that user becomes the host login, with the password of the first of them (a copy encrypted for the host). Connections with the same user and password now show "nutzt den Host-Login";
every other connection keeps its own login (`own_login`): same user but another password, another user, no user name. A connection with the same user and **no** saved password (it asked every time) now uses the host password.
With **several users** (or no saved password) nothing is set: the host page shows the **suggestion** of the most frequent user (terminal first) and sets nothing by itself. A password that cannot be decrypted (wrong or missing key)
leaves the host unchecked and it is tried again at the next start. The connection passwords stay valid as they were, so removing the host login brings back the old behaviour exactly.

**In the app:** a section **Login** on the host page (phone and desktop, no agent needed): user, password field (**never pre-filled**), "Passwort nicht speichern", **Speichern**, **Login entfernen** (second tap), the
suggestion, the root note ("Für Root-Aktionen brauchst du hier kein Passwort: Der Agent erledigt sie. Ein Root-Terminal öffnest du mit sudo -i."), and a list of the host's connections with what each uses.
In the **connection dialog** a host with a login shows the tick **Eigenen Benutzer verwenden** (off by default); off: user and password fields are hidden and a line says the host login applies.
`GET /api/hosts` carries `loginUser` and `loginPassword`, `GET /api/connections` carries `ownLogin`, `loginSource` (`host`/`own`), `loginUser`, `loginPassword` (a password is ready, nothing will be asked), `loginAddressNew`.

## Endpoints

All need the session, the `X-JulOS` header for changes, and only ever show the signed-in user's own hosts (a foreign id is `404`).

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/hosts` | list with state, see above |
| POST | `/api/hosts` `{name}` | new host |
| PUT | `/api/hosts/{id}` `{name}` | rename |
| DELETE | `/api/hosts/{id}` | `409` while it has connections; the login goes with the host |
| GET | `/api/hosts/{id}/login` | the host login (never the password) and what each connection of the host uses; 60 a minute |
| PUT | `/api/hosts/{id}/login` `{user, password?, forgetPassword?}` | set or change the login, 20 a minute |
| DELETE | `/api/hosts/{id}/login` | remove the login (the connections keep their own data) |
| POST | `/api/hosts/{id}/agent/code` | one-time enrolment code (10 minutes, once) for this host's agent |
| DELETE | `/api/hosts/{id}/agent` | remove the agent, close its link |
| GET | `/api/agent/*`, `POST /api/upload*`, `POST /api/clipboard/pull` | `?c=<connection id>` (its host's agent) or `?h=<host id>` |

`GET /api/connections` and connection requests carry the host as `hostId` (the field `host` stays the address). The old
`/api/connections/{id}/agent/*` and `/api/agents` are gone: the agent belongs to the host now.

## In the app

- **Desktop interface:** menu **Hosts** (list with state, manage, new), dock icon **Hosts**, and the Start window's view **Hosts**: one **host card** per host (below); **Details** or a note on it opens the host page as a view of the same window, with an arrow back. In **Dienste** a host selection plus services grouped by host once there is more than one host. The Start window opens at about 82 % of the screen (at least 960 x 640, at most 1280 x 860); a size you chose yourself is remembered.
- **Phone:** page **Hosts** shows the same host cards below the server's load; **Dienste** gets a host selection when there is more than one host, and every tile has its status dot.
- **Connection dialog:** a **Host** field (with "Neuer Host …"); the **host dialog** (**Bearbeiten**) holds only the name and delete. The agent has its place on the host page, section **Agent**: the one flow **Agent einrichten** (what the agent may do as ticks, command to copy or install over SSH), update, automatic update, remove.

### The host card

One card for phone and desktop (`hostview.js`, styles `hostui.css`, building blocks in [ui-kit.html](ui-kit.html) "Hosts"):

| Part | Shows |
| --- | --- |
| Header | name, host name and system, state pill (online or offline) |
| Kind | "Host-Agent", "Desktop-Agent" or "Kein Agent", with the agent version; a disconnected agent says when it was last seen |
| Key figures | CPU load (per core), RAM, the main disk ("/" or the fullest) with a bar each; yellow from 80 %, red from 90 %; only for a connected host agent that reports system values |
| Notes | only when there is something: updates (security ones red), reboot needed, failed services, a disk problem, an outdated agent; plus the uptime. The notes are buttons: the updates note ("14 Updates · 8 davon Sicherheit") opens the page at **Updates ansehen und installieren**, the others at their section |
| Connections | the host's connections as chips with a status dot; a tap connects |
| Actions | the one calm main action **Details**; the menu has **Paketlisten aktualisieren**, **Updates installieren**, **Neu starten**, **Ausschalten**, **Logs ansehen** (greyed out with the reason and the way to unlock when the level, the pause or the local word forbids it; each goes through the confirmed flow), then **Agent verwalten** and **Bearbeiten** |
| Empty | a host without an agent explains what the agent brings and offers **Agent einrichten**; an agent that reports no capabilities (for example an older desktop agent) says "Dieser Agent liefert noch keine Systemwerte" with the button **Agent aktualisieren / einrichten**; a disconnected agent says that values return when it reports in. Which sections show data follows the `caps` the agent reports, not its platform name |



The numbers come from the cards' own memory (at most one read per host every 55 seconds, only while a card is drawn); nothing here polls the server more than before.

## Host agent

**One agent per machine, with or without a desktop.** A machine without a desktop (Docker VM, Proxmox, servers) runs the agent in **host mode**; a machine with a desktop runs it in **desktop mode**, which since agent 0.7.0 also does everything the host mode does (no second agent, no second host). The agent says what it can in the `hello` (`caps`: `desktop`, `host`, `sysinfo`, ...); `agent.hostSupport` in `GET /api/hosts` says whether the host abilities are there (`yes`), would come with an update of an old desktop agent (`update`: "liefert noch keine Systemwerte"), are not available (`none`) or unknown while the agent is away (`unknown`). One installer for both: `install-agent.sh` finds out what the machine is.
The host abilities: `GET /api/hosts/{id}/status`, `/updates`, `/services`, `/docker` (session needed, 60 s memory, never polled by the server) show load, memory, disks, disk health, updates and containers on the host card and on the host page **Host-Details**. `agent` in `GET /api/hosts` gains `kind`, `caps`, `allow` and `control`. Install it from the host dialog (set-up of the agent, or **Agent per SSH installieren**); the command is the same for both kinds of machine (`install-agent.sh`). Since agent 0.6.0 the dialog can also run **actions** (install updates, reboot, services, containers, logs, images): off by default, switched on per host, every single one confirmed by you in the app, with a history; see [HOST-AGENT.md](HOST-AGENT.md#actions-stage-3). All of it: [HOST-AGENT.md](HOST-AGENT.md).

## Not part of this step

A Windows agent, groups or tenants, roles, and sharing a host between users. The agent's Unix-socket mode still serves the machine the
server runs on: a host without an agent uses it, as before, so several hosts on different machines each need their own agent.

Every SSH connection of a host is also a place in the **Files app**: browse, view, download and (writing only after you switch it on in Settings, Security) change the files of the SSH user over SFTP, with the host login (or the connection's own); see [FILES.md](FILES.md).

## Updates and background tasks

Actions on a host (updates, repair, restart, Docker pull) run as **background tasks**: the confirmation is a small sheet, then the action runs without blocking anything. The desktop shows an entry in the menu bar with progress ("Updates installieren auf pve: 12 von 63"), the phone a bar under the header; a click opens the panel with the live output, the error summary and the folded log. `GET /api/host-jobs` brings running and just finished tasks back after a reload; the details are in [HOST-AGENT.md](HOST-AGENT.md#background-tasks).

A **Proxmox** host is updated as a whole (`dist-upgrade`, no single packages); other hosts get a plain `upgrade` with the explicit option "full-upgrade" for packages that `upgrade` holds back. Half configured packages (`dpkg --audit`) show in the section Updates with the button **Reparieren**. The disk health card shows SMART (with the word `disks`), software RAID and ZFS; "ZFS not readable" is said as such and is not a problem.
