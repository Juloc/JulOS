# Sound and microphone

JulOS carries the sound of a remote RDP desktop to the browser and the browser's microphone to the desktop, through its own stream engine
(no extra server, no extra port). This page says what each side needs, how to check it, and where it usually goes wrong. The design is in
[ARCHITECTURE.md](ARCHITECTURE.md) (section "Sound"); the tests are listed at the end.

|                           | Sound from the desktop                         | Microphone to the desktop                                   |
|---------------------------|------------------------------------------------|-------------------------------------------------------------|
| RDP desktop (Windows)     | yes, nothing to install                        | yes, nothing to install                                     |
| RDP desktop (Linux, xrdp) | needs `xrdp-sink` (see below)                  | needs `xrdp-source` (see below); only while a program records |
| VNC desktop               | **no** (the protocol has none); the menu says so | **no**; the menu says so                                  |
| Program sessions (terminal) | no (there is no desktop)                     | no                                                          |

## In the browser

* **Where the controls are.** Phone: the session menu (the dock) has "Sound from computer", "Volume", "Microphone" and "Sound and microphone: diagnosis".
  PC: the menu bar of the desktop view has a menu "Sound" with the same entries (it is shown for RDP and VNC sessions, not for terminals).
  While the microphone is on, a red "Microphone on" pill stays at the top of the page, whichever session is in front; tapping it switches the microphone off.
* **Sound starts after an input.** Browsers keep an `AudioContext` locked until the user taps or presses a key on the page. Connecting a session counts
  (you clicked the tile), so normally sound simply runs. If it does not, the menu says "please tap", a hint appears once ("tap the picture or press a key") and the next
  tap or key press starts it. iOS and Safari need the unlock inside the tap itself; JulOS unlocks on `pointerdown`, `touchend` and `keydown`, and
  creates the microphone's `AudioContext` before the permission question so that it still belongs to the tap.
* **The microphone needs a secure context** (https, or `localhost`). Over plain http the menu says so instead of failing silently.
* **Permission.** The first switch-on makes the browser ask; a hint is shown before it ("choose Allow"). If the answer was no, the message
  explains how to allow it again for the browser in use (Chrome and Edge, Firefox, Safari and iOS, Android Chrome), like the clipboard does.
