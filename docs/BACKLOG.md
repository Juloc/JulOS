# Backlog and order of work

Kept short on purpose. The owner decides what moves up. New ideas go to [IDEAS.md](IDEAS.md); this page is the order. Last agreed: 2026-11-22 (finish and walk through what is built before anything new; targeted tests, short reports).

## Principle (owner's wish, 2026-11-20)

**Settings live in the app, not in Compose files or environment variables**, wherever that is possible. An environment variable stays as a default or an override for the person who runs the server, but every setting that matters to the owner has a place in the settings page, is stored in the database and takes effect without a restart. The same goes for **updating JulOS itself from the app** (see "Later").

## Now: finish and go through it with the owner (agreed 2026-11-22)

No new big piece before the owner has looked at everything and said what still bothers. Order: (1) merge and release what is built, (2) a walkthrough of the live system in nine short parts (updates and host actions, explorer with the address bar, dialogs as windows, editor, preview, window manager, dock pills and remembered windows, taskbar of an RDP desktop, appearance and icons), (3) fix what the owner names, (4) only then the next piece (the owner's pick: the self-update, see [SELF-UPDATE.md](SELF-UPDATE.md)).

## Done and live (0.18 to 0.23, 2026-11-20 to 2026-11-22)

- **Security** (0.18, 0.20): trusted proxies and allowed hosts in the app, backup key apart from the dump, second factor (TOTP), confirmations in three levels ([SECURITY-REVIEW.md](SECURITY-REVIEW.md)).
- **Push messages without AI** ([PUSH.md](PUSH.md)).
- **Files** (0.21, 0.22): explorer with an address bar like the Windows Explorer, one *Home* and *Root* per host, connections grouped by host, "write files" as a switch in the app, reasons for files that cannot be read, preview of images, video, audio, PDF, Word, Excel and csv, an editor with CodeMirror 6 ([FILES-EXPLORER.md](FILES-EXPLORER.md), [FILES-VIEWER.md](FILES-VIEWER.md)).
- **Dialogs and confirmations as movable windows** (0.22, phone keeps its sheets).
- **Window manager** (0.23): snapping with mouse and keyboard (`Ctrl+Alt`), mouse buttons 4 and 5, thin dock pills, windows remembered per device, the taskbar of an RDP desktop as a framed strip in the dock (step 1, marked by hand) ([MULTI-SCREEN.md](MULTI-SCREEN.md), [DOCK-REMOTE-TASKBAR.md](DOCK-REMOTE-TASKBAR.md)).
- **CI:** tag image build about 70 s, tests in parallel jobs, doc-only changes start nothing.

## Built, not released yet

- **Icons from the programs and an Appearance section** (theme, accent, background, dock mode and size, icon size and style, own icon per connection): [ICONS.md](ICONS.md), [APPEARANCE.md](APPEARANCE.md).

## Half built (decide after the walkthrough)

- **Taskbar of an RDP desktop:** step 2 (agent detection of the panel) and step 3 (JulOS pills for the VM's windows) are open; not verified: a Windows target, a panel at the left or right, HiDPI, real full screen, two VMs.
- **Several browser windows as one desktop** (MULTI-SCREEN steps 2 to 4): windows across the border, several viewers per connection, continue on the phone. Planned, not started.
- **Dock:** one pill per program instead of per window, a dock at the left or right, an own wallpaper picture.
- **Editor:** a read-only look at the end of files over 1 MiB (dropped for now); **preview:** old `.doc` and `.xls` do not open (download offered).

## Small things, on the side

- Review the open Dependabot pull requests (two GitHub Actions bumps, green), merge the safe ones; close the old pull request from August.
- One server test is sometimes red (`Jobs_belong_to_their_user_and_a_user_runs_at_most_two_at_once`): repair it, it guards user isolation.
- The branch `claude/integr-m0` keeps the parked integration layer (M0/M1) of 2026-11-20; not merged.
- Try the Docker actions on the Docker VM with a throw-away container (done for restart, stop, start, pull on 2026-11-20).

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

- The walkthrough above, on the real screen and on the phone (everything was only tested by the assistant in a browser pane).
- Restart the Docker VM and the Proxmox host so that the new kernels apply (the owner decides when).
- Settings, Security: enter the trusted proxy address and keep the recovery key; switch push on at the phone.
- Dev VM sound: the owner runs the `apt install` line from [AUDIO.md](AUDIO.md) and signs in again; Dev VM: `claude auth login`.
