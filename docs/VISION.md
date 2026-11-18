# JulOS vision

JulOS is not a remote desktop client. It is the **one interface where everything of mine comes together**: my computers,
programs and files, and the services I run at home: Docker, the reverse proxy (Caddy), dynamic DNS, and more. One place, in
your pocket or in any browser, that feels like a single device that you own.

Remote access is the first module, because it is the one that makes everything else reachable from a phone. Docker, Caddy,
DDNS and the rest follow as further modules on the same core.
Everything below is the direction, not a promise of dates. What is actually being built now is defined in [SCOPE.md](SCOPE.md),
which wins whenever the two disagree.

## Principles

1. **Yours.** Self-hosted, open source, no account, no cloud relay. Your data and your sessions stay on your machines.
2. **Mobile first, desktop too.** Designed for a thumb and a phone keyboard, and just as good in a desktop browser.
3. **Apps, not desktops.** You open a program, not a screen of tiny windows. Windows become apps with their own zoom and layout.
4. **Private by default.** mTLS, a proxy secret, short-lived tokens, no open ports on the machines you control.
5. **Small core, open edges.** A tiny gateway and a versioned agent API; everything else is an adapter you can write.

## Pillars

### 1. Every machine
One gateway, many hosts. A host runs a small agent (Linux first, then Windows, later macOS and headless VMs/containers).
Agents connect **outwards** where possible, so nothing listens on the machine. You pick the host like you pick an app.

### 2. Apps as first-class
Window bar instead of a taskbar, program launcher, per-program zoom and layout profiles, narrow windows so responsive apps
switch to their mobile layout. Later: pin a program to your Android home screen as its own icon (PWA shortcuts).

### 3. Files and clipboard that just work
Photos, files, folders, text: drag and drop in the desktop web version, share target and keyboard images on Android,
a file browser for the host, a clipboard that carries images and file lists in both directions.

### 4. Input done right
The Android keyboard as it is (suggestions, autocorrect, images), extra keys you define yourself, gestures, touchpad mode,
and later hardware keyboards and mice, stylus, and a clean fullscreen mode.

### 5. The hub for everything I run
The same app shows and controls the rest of my setup as modules: Docker (containers, stacks, logs), Caddy (sites, certificates),
DDNS (current address, updates), plus things like Proxmox, files and monitoring. Each module talks to the existing tool through
its API and leaves that tool in charge of its domain. Notifications from hosts and quick actions (run a script, restart a
container, open a project) live here too. Modules are adapters, not hard dependencies of the core.

### 6. One look
Material You colours from your phone, installable PWA, fast reconnect, works offline as a shell.

## Milestones

| Step | Content |
| --- | --- |
| 1 (now) | Remote module: Linux desktop on the phone, keyboard, extra keys, program bar, photo paste, PWA, mTLS/proxy mode, Docker image |
| 2 | Core for modules: one shell with module list, login and settings, hosts and modules registered in one place |
| 3 | Docker and Caddy modules (read first, then act), DDNS status |
| 4 | Files: upload/download, drag and drop in a desktop web app, clipboard with files, Android share target |
| 5 | Windows agent (outbound connection), multi-host, notifications, quick actions |
| 6 | Optional extras: Proxmox, monitoring, macOS agent, audio, multi-user for a household |

## Non-goals

- No hosted service and no telemetry.
- Not a replacement operating system kernel or distribution; "OS" means the layer you work in.
- No weakening of the security model for convenience: remote control stays behind mTLS or an equivalent second factor.