* **Only the session in front plays**, the microphone belongs to one session at a time (starting it in a second one is refused with the name of the first).
  A browser tab in the background keeps playing (that is what you want for music or a call); a minimised window or another active session is told "off", so the server sends nothing.
  A hidden or minimised pop-out window is silent as well (decision: like a session that is not in front; the main page's background tab keeps playing).
* **Diagnosis** ("Sound and microphone: diagnosis"): is the desktop's sound channel there, is playback running or waiting for input, how many chunks arrived,
  level, buffer, underruns and drops, is the desktop's microphone channel offered, is it recording, level and chunks sent, and the browser's permission state.
  It never shows or stores any sound. With `?debug` in the address, `__pd.cur().client.audio.stats` has the same counters (`received` = samples handed to the
  player, `played` = samples the AudioWorklet wrote to the output, `peak` = level since the last report).

## On the Linux desktop (xrdp)

xrdp only forwards sound that a **per-user audio server inside the session** hands to it, through a module of its own:

| Piece | What | Check |
|-------|------|-------|
| xrdp 0.10 with `xrdp-chansrv` | the channel service of the session (started by `xrdp-sesman`) | `pgrep -a -u $USER xrdp-chansrv`; its log `~/.local/share/xrdp/xrdp-chansrv.<display>.log` shows `sound_process_output_format` lines once a client connected |
| PipeWire as the user's audio server, with `pipewire-module-xrdp` (Debian 13: package `pipewire-module-xrdp`) **or** PulseAudio with `pulseaudio-module-xrdp` (built from the xrdp sources; there is no Debian 13 package) | adds the devices `xrdp-sink` (sound out) and `xrdp-source` (microphone in) | `pactl list short sinks` lists `xrdp-sink`, `pactl list short sources` lists `xrdp-source` |
| the session's `XDG_RUNTIME_DIR` (`/run/user/<uid>`) and the user's audio server running when you log in | the module is loaded per session | `pactl info` answers; `pactl list short modules` shows the xrdp module |
| a program that plays or records | the microphone channel is opened by xrdp **only while something records** | `parecord --raw /dev/null` in a terminal of the desktop, then the menu shows "recording" |

### Setting it up on Debian 13 (steps for the machine's owner; JulOS itself changes nothing there)

Two routes. **Route A (PipeWire)** is the one with a package: Debian 13 (trixie) has `pipewire-module-xrdp` 0.2-2 (it pulls in `libpipewire-0.3-modules-xrdp`). The desktop then uses PipeWire
instead of PulseAudio, which also serves the local desktop (`pipewire-pulse` speaks the PulseAudio protocol, so programs keep working).

1. Install: `sudo apt install pipewire pipewire-pulse wireplumber pipewire-module-xrdp pulseaudio-utils` (apt replaces the `pulseaudio` package by `pipewire-pulse`; read the list of removals before you confirm).
2. Make sure the user services run: `systemctl --user enable --now pipewire pipewire-pulse wireplumber`, and that the old PulseAudio is not started any more (`systemctl --user disable --now pulseaudio.service pulseaudio.socket` if they still exist).
3. Log the xrdp session out completely and connect again (a new session is needed: the module is loaded when the session starts, via `/etc/xdg/autostart/pipewire-xrdp.desktop` of the package). Closing only the JulOS tab keeps the old session.
4. Check with the checklist below (`pactl list short sinks` must list `xrdp-sink`).

**Route B (PulseAudio kept)** has no Debian package for `pulseaudio-module-xrdp`; it is built against the installed PulseAudio sources (follow the project's README of `neutrinolabs/pulseaudio-module-xrdp`; in outline:
enable `deb-src`, `sudo apt install build-essential dpkg-dev git autoconf libtool libltdl-dev libpulse-dev pulseaudio`, `sudo apt build-dep pulseaudio`, clone the repository, run its script that fetches and configures the PulseAudio source
(`scripts/install_pulseaudio_sources_apt_wrapper.sh`, name per release), then `./bootstrap && ./configure PULSE_DIR=<that source folder> && make && sudo make install`), then log the xrdp session out and in. This route was not run here; route A is the tested one (the test desktop uses it).

The test desktop (`deploy/dev/rdp-target`, which has no systemd) starts PipeWire from an autostart script instead (`julos-audio.sh`).

### Checklist for the real VM

Run these as the desktop user, in a terminal **inside the xrdp session** (not over SSH, which has no session audio server):

1. `xrdp --version` shows 0.10 or newer (Debian 13 has 0.10.1) and `pgrep -a -u $USER xrdp-chansrv` shows a process.
2. `tail -30 ~/.local/share/xrdp/xrdp-chansrv.*.log`: after a JulOS (or any RDP) client connected there are `sound_process_output_format` blocks with `WAVE_FORMAT_PCM` (the client side of the sound channel is negotiated).
3. `pactl info` answers (audio server running) and `pactl list short sinks` shows **`xrdp-sink`**; `pactl list short sources` shows **`xrdp-source`**. Only `auto_null` means the module is not installed or not loaded for this session.
4. `pactl list short modules | grep -i xrdp` shows the module; `pactl get-default-sink` is `xrdp-sink` (or play with `paplay --device=xrdp-sink file.wav`).
5. Play something (`speaker-test -t sine -f 440 -l 1` or any video in the desktop): in JulOS open "Sound and microphone: diagnosis": playback "running", chunks received growing, level above 0 %.
6. Microphone: run `parecord --raw --device=xrdp-source /dev/null` in the desktop, switch the microphone on in JulOS: the diagnosis shows "channel offered" and "the desktop is recording", the pill is red.
7. Sound stays silent while one of the checks above fails: the first failing line is the cause.

State of the development VM when this page was written (read-only look, 2026-11-19): xrdp 0.10.1-3.1+deb13u2 and xorgxrdp 0.10.2 installed, `xrdp-chansrv` of session `:10` running and
its log shows the sound formats negotiated (PCM 44100 and 22050 Hz, 2 channels), the user's PulseAudio 17 running, but **only the `auto_null` sink and no xrdp module**
(`pulseaudio-module-xrdp` and `pipewire-module-xrdp` are not installed; PipeWire is not installed at all and the user's `pulseaudio` unit is enabled, so route A replaces it). So the browser side and the server side are in place, and sound and microphone for that desktop need
step 3 above; until then the diagnosis shows the channel as present and "no sound arrived".

## Tests and tools

* `deploy/dev/test-display.sh` (needs the throwaway desktop `rdp-target`): `Sound_played_on_the_desktop_arrives_as_pcm` (a tone played in the session arrives as loud PCM blocks),
  `The_microphone_reaches_a_program_recording_on_the_desktop` (a 600 Hz tone sent as the browser would arrives in a `parecord` recording) and the clipboard test of a real xrdp. They need the
  current image of the target: see "If the display tests are red" in [DEVELOPMENT.md](DEVELOPMENT.md).
* `deploy/dev/e2e.sh` flows 13 to 15 (real Chromium with a fake microphone): a tone in the desktop reaches the browser and the worklet plays it (level, samples), a hidden session gets nothing; the microphone
  switched on in the menu reaches a recording in the desktop; a denied microphone shows the instructions for the browser.
* `deploy/dev/play-tone.sh` and `deploy/dev/record-mic.sh`: play a tone / record in the test desktop by hand (`docker exec -i -u ubuntu <rdp-target> bash -s < deploy/dev/play-tone.sh`).
* `node --test tests/frontend` covers the browser module (`stream-audio.test.mjs`) and the diagnosis (`audiodiag.test.mjs`) with fake audio objects.

## Not verified here

Safari and iOS (the unlock rules above follow the documented behaviour, the code was exercised with Chromium and fake objects only), a real phone, and the real VM (see its state above).
