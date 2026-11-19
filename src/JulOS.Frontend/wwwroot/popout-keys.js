/* =====================================================================
 * Tasten des Pop-out-Fensters (popout.js): die physische Taste (KeyboardEvent.code) wird zum RDP-Scancode (Satz 1), unabhaengig davon, welches
 * Zeichen sie am Browser erzeugt. Das Ziel entscheidet mit seiner Belegung (JulOS verbindet mit der deutschen, siehe RdpOptions.KeyboardLayout),
 * was die Taste schreibt. Zeichen, die keine Taste haben (Emoji, Eingabemethoden), gehen so nicht: dafuer gibt es das Hauptfenster.
 * Getestet in tests/frontend/popout.test.mjs.
 * ===================================================================== */

// [Scancode, erweitert]
const T = new Map();
const add = (code, scan, ext = 0) => T.set(code, [scan, ext]);
'QWERTYUIOP'.split('').forEach((c, i) => add('Key' + c, 0x10 + i));
'ASDFGHJKL'.split('').forEach((c, i) => add('Key' + c, 0x1e + i));
'ZXCVBNM'.split('').forEach((c, i) => add('Key' + c, 0x2c + i));
for (let i = 1; i <= 9; i++) add('Digit' + i, 0x01 + i);
add('Digit0', 0x0b);
for (let i = 1; i <= 10; i++) add('F' + i, 0x3a + i);
add('F11', 0x57); add('F12', 0x58);
for (const [code, scan] of [['Escape', 0x01], ['Minus', 0x0c], ['Equal', 0x0d], ['Backspace', 0x0e], ['Tab', 0x0f], ['BracketLeft', 0x1a], ['BracketRight', 0x1b],
  ['Enter', 0x1c], ['ControlLeft', 0x1d], ['Semicolon', 0x27], ['Quote', 0x28], ['Backquote', 0x29], ['ShiftLeft', 0x2a], ['Backslash', 0x2b],
  ['Comma', 0x33], ['Period', 0x34], ['Slash', 0x35], ['ShiftRight', 0x36], ['NumpadMultiply', 0x37], ['AltLeft', 0x38], ['Space', 0x39], ['CapsLock', 0x3a],
  ['NumLock', 0x45], ['ScrollLock', 0x46], ['Numpad7', 0x47], ['Numpad8', 0x48], ['Numpad9', 0x49], ['NumpadSubtract', 0x4a], ['Numpad4', 0x4b], ['Numpad5', 0x4c],
  ['Numpad6', 0x4d], ['NumpadAdd', 0x4e], ['Numpad1', 0x4f], ['Numpad2', 0x50], ['Numpad3', 0x51], ['Numpad0', 0x52], ['NumpadDecimal', 0x53], ['IntlBackslash', 0x56]]) add(code, scan);
for (const [code, scan] of [['NumpadEnter', 0x1c], ['ControlRight', 0x1d], ['NumpadDivide', 0x35], ['PrintScreen', 0x37], ['AltRight', 0x38], ['Home', 0x47],
  ['ArrowUp', 0x48], ['PageUp', 0x49], ['ArrowLeft', 0x4b], ['ArrowRight', 0x4d], ['End', 0x4f], ['ArrowDown', 0x50], ['PageDown', 0x51], ['Insert', 0x52],
  ['Delete', 0x53], ['MetaLeft', 0x5b], ['MetaRight', 0x5c], ['ContextMenu', 0x5d]]) add(code, scan, 1);

/** [Scancode, erweitert] der Taste, oder null (unbekannte Taste: nicht senden). */
export function scancodeOf(code) { return T.get(code) || null; }

/** Strg+V oder Umschalt+Einfg: das Einfuegen laeuft ueber die Zwischenablage (der Text muss erst zur VM). */
export function isPasteShortcut(e) {
  const key = String(e.key || '').toLowerCase();
  return (!!e.ctrlKey && !e.altKey && !e.metaKey && key === 'v') || (!!e.shiftKey && !e.ctrlKey && !e.altKey && e.key === 'Insert');
}
