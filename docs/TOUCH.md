# Touch: operating a remote session with fingers

Logic: `touch-gestures.js` (pure state machine and zoom maths, tested in `tests/frontend/touch-gestures.test.mjs` and `touch-tablet.test.mjs`), feedback `touch-ui.js` (ring, drag marker, vibration), settings sheet `touch-settings.js`,
clipboard sheet `clip-panel.js`, hold keys `keys-sticky.js`, shortcuts and "type as text" `keys-shortcuts.js`, keyboard text sync `kb-input.js`.

## Tablet mode (default on phones)

One finger scrolls like on a tablet. JulOS turns the fingers into pointer, buttons and wheel events, because the remote side only knows a mouse (see "Why Linux sees no real fingers" below).

| Gesture | What reaches the target |
| --- | --- |
| One finger swipes (vertical or horizontal) | pointer moved to the finger without a click, then wheel notches; the content follows the finger (setting: natural or classic), faster swipes scroll further, after lifting it coasts on and slows down (not with `prefers-reduced-motion`) |
| Touch a coasting list | stops it at once; this touch is not a click |
| Tap | left click at the finger |
| Tap twice (within 330 ms, 36 px) | double click: the second click goes to the spot of the first, so the target sees two clicks at the same place |
| Hold still, ring half full (about 320 ms, short buzz, ring changes colour): "ready to drag" | moving now presses the left button at the start spot and holds it until you lift (marker shown): select text, move windows, sliders, scroll bars; lifting without moving is a left click |
| Hold still, ring full (500 ms by default, longer buzz) | lifting without moving is a right click at the finger; moving still drags |
| Two fingers spread or pinch | optical zoom of the picture in the browser, up to 4 times, around the fingers; the VM resolution stays |
| Two fingers slide | zoomed: moves the magnified picture; not zoomed: scrolls |
| Two-finger tap | right click |
| Two-finger double tap | zoom back (when magnified; the right click of the first tap then waits 380 ms) |

Rules that keep the gestures apart: a swipe starts only after 12 px (below that it is a tap). A swipe that begins before "ready to drag" scrolls; once the ring is half full a move drags instead, so
hold first, then move. The scroll axis is locked to the dominant direction (slight drift does not scroll sideways) until you clearly move across (over 48 px). With the on-screen keyboard open the
existing shifting of the picture still applies first. A second finger or `touchcancel` ends everything: a held button is released, ring, marker and coasting are removed.

Settings (Profile, "Sitzung und Touch", and the session menu with a test area): mode (Tablet, Maus, Touchpad), pinch (magnify the picture, or change the resolution), right-click method (hold, two fingers, both),
hold time 300 to 1000 ms (the "ready to drag" point is half way between ring start and full), scroll direction and speed, vibration. The quick switch in the session menu cycles Tablet, Maus, Touchpad;
"Anzeigegröße" there changes the VM resolution (60 to 160 %), which used to be the pinch. Stored per device and as server setting `touch`; the mode is part of it.
Everything the machine decides is written to the client log as `gest` (kind, fingers, ms, path in px, wheel steps, zoom; no content; at most 60 per session), so the server log shows what the phone really did.

**Default.** Tablet is the default mode. A device that never chose anything, and one that only had the old default "direct", get Tablet once (the old default could not be told apart from a choice);
"pad" stays. After that a deliberate "Maus" is kept. A one-time toast on a phone announces it. On a PC with a mouse the setting has no effect (the mouse is used directly).

## How finely can it scroll? (measured)

Against the test target (xrdp 0.10.1, xorgxrdp 0.10.2, XFCE terminal with mouse reporting on, 4 wheel messages of 30, 4 of 120, one of 120, one of 600 units):
**xrdp ignores the size of a wheel delta and turns every wheel message into exactly one notch** (12 messages gave 12 wheel clicks; 30 units scroll as much as 120; 600 is split by the server into three messages of at most 255 and gives
three clicks, not five). Small deltas are neither accumulated nor dropped; they simply count as a full notch. So the finest scroll step is one notch (a bit over two lines in the terminal, about 40 px of the VM),
and two notches in one message (240) would be one notch. The client therefore sends **one message of 120 per notch** (up to 8 per call); the old code sent `-120 * n` in one message, which lost notches for n of 2 or more
(also for two-finger scrolling). VNC does it properly (a rest is kept, one click per 120 units), so the same messages work there.
Mapping finger to notch: one notch per `scrollStep` pixels of finger travel, which is 40 VM pixels (slow 56, fast 28) times the display scale and the optical zoom, limited to 14 to 90 px, so the content moves about
as far as the finger; a swipe is amplified up to 1.6 times when fast (finger speed up to 2 px/ms). Coasting uses an exponential decay (time constant 350 ms, stops below 0.04 px/ms, at most 3 s).
Because every step is a whole notch the movement is stepped, not smooth; what a notch moves depends on the program (terminal two to three lines, browsers and Thunar more). This is the limit of xrdp, not of the client.

