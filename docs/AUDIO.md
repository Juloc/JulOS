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
  A hidden pop-out window is silent as well.
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

Installing on Debian 13 with PipeWire: `sudo apt install pipewire pipewire-pulse wireplumber pipewire-module-xrdp pulseaudio-utils`, make sure the user's
`pipewire`, `wireplumber` and `pipewire-pulse` units are enabled (`systemctl --user enable --now pipewire pipewire-pulse wireplumber`; if the desktop had PulseAudio before,
`pipewire-pulse` replaces it: stop and disable `pulseaudio.service` and `pulseaudio.socket` of the user first), then **log the xrdp session out and in again** (the module is loaded when the session starts).
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
(`pulseaudio-module-xrdp` and `pipewire-module-xrdp` are not installed). So the browser side and the server side are in place, and sound and microphone for that desktop need
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

## Work in progress (paused, to be removed when finished)

Done on branch `claude/audio-check`: the three formerly red display tests are green against a freshly built target (environment cause, see DEVELOPMENT.md); sound end to end (e2e flow 13) and microphone end to end (flow 14) are green in Chromium with fake devices; unit tests (`node --test tests/frontend`) green; real-VM look done (no xrdp audio module installed there, see above).

Not yet done / unverified:
* Flow 15 (denied microphone) was red: Chromium without a permission UI reports `NotSupportedError`. The fix (use the permission state `denied` for unknown error names, `stream-audio.js`) is in the code and unit-tested but flow 15 was **not re-run**.
* `deploy/dev/test.sh` (backend tests) was started and stopped before it finished; run it again: `nice -n 10 deploy/dev/test.sh`.
* The new server test `A_browser_that_takes_over_a_kept_session_is_told_what_the_desktop_offers_for_sound` was not run yet (needs the SDK container): `JULOS_DEV_NETWORK=<net> deploy/dev/test-display.sh --filter-class "*AudioBridgeTests"`.
* Full `deploy/dev/e2e.sh` once after a 60 s pause (all 15 flows), `dotnet build-server shutdown` afterwards.
* Open decision: a hidden/minimised pop-out window is silent, a hidden browser tab is not (see "In the browser").
* The throwaway target and the e2e stack were removed. Rebuild for a re-run: `docker build -t julos-audio-rdp-target -f deploy/dev/rdp-target/Dockerfile .`, run it as described in DEVELOPMENT.md ("If the display tests are red"), and for e2e `E2E_PROJECT=julos-audio-e2e JULOS_E2E_STATE=$HOME/.cache/julos-audio-e2e E2E_KEEP=1 deploy/dev/e2e.sh -g '[^0-9](1|2|13|14|15) [A-Z]'`, then `... e2e.sh down`.
