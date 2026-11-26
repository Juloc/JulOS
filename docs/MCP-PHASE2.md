# MCP phase 2: proposals with the owner's approval (build plan)

Status: **plan for the build** (2026-11-26). Builds on phase 1 (branch `claude/mcp-stufe-1-fdbb66`, see [MCP.md](MCP.md)). Written by the JulOS chief chat for a build session; every decision below is made. If something here does not fit the code, ask the chief before choosing.

## Goal in one sentence

An agent may ask for one of the existing host actions; the owner gets a push message, opens a sheet in his own session, taps *Erlauben* (with the usual second factor and confirmation level) or *Ablehnen*; only then the server runs the action exactly as if the owner had started it; the agent reads the result.

## What exists and must be reused, not copied

- `HostActionService.PrepareAsync(who, hostId, action, target, lines, ct)` and `RunAsync(who, hostId, token, action, target, typed, acknowledged, full, ct)`: all checks (pause switch, control level, agent version and capability, the agent's `allow`, unknown target, one action at a time), the confirmation token (60 s, single use, bound to session, host, action, target), the confirmation level (`routine`, `dangerous`, `critical` with `click`, `check`, `type`), the audit (`host_audit`, fail-closed).
- `GetJobAsync` and `/api/host-jobs` for progress and results.
- Push: `PushStore.SubscriptionsAsync`, the Web Push sender used by the alert rules, `AddLogAsync` for the message list. A proposal message is a new kind next to the alert kinds, same delivery, same quiet hours **do not apply** (a proposal is something the owner is waiting for; say so in the settings text).
- Step-up: `StepUpPolicy` (the approve route needs the fresh second factor like `actions/prepare`).
- Phase 1: `McpGateway`, `McpTools`, `McpClient.CanRead`, `McpRedact`, `mcp_audit`.

## Levels

`mcp_client_hosts.level` already allows `propose`. Phase 2 makes it selectable and effective:
- `read`: as in phase 1.
- `propose`: everything of `read`, plus the three tools below. `CanRead` is true for `read` and `propose`; a new `CanPropose` only for `propose`.
- `command` stays refused by the server (phase 3).
- Settings page: the level select per host gets *Vorschlagen*; the help text says in one line that every proposal waits for the owner's tap.

An agent never sees a level higher than the host's server-side control level: if the host's `agents.control` is `read`, the client's `propose` has no effect for that host and `action_propose` answers `not_permitted` (same code the action service uses).

## Data (migration `030_mcp_proposals.sql`; 028 and 029 are taken)

```
mcp_proposals
  id            uuid primary key default gen_random_uuid()
  user_id       uuid not null references users(id) on delete cascade
  client_id     uuid references mcp_clients(id) on delete set null
  client_name   text not null                 -- kept when the client is deleted
  host_id       uuid not null references hosts(id) on delete cascade
  host_name     text not null
  action        text not null                 -- an id of the action list (updates.refresh, docker.restart, ...)
  target        text                          -- as the action service expects it (container id, unit, package or null)
  target_name   text                          -- what the owner sees (container name, unit)
  lines         int                           -- only for logs
  reason        text                          -- the agent's short reason, at most 200 characters, shown as data
  state         text not null check (state in ('waiting','approved','denied','running','ok','failed','expired','cancelled'))
  job           text                          -- the job id of the action service once it runs
  result_code   text                          -- ok or the error code of the action
  created_at    timestamptz not null default now()
  decided_at    timestamptz
  expires_at    timestamptz not null          -- created_at + 24 h
create index on mcp_proposals (user_id, created_at desc);
create index on mcp_proposals (client_id, state);
```

Keep the last 500 per user (prune on insert, like `mcp_audit`).

## Tools (added to `McpTools.Defs`; `tools/list` shows them only if the client has `propose` on at least one host)

| Tool | Input | What happens |
|---|---|---|
| `action_propose` | `host`, `action`, `target` (optional), `reason` (optional, 200 chars) | 1. Resolve host as in phase 1 (not found if not `propose`). 2. `action` must be one of the action ids of `HostActionService`; `target` must be in the host's current list (same check as `prepare`, run it with a **server identity of the owner** but do not keep the token). 3. At most 5 waiting proposals per client and 20 per user (`too_many_proposals`). 4. Store the proposal (`waiting`, 24 h), send the push message, write `mcp_audit` (`action_propose`, host, `waiting` or the refusal code). Answer `{proposal, state:"waiting", expiresAt}`. |
| `action_state` | `proposal` | The proposal of this client only (else `not_found`). Answer state, decided time, and once a job exists: the job state, progress `done/total`, the last output lines (`tail`, max 20 lines, through `McpRedact`), result code, `rebootRequired`. |
| `logs_read` | `host`, `target` (a service of the units list or a container), `lines` 10–200 | A proposal for `logs.unit` or `logs.container`, unless the client has *Logs ohne Nachfrage* for that host (a per-host flag in `mcp_client_hosts`, new column `logs_free boolean not null default false`, settable in the settings page, default off). Free logs run at once through the action service as the owner would and answer the lines (redacted, capped); a proposal answers like `action_propose`. |

Never: a tool that approves, runs, cancels for the owner, or changes a level. `action_propose` with `reboot` or `poweroff` on the host JulOS itself runs on is refused (`not_permitted`), so an agent cannot ask to switch off the machine that would have to approve it.

## The owner's side

- **Push message:** title `KI-Agent fragt: <action in German> auf <host>`, body `<client name>: <target name>` plus the reason in quotes, tap opens `#/mcp/proposal/<id>`. Kind `mcp.proposal` in the message list.
- **Sheet** (desktop: a small window like the action sheet; phone: a page): host, action, target, the client's name, the reason **marked as text from the agent**, its age, and the same warning block, tick or name field as the action sheet for the action's level. Buttons *Erlauben* and *Ablehnen*.
- **Routes** (behind the session, `ApiGuard` as usual):
  - `GET /api/mcp/proposals?state=waiting` (own only)
  - `GET /api/mcp/proposals/{id}`
  - `POST /api/mcp/proposals/{id}/approve {acknowledged?, typed?, full?}`: needs the fresh second factor (`StepUpPolicy`). Inside: re-check the proposal is `waiting`, not expired, belongs to the user; then call `PrepareAsync` and `RunAsync` **with the owner's session identity**, in one request, passing `acknowledged`, `typed`, `full` from the owner's tap. Any denial from the action service becomes `failed` with its code. Success: state `running`, store the job id.
  - `POST /api/mcp/proposals/{id}/deny`
- **Settings, KI, Zugang für KI-Agenten:** a section *Offene Vorschläge* (list with open sheet) and the last 20 decided ones.
- **Background:** a 1-minute timer marks `waiting` past `expires_at` as `expired`; job completion updates `running` to `ok` or `failed` (poll `GetJobAsync`-equivalent internally or hook where the job ends; pick the one that needs less new code and say which in the commit).

## History

- `mcp_audit`: `action_propose`, `action_state`, `logs_read` with the result codes.
- `host_audit`: unchanged path through the action service; the entry of the run carries the owner as user (he approved). Add the client name to `detail` as `via mcp:<client name>` so the history shows who asked.
- Security history: approve and deny (`mcp.proposal.approve|deny`, proposal id and action, never output).

## Tests (security-critical, automated; extend `McpTests`)

1. `read` client: `tools/list` has no propose tools; calling `action_propose` gives `not_permitted`.
2. `propose` client on a host with control `read`: `not_permitted`.
3. A proposal never runs without the owner's approve call; the client's key on `/api/mcp/proposals/...` is refused (no session).
4. Approve needs the second factor; without it `403 step_up_required`, proposal stays `waiting`.
5. Approve by a different user: 404. Approve twice: second is refused. Approve after expiry: refused, state `expired`.
6. A dangerous action without `acknowledged` (mode `check`): refused with `confirm_required`, state stays `waiting` so the owner can tap again.
7. Target not in the list: refused at propose time.
8. Limits: 6th waiting proposal of a client is refused.
9. Reboot of the JulOS host: refused.
10. `action_state` of another client's proposal: `not_found`.
11. Logs: without `logs_free` it is a proposal; with it the lines come back redacted and capped.
12. Every path writes `mcp_audit`; no output text in `mcp_audit` or the security history.

Frontend: one small test for the sheet's warning mode selection (pure logic), nothing for plain display.

## Docs in the same commits

`docs/MCP.md` (phase 2 built, what is left: phase 1b and 3), `docs/HOST-AGENT.md` (actions can be proposed by an agent; the run is the owner's), `docs/PUSH.md` (the new kind, not muted by quiet hours), `docs/SECURITY-REVIEW.md` (a section for phase 2), README line, CHANGELOG under *Unveröffentlicht*, i18n de and en, service worker version +1 from the base branch.

## Done means

- Tests green (backend suite once through `devq`, frontend tests).
- A run by hand on localhost with throwaway values: `propose` client, `action_propose docker.restart` on the dev stack's test container, the push message appears in the message list, approve in the browser, the container restarts, `action_state` shows `ok`. Report the real answers shortened.
- Not verified, say so: the browser tests (they need `/dev/fuse`, which the dev container gets with its next restart), a real phone with push, a real Claude Code client.
