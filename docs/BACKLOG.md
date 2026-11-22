# Backlog and order of work

Kept short on purpose. The owner decides what moves up. New ideas go to [IDEAS.md](IDEAS.md); this page is the order. Last agreed: 2026-11-20 (a week with few tokens: one piece of work at a time, targeted tests, short reports).

## Principle (owner's wish, 2026-11-20)

**Settings live in the app, not in Compose files or environment variables**, wherever that is possible. An environment variable stays as a default or an override for the person who runs the server, but every setting that matters to the owner has a place in the settings page, is stored in the database and takes effect without a restart. The same goes for **updating JulOS itself from the app** (see "Later").

## Now (this week, in this order, one at a time)

1. **Security quick fixes (done in 0.18, waiting for the owner to switch them on and look at them).** (a) The two settings that protect the internet-facing server move into the app (Settings, Security): *trusted proxy addresses* (today `JULOS_TRUSTED_PROXIES`) and *allowed target hosts* (today `JULOS_ALLOWED_HOSTS`), stored in the database, environment variable as the fallback; a clear warning on the settings page while the proxy list is empty. (b) The backup key no longer lies next to the dump by default (F-01). (c) A second factor (TOTP) for actions that change hosts or files, asked once for a short window (F-04). See [SECURITY-REVIEW.md](SECURITY-REVIEW.md).
2. **Push messages without AI. Done** ([PUSH.md](PUSH.md)). New security updates, disk almost full (threshold in the app), failed service or container, agent offline or reboot needed. Web Push over the PWA (VAPID), rules in the settings page, quiet hours, one place to see what was sent. See [ASSISTANT.md](ASSISTANT.md) (alerts) and [HOST-AGENT.md](HOST-AGENT.md).
3. **Files app as a real explorer.** Built on the branch `claude/explorer-30d19a`, not released yet: [FILES-EXPLORER.md](FILES-EXPLORER.md) (what is built, the decisions, what was not verified).

## Desktop polish and screens (requested 2026-11-21, owner's words condensed)

- **Explorer repairs (done, waiting for release and the owner's look):** the column headers and the toolbar look broken (they are buttons that do not look like headers); keyboard focus is lost after every folder change; the sidebar lists terminal tiles (`dev-ssh`, Codex, Claude Code) as places, there must be only **Home** and **Root** per host; the window opens small whatever the screen size.
- **Connections app grouped by host (done, same release):** the connections can be told apart per host (a section per host, programs such as Codex and Claude Code under their host, not mixed with plain connections).
- **Dock (pills built on `claude/fenstermanager-f514c3`, not released):** when it folds away it becomes very thin pills of about 5 px height (one per running window, the active one wider, remote connections in another colour), unfolding to named cards at the bottom edge or with the keyboard (`desktop-pills.js`, see [MULTI-SCREEN.md](MULTI-SCREEN.md)); **icons and the customisable surface are built on `claude/icons-oberflaeche-23e387`, not released:** icons come automatically from kind, program and host operating system ([ICONS.md](ICONS.md)), and Settings has an Appearance section (theme, accent colour, background, dock mode and size, icon size and style, reset per area, own icon per connection; [APPEARANCE.md](APPEARANCE.md)). **Still open:** one pill per *program* instead of per window, a dock at the left or right, an own wallpaper picture (needs a small file endpoint).
- **Taskbar of an RDP desktop inside the JulOS dock:** a narrow dock whose framed, labelled strip is the real panel of the VM (a slice of the same picture, nothing changed on the host): [DOCK-REMOTE-TASKBAR.md](DOCK-REMOTE-TASKBAR.md) (requested 2026-11-21; step 1 built on `claude/rdp-taskleiste-1ac34b`, not released: mark the panel by hand, framed strip in the dock, size `height + P`, input mapping; the agent detection (step 2) and JulOS pills for the VM's windows (step 3) are open).
- **Per device memory and several browser windows as one big desktop, seamless, with windows across the border, mouse buttons 4 and 5 and keyboard snapping like Windows:** planned in [MULTI-SCREEN.md](MULTI-SCREEN.md) (decisions of 2026-11-21), four steps, step 1 (window manager in one browser window, workspace saved and restored per device) is built, not released.
- **Dialogs as windows, a real editor and a viewer in the Files app (owner's wishes 2026-11-21):** dialogs and confirmations open as movable, non-blocking windows (phone keeps its sheets), an editor for YAML, JSON and the like (CodeMirror 6, vendored), and viewing of images, video, audio, PDF, Word and Excel through a new narrow preview endpoint. Plan with inventory, library comparison, security model and five releasable steps (windows, remaining dialogs, preview, editor, Word and Excel): [FILES-VIEWER.md](FILES-VIEWER.md); six open questions at its end. Starts after the explorer repairs are released.

## Small things, on the side

- Review the open Dependabot pull requests, merge the safe ones.
- Try the Docker actions on the Docker VM with a throw-away container (start, stop, restart, pull).
- Tidy: archive finished side sessions, remove old branches and worktrees.

## Later (each needs its own plan)

- **JulOS updates itself from the app:** shows the new version, release notes, a button; the server pulls the new image through a small, separate helper that has access to the container runtime (never the server itself), with a health check and an automatic way back. Needs a decision on the helper (a sidecar container versus the existing Compose manager of the owner). **Plan with the variants compared: [SELF-UPDATE.md](SELF-UPDATE.md)** (recommendation: notice and release notes first, S/M; the pin raised by a bot in the stack repo; a real "Update now" only after the owner's decision).
- **Assistant** ([ASSISTANT.md](ASSISTANT.md)): phase 1 read and propose (Codex bridge, API keys, internal MCP tools, notes per host, budget and usage), phase 2 free commands with modes, phase 3 files.
- **Home Assistant in JulOS (state and updates):** a card on the host page through the Home Assistant API with a long-lived token the owner creates in his profile and types into JulOS: version, updates (Core, OS, add-ons), add-on status, read only first, installing updates later with confirmation. Files and the terminal already work through the stored host login (SFTP to `/config`, `/backup`, `/share`; checked live on 2026-11-20); a host agent is not possible on the appliance. Plan: [INTEGRATIONS.md](INTEGRATIONS.md), milestone for Home Assistant.
- **Integrations** ([INTEGRATIONS.md](INTEGRATIONS.md)): Docker through a socket proxy, Proxmox API, Home Assistant API, Synology, Caddy read-only.
- **Update overview of all hosts** and a **maintenance window** with a report (opt-in, never silent).
- **One SSH key per host** instead of stored passwords (JulOS makes the key, the agent installs it).
- **Backup:** encryption with a passphrase, a second target outside the Docker VM.
- **Sign-in:** passkeys (WebAuthn) and a client certificate at the proxy ([SECURITY-REVIEW.md](SECURITY-REVIEW.md), recommendations).
- **Windows agent and Windows management**, reminders.
- **A Home Assistant add-on** instead of an agent (the appliance cannot run an agent).
- **A mascot in the progress panel** of the Claude mod (parked on request).

## Waiting for the owner

- Look at the new Hosts window and the Files app on the real screen and phone and say what still bothers.
- Dev VM sound: the owner runs the `apt install` line from [AUDIO.md](AUDIO.md) and signs in again.
- Dev VM: `claude auth login`.
