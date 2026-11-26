# Access for AI agents (MCP): plan, not built

Status: **planned** (owner's wish 2026-11-26). Nothing is built. This opens the tool server that [ASSISTANT.md](ASSISTANT.md) plans for the assistant ("exposing it to the outside is later; it is the same server") to an outside client: Claude Code on a development machine, with a key of its own.

## Why

Today an agent that has to look at a host drives the JulOS web page in a browser, or gets a shell on the machine. Both are the wrong tool: the page is made for people, and a shell is far more power than "how full is the disk". JulOS already knows every host through its agents ([HOST-AGENT.md](HOST-AGENT.md)) and already has the rules for changing one (keys, confirmation, history). An agent should use exactly that, through one small door.

## What it is

```
Claude Code ──(key of the client)──▶ JulOS /api/mcp/v1 ──▶ host agents (the existing link)
                                       │
                                       └─ same keys, same confirmation, same history as the page
```

- **Off by default.** One switch in Settings, *Zugänge für KI-Agenten*. While it is off the path answers `503 mcp_disabled`.
- **A client** is registered in Settings like an app of the AI gateway ([AI-GATEWAY.md](AI-GATEWAY.md)): a name, a key (256 bits, shown once, stored as SHA-256), the hosts it may see, a level per host, an optional end date. It can be blocked, re-keyed or deleted at once. Every change needs the second factor and is written to the security history.
- **The protocol** is MCP over HTTP: `POST /api/mcp/v1` with JSON-RPC (`initialize`, `tools/list`, `tools/call`). Stateless, no stream in phase 1. Written by hand in the server (three methods), no SDK.
- **The second reasoned exception** to "every API needs a session", next to `/api/ai/v1`: `ApiGuard` lets exactly this path through and the endpoint checks the key. The route list test covers it.

## Levels per client and host

| Level | What the client may do |
|---|---|
| `none` (default) | the host does not exist for this client |
| `read` | the read tools below |
| `propose` | read, and ask for an action; **the owner approves each one** |
| `command` (phase 3) | read-only commands from the checked list run by themselves; everything else is a proposal |

A client can never raise its own level, change settings, manage keys, touch the pause switch, or see a stored password, a token or the server key. No tool exists for any of that.

## Tools

**Phase 1, read** (everything the host page already shows, same data, same limits):

| Tool | Answer |
|---|---|
| `hosts_list` | the client's hosts: name, online, system, agent version, what the agent can do |
| `host_status` | load, memory, disks, restart needed, health (`sysinfo`) |
| `host_updates` | pending updates, how many are security updates |
| `host_services` | failed and listed services |
| `host_docker` | containers with image, state and Compose project |
| `host_history` | the last entries of the action history (codes only, as stored) |
| `alerts_recent` | the last push messages ([PUSH.md](PUSH.md)) |

**Phase 2, propose** (the existing actions of [HOST-AGENT.md](HOST-AGENT.md#actions-stage-3), unchanged):

| Tool | What happens |
|---|---|
| `action_propose {host, action, target}` | the server checks everything `prepare` checks, stores a **proposal** and sends the owner a push message with host, action and target. Answer: `{proposal, state: "waiting"}` |
| `action_state {proposal}` | `waiting`, `approved`, `denied`, `running` (with the last output lines), `ok`, `failed`, `expired` |
| `logs_read {host, target, lines}` | logs of a service or container of the lists (10 to 200 lines); a proposal like any action unless the client has "logs without asking" for that host |

The owner opens the message, sees the usual sheet (host, action, target, warning by level) **in his own session**, and taps *Erlauben* or *Ablehnen*. Only then the server issues the confirmation and runs the action, exactly as if he had started it on the page: second factor, level of the action, pause switch, the agent's local word and the history apply unchanged. **The client cannot run anything itself; there is no tool for it.** A proposal is bound to the exact host, action and target, shows its age, is re-checked against the host's state before it runs, and is single use.

**Phase 3, commands** (together with the assistant's phase 2, one implementation): a free command on a host through a new helper verb with a fixed argument vector; modes "ask", "safe auto" (the short checked list: `df`, `free`, `uptime`, `systemctl status`, `journalctl` with a line limit, `docker ps`) and a time-boxed release by the owner; root only one command at a time with its own confirmation. Needs its own review.

**Later, each its own plan:** rolling out a stack (pull, recreate, health check) as one confirmed action, which would replace the clicks in the Compose manager; files through the Files tools; Proxmox depth.

## Connecting a client

1. Settings, *Zugänge für KI-Agenten*, *Neuer Zugang*: name, hosts, levels. JulOS shows the key once.
2. On the development machine the owner adds the server himself (the key is typed by him, never by an agent, and never appears in a chat):

```bash
claude mcp add --transport http julos https://julos.example/api/mcp/v1 --header "Authorization: Bearer $JULOS_MCP_KEY"
```

**One click instead (phase 1b):** *Mit JulOS verbinden*. A small program on the development machine asks JulOS for a pairing code and shows it; the owner confirms the code in JulOS and picks hosts and levels; the program receives the key and writes it into the local configuration (file mode 0600). Nobody copies a key. The same pairing can later serve the apps of the family.

## Security

- **Same keys as the page.** Nothing in this plan loosens the pause switch, the level of a host (`agents.control`), the agent's local word, the confirmation levels or the history. The client is one more requester, never an approver.
- **The key** opens `/api/mcp/v1` and nothing else. Wrong keys are limited per address before a key is checked (as in the AI gateway); a rate limit per client; at most a few open proposals per client.
- **What a tool returns** is capped (lines, characters) and passed through the same redaction as the assistant plans (anything that looks like a password, token or key is cut). Stored logins and the server key are never part of an answer.
- **Text from a host is data.** Container names, log lines and package names come from the machine and may be written by someone else. Tool answers carry them as plain fields; no tool acts on text found in an answer, and a proposal shows the owner the exact target, not a description by the model.
- **History.** Every call writes an entry (`mcp.call`: client, tool, host, result code; never an argument's content or an answer). Proposals write the events the actions already write, with the client's name next to the owner's.
- **What "read" gives away:** host names, addresses, container names and versions of everything the client may see. A client with `read` on all hosts knows the whole installation. Give a client only the hosts it needs, and never put what it read into a public repository.
- **Tests** (security-critical, so automated): no key and wrong key, a blocked client, a host outside the client's list is "not found", no tool for running or approving, a proposal cannot be approved with the client's key, an approval of an older proposal for a changed target is refused, no stored secret in any answer, the route list.

## Phases

| Phase | Content | Done means |
|---|---|---|
| 1 | switch, clients and keys in Settings, the endpoint, the seven read tools, history | Claude Code on the development machine lists the hosts and reads their state without a browser |
| 1b | pairing by code | a client is connected without anyone copying a key |
| 2 | proposals with push and the approval sheet, `logs_read` | an agent asks to restart a container; the owner taps *Erlauben* on the phone; the result comes back to the agent |
| 3 | commands (with the assistant) | see above; own review first |

## Not covered

No effort estimate. The assistant itself ([ASSISTANT.md](ASSISTANT.md)) is a separate plan that will use the same tools. This plan moves the order in [BACKLOG.md](BACKLOG.md) ("only fixes until the shared base"): the owner asked for it directly.
