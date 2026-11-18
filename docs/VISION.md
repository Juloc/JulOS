# JulOS vision

JulOS is not a remote desktop client. It is a **personal, self-hosted workspace layer**: one place, in your pocket or in any
browser, where all your computers, programs and files feel like a single device that you own.

The first release is the smallest useful piece: your own Linux desktop on your phone, with the keyboard you already use.
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

### 5. Your workspace, not just your screen
Things you do all day, close at hand: notifications from the host, quick actions (run a script, open a project),
status tiles, and links to the tools you already run (for example coding agents, home automation, dashboards) as adapters,
not as hard dependencies.

### 6. One look
Material You colours from your phone, installable PWA, fast reconnect, works offline as a shell.

## Milestones

| Step | Content |
| --- | --- |
| 0.x (now) | Linux desktop on the phone: keyboard, extra keys, window bar, photo paste, PWA, mTLS/proxy mode, Docker image |
| 1 | Reliable daily use: HTTPS rollout, polish from real phone testing, per-program profiles, settings sync |
| 2 | Files: upload/download, drag and drop in a desktop web app, clipboard with files, Android share target |
| 3 | Windows agent (outbound connection, one-click install), host picker, multi-host |
| 4 | Adapters and quick actions, notifications from hosts, pinned apps as home screen icons |
| 5 | Optional extras: macOS agent, audio, multi-user for a household |

## Non-goals

- No hosted service and no telemetry.
- Not a replacement operating system kernel or distribution; "OS" means the layer you work in.
- No weakening of the security model for convenience: remote control stays behind mTLS or an equivalent second factor.
