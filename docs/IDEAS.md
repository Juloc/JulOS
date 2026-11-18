# Ideas parking lot

Everything that is not in [SCOPE.md](SCOPE.md) goes here, one line each, with the date. Not started until 1.0 is tagged and one idea is picked on purpose.

- 2026-11-18 Windows agent (outbound connection, one-click install)
- 2026-11-18 Desktop web version with drag and drop of files into a chat or folder
- 2026-11-18 File browser for the host, clipboard with file lists
- 2026-11-18 Android share target (send files and images into the remote session)
- 2026-11-18 Multi-host picker
- 2026-11-18 Pin a single program to the Android home screen as its own icon
- 2026-11-18 Per-program zoom and layout profiles beyond the Claude default
- 2026-11-18 Adapters and quick actions, notifications from the host
- 2026-11-18 macOS agent, audio, multi-user
- 2026-11-18 Docker module, Caddy module, DDNS status (milestone 3 in the vision)
- 2026-11-18 Module shell/core with module list (milestone 2)
- 2026-11-18 VNC connections, SSH key login, saved connection groups, QR code for the authenticator secret, passkeys (WebAuthn)
- 2026-11-18 Mark which Desktop connection is the machine the agent runs on (today the window bar is offered for every Desktop connection)
- 2026-11-18 Desktop layout (the keyboard part is done: in a desktop browser the hidden input field takes the focus on connect, on every click into the picture and on the first key, so a physical keyboard types without tapping the keyboard button): top bar with the buttons that make sense on a PC (zoom, fullscreen, menu), program bar at the bottom or hidden, no touch-only buttons
- 2026-11-18 Measure and reduce lag over the internet: show round-trip time in the client log, try lower colour depth or no RDP graphics pipeline, compare with the direct LAN path
- 2026-11-18 Picture engine for the Linux desktop (quality, latency, smoothness; research, nothing measured yet). Guacamole only sends PNG/JPEG/WebP tiles, no video; guacd decodes RDP GFX/H.264 and re-encodes it, and RDP has no quality parameter (only `force-lossless`). So it cannot become fluid. Candidates for a CPU-only KVM guest: **Selkies** (docs: attaches to an existing X.Org display, H.264 via WebCodecs over WebSocket, two-way clipboard with images, audio, file drag and drop, MPL-2.0, maintainers wanted), **Xpra** (HTML5 client with H.264/VP8 via WebCodecs, `xpra shadow` shows the whole screen, per-window apps need seamless mode, html5 client wants Xpra 7+ while Debian 13 packages are older), **KasmVNC** (starts its own X session, so no attach to an xrdp session; H.264/H.265/AV1 video mode since 1.5.0, GPL-2.0). No source gives CPU-only numbers, so measure on the real VM first: re-test GFX in guacd now that chansrv works, and prototype Selkies on display :10 from Android Chrome and a PC. Sources: github.com/selkies-project/selkies, docs.selkies.io, github.com/kasmtech/KasmVNC, xpra-org.github.io/xpra, guacamole.apache.org/doc/gug, github.com/neutrinolabs/xrdp/wiki/H.264-encoding. JulOS would then pick an engine per connection (guacd for RDP/SSH/Windows, a stream engine for the Linux desktop) behind the same login.
- 2026-11-18 Measured: Guacamole against Selkies on the Docker test desktop (Debian 13, xrdp 0.10, XFCE, 6 shared CPUs, no GPU, 940x520, Chromium in the app pane, one run each, so a first indication and not a benchmark). Load = a 640x360 30 fps test video (ffplay testsrc2) playing in the session; probe = a small square that toggles on a click, time from click to the colour change in the browser. Visible updates per second / click latency median, p90:
  - Guacamole, GFX off (today): 2.3 fps, 328 ms, 1272 ms (earlier run on a smaller screen 4.5 fps, 277 ms).
  - Guacamole, GFX on (`JULOS_RDP_GFX=on`): 2.7 fps, 619 ms, 2777 ms. GFX changes nothing important; the default stays off.
  - Selkies 2.2.1, WebSocket, software H.264, attached to display :10: 11.1 fps, 154 ms, 288 ms.
  - Idle (no video), click latency median: Guacamole 34 ms (p90 50), Selkies 70 ms (p90 181); the Guacamole number has no browser input path in it, so it is slightly optimistic.
  Not measured yet: text sharpness (H.264 against PNG/JPEG tiles), Android Chrome, the real VM (RAM and CPU while encoding), HTTPS (WebCodecs needs a secure context, so Selkies needs TLS or localhost). Measuring tools: `JULOS_RDP_GFX` switch in `GuacSpec`, `kB insgesamt` in the "Bildaufbau" log line.
- 2026-11-18 Measured on the real VM (Selkies 2.2.1 in a container attached to the live display :10, 1344x2304 portrait, software H.264, loopback only, same probe and 640x360 30 fps test video, one run each): 16.1 visible fps with the video, click latency median 91 ms (p90 240 ms) under load and 78 ms (p90 135 ms) idle. Selkies used about 70-90 % of one core and 155 MB of RAM; the load average of the 6 vCPUs rose to about 5. Text in a 1:1 crop of the stream was crisp. No Guacamole baseline on the real VM yet: connecting would take over the live session and resize it. Still open: Android Chrome (needs HTTPS for WebCodecs), keeping the stream behind the JulOS login, a hardware encoder is not available on this KVM guest.
