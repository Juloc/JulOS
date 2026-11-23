# Touch: operating a remote session with fingers

Logic: `touch-gestures.js` (pure state machine, tested), feedback `touch-ui.js` (ring, drag marker, vibration), settings sheet `touch-settings.js`,
clipboard sheet `clip-panel.js`, hold keys `keys-sticky.js`, shortcuts and "type as text" `keys-shortcuts.js`, keyboard text sync `kb-input.js`.

## Gestures (direct and touchpad mode)

| Gesture | Effect |
| --- | --- |
| Tap | left click (direct: at the finger, pad: at the pointer) |
| Hold (ring fills, 300 to 1000 ms, default 500) | right click, short vibration; moving more than 12 px cancels |
| Two-finger tap | right click (method setting: hold, two fingers, both) |
| Two fingers slide | scroll vertically and horizontally (natural or classic, slow/normal/fast); zoom only after a clear change of distance |
| Double tap and hold | drag: left button stays down until the finger lifts (marker shown); touchpad: tap and drag |
| One finger moves | direct: drag as before (can be switched off); pad: moves the pointer (moderate acceleration, speed setting) |
| Pinch | zoom (changes the VM resolution, so the picture always fills the screen; no panning needed) |
| Two-finger double tap | reset zoom (only when zoomed; the first right click then waits 380 ms) |

Settings: Profile, "Sitzung und Touch" and the session menu ("Touch-Einstellungen", with a test area). Stored per device and as server setting `touch`.

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

## Not verified
Only synthetic touch events in a desktop browser pane (375x812) were used. Real finger feel (hold time), vibration, the real Android keyboard and its dictation, iOS: untested.