## Mouse and touchpad modes

Mode "Maus" (internally `direct`; the old default) and "Touchpad" (`pad`) work as before:

| Gesture | Effect |
| --- | --- |
| Tap | left click (Maus: at the finger, Touchpad: at the pointer) |
| Hold (ring fills, 300 to 1000 ms, default 500) | right click, short vibration; moving more than 12 px cancels |
| Two-finger tap | right click (method setting: hold, two fingers, both) |
| Two fingers slide | scroll vertically and horizontally (natural or classic, slow/normal/fast); zoom only after a clear change of distance |
| Double tap and hold | drag: left button stays down until the finger lifts (marker shown); Touchpad: tap and drag |
| One finger moves | Maus: drag (can be switched off); Touchpad: moves the pointer (moderate acceleration, speed setting) |
| Pinch | zoom that changes the VM resolution (sharp text, the picture fills the screen; no panning needed) |
| Two-finger double tap | reset zoom (only when zoomed; the first right click then waits 380 ms) |

In Tablet mode the setting "Pinch" can also be "change the resolution" (the old behaviour).

## Why Linux sees no real fingers

RDP has real multi-touch only through the separate touch channel (RDPEI, `Microsoft::Windows::RDS::Input`): the client sends touch contacts, and a Windows server turns them into touch input, so apps see swipes, inertia
and gestures natively. xrdp does not offer that channel (only mouse, keyboard, wheel), and X11 sessions under XFCE would not know what to do with it anyway. So under Linux the client has to turn fingers into
pointer, buttons and wheel events itself, and the "tablet mode" is exactly that translation. There is no mode to switch on in Linux for this. For Windows targets the touch channel could later carry real contacts
(then pinch, inertia and edge swipes would be done by Windows; the picture zoom would stay a client matter); the engine does not use it yet.

## Clipboard sheet
A small chip in the session shows what is on this device's clipboard ("Text · 42 Zeichen", "Bild · PNG 1280x720"). It reads without a tap only when the browser
permission is `granted` (Chrome, Edge) and the page has focus; otherwise only after a tap (hint first). Tapping opens the sheet: Paste, Type as text (up to 4000 characters,
progress and cancel), Edit first, Send as file, images as PNG or JPG file; below, the last text or image from the session with "Copy to this device".
No content goes into logs.

## Hold keys and shortcuts
Ctrl, Alt, Shift, Win, AltGr: one tap = next key or click (outline), two quick taps = locked (filled), another tap releases. All are released when leaving the session,
when the page goes to the background, on a lost connection and after reconnecting. Shortcuts are a new key type (a sequence like `ctrl+alt+end, alt+tab`),
with templates for Windows and Linux, an optional icon, optionally bound to one connection. Text snippets are not for passwords (hint in the editor, warning on save).

## Dictation
Voice typing of the phone keyboard arrives through `input`/composition events and is compared with the previous state (`kb-input.js`); tested with simulated sequences only.
Modes "with images" and "with suggestions" allow voice typing; the password-type mode ("without suggestions") normally does not. A browser speech-recognition button was left out:
Chrome sends the audio to Google, it conflicts with the app's own microphone use and cannot be verified without a device.

## Checked
Against a server built from this branch: the `touch` setting is stored, normalised (unknown fields dropped, hold time rounded to 50 ms) and rejected for non-objects; a shortcut key with icon and
connection is accepted. In the browser pane (375x812, light and dark): the settings sheet with its test area, a saved shortcut (Ctrl+Alt+T opened a terminal), a text snippet (typed into it)
and a multi-step shortcut (`ctrl+c, e, x, i, t, enter` closed it). A shortcut bound to one connection did not show for another connection.

Tablet mode (0.27): in the browser pane at 390x844, against the test desktop (xrdp, XFCE: a terminal with 300 lines and Thunar), with real `TouchEvent` sequences made of `Touch` objects on the touch layer
(every event was `defaultPrevented`, `touch-action` is `none`, `user-select` none): swipe scrolled the terminal (steps and coasting in the log), a double tap opened a folder, hold with a full ring opened the context menu,
hold half way and move dragged a window and marked text, a pinch magnified the picture to 306 % around the fingers, a double tap in the magnified picture hit the right folder, two fingers moved the picture, a two-finger double tap
zoomed back, and `touchcancel` in a drag removed the marker and released the button. The test area in the settings shows each state.

## Not verified
Real fingers: the feel of the hold times and the "ready to drag" buzz, swipe gain and coasting length, how browser gestures (back swipe, pull to refresh, the text menu) react to a real finger on Android Chrome (the events are
prevented and `touch-action` is `none`, but only a phone shows it), vibration, the real Android keyboard and its dictation, iOS. VNC sessions use the same code but were not run in this round.
