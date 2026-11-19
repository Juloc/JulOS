# Hosts

A **host** is one machine or server you run: it has a name, its own saved connections and at most one agent. Before hosts, JulOS
saw one machine; now the desktop interface and the phone shell list several, each with its own status.

## Model

| Thing | Belongs to | Notes |
| --- | --- | --- |
| Host (`hosts`) | one user | name (1 to 60 characters), at most 20 per user |
| Connection (`connections.host_id`) | one host | RDP, SSH and so on; a connection is a **service** of its host |
| Agent (`agents.host_id`) | one host | one per host, serves all its connections; enrolment code and token are per host |

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
  `JULOS_ALLOWED_HOSTS`; a target that does not is not probed and `online` is left out (shown as "unbekannt", grey).
- **Host** (`online`): the agent link is connected **or** at least one service answers. `agent` is absent when none is enrolled;
  `agent.online` is the live link, `lastSeen` the last report.
- **Operating system and name** (`os`, `hostname`) come from the agent (its `hello` and enrolment). Values are display only: up to 64
  characters of letters, digits, spaces and `. - _ + / ( ) ,`; anything else is dropped, and the last good value stays when the agent is away.
- The browser refreshes the state every 30 s while the page is visible (the server's 15 s memory keeps this light).

The status dot is **green** (online) or **grey** (offline or unknown), per host and per service.

## Endpoints

All need the session, the `X-JulOS` header for changes, and only ever show the signed-in user's own hosts (a foreign id is `404`).

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/hosts` | list with state, see above |
| POST | `/api/hosts` `{name}` | new host |
| PUT | `/api/hosts/{id}` `{name}` | rename |
| DELETE | `/api/hosts/{id}` | `409` while it has connections |
| POST | `/api/hosts/{id}/agent/code` | one-time enrolment code (10 minutes, once) for this host's agent |
| DELETE | `/api/hosts/{id}/agent` | remove the agent, close its link |
| GET | `/api/agent/*`, `POST /api/upload*`, `POST /api/clipboard/pull` | `?c=<connection id>` (its host's agent) or `?h=<host id>` |

`GET /api/connections` and connection requests carry the host as `hostId` (the field `host` stays the address). The old
`/api/connections/{id}/agent/*` and `/api/agents` are gone: the agent belongs to the host now.

## In the app

- **Desktop interface:** menu **Hosts** (list with state, manage, new), dock icon **Hosts**, and the Start window's view **Hosts**: one **host card** per host (below). In **Dienste** a host selection plus services grouped by host once there is more than one host. The Start window opens at about 82 % of the screen (at least 960 x 640, at most 1280 x 860); a size you chose yourself is remembered.
- **Phone:** page **Hosts** shows the same host cards below the server's load; **Dienste** gets a host selection when there is more than one host, and every tile has its status dot.
- **Connection dialog:** a **Host** field (with "Neuer Host …"); the **host dialog** holds the name, delete and **Agent einrichten / entfernen** for that host.

### The host card

One card for phone and desktop (`hostview.js`, styles `hostui.css`, building blocks in [ui-kit.html](ui-kit.html) "Hosts"):

| Part | Shows |
| --- | --- |
| Header | name, host name and system, state pill (online or offline) |
| Kind | "Host-Agent", "Desktop-Agent" or "Kein Agent", with the agent version; a disconnected agent says when it was last seen |
| Key figures | CPU load (per core), RAM, the main disk ("/" or the fullest) with a bar each; yellow from 80 %, red from 90 %; only for a connected host agent that reports system values |
| Notes | only when there is something: updates (security ones red), reboot needed, failed services, a disk problem, an outdated agent; plus the uptime |
| Connections | the host's connections as chips with a status dot; a tap connects |
| Actions | the one calm main action **Details** (or **Agent verwalten** without readable data); in the menu **Agent verwalten** and **Bearbeiten** |
| Empty | a host without an agent explains what the agent brings and offers **Agent einrichten**; a desktop agent says that the host agent reads system values; a disconnected agent says that values return when it reports in |

The numbers come from the cards' own memory (at most one read per host every 55 seconds, only while a card is drawn); nothing here polls the server more than before.

## Host agent

A host without a desktop (Docker VM, Proxmox, servers) gets the **host-mode agent**: `GET /api/hosts/{id}/status`, `/updates`, `/services`, `/docker` (session needed, 60 s memory, never polled by the server) show load, memory, disks, disk health, updates and containers on the host card and in the dialog **Host-Details**. `agent` in `GET /api/hosts` gains `kind`, `caps`, `allow` and `control`. Install it from the host dialog with **Agent per SSH installieren**. Since agent 0.6.0 the dialog can also run **actions** (install updates, reboot, services, containers, logs, images): off by default, switched on per host, every single one confirmed by you in the app, with a history; see [HOST-AGENT.md](HOST-AGENT.md#actions-stage-3). All of it: [HOST-AGENT.md](HOST-AGENT.md).

## Not part of this step

A Windows agent, groups or tenants, roles, and sharing a host between users. The agent's Unix-socket mode still serves the machine the
server runs on: a host without an agent uses it, as before, so several hosts on different machines each need their own agent.
