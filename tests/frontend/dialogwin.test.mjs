// Dialoge als Fenster (desktop-window.js): die Regel, die zaehlt, ist "Schliessen heisst abgebrochen, nie bestaetigt". Mit einem winzigen Ersatz fuer das DOM und einer
// Fenster-Fabrik, die sich wie desktop.js verhaelt (Schliessen-Taste und Eigentuemer fragen onRequestClose, danach meldet onClose). Optik und Fensterverhalten prueft der Browser.
import test from 'node:test';
import assert from 'node:assert/strict';

function node(tag) {
  const l = new EventTarget();
  const e = {
    tag, className: '', dataset: {}, children: [], disabled: false, hidden: false, textContent: '', type: '', isConnected: true,
    append(...k) { e.children.push(...k); }, focus() { globalThis.document.activeElement = e; }, contains: (x) => x === e || e.children.includes(x),
    querySelector: () => null,
    addEventListener: (...a) => l.addEventListener(...a), dispatchEvent: (ev) => l.dispatchEvent(ev),
    click() { l.dispatchEvent(new Event('click')); },
  };
  return e;
}
globalThis.document = { documentElement: { classList: { contains: (c) => c === 'desktop' } }, createElement: node, body: node('body'), activeElement: null };
globalThis.queueMicrotask ||= (f) => Promise.resolve().then(f);

const { openDialogWindow, setDialogHost, showDialog } = await import('../../src/JulOS.Frontend/wwwroot/desktop-window.js');

/** Wie dialogFrame in desktop.js: ein Fenster mit Rahmen; close() ruft onClose, der Schliessen-Knopf fragt erst */
let last = null;
setDialogHost((o) => {
  const el = node('win'), body = node('body');
  last = { el, body, opts: o, closed: false, xButton() { if (!o.onRequestClose || o.onRequestClose()) last.close(); }, close() { if (last.closed) return; last.closed = true; o.onClose(); }, setTitle() {}, place() {}, focus() {} };
  return last;
});
const open = (o = {}) => openDialogWindow({ title: 't', actions: [{ id: 'no', label: 'Nein' }, { id: 'yes', label: 'Ja', kind: 'primary' }], ...o });
const press = (win, key) => { const ev = new Event('keydown', { cancelable: true }); ev.key = key; win.el.dispatchEvent(ev); };
const button = (d, id) => d.el.children.find((c) => c.className.includes('dlg-btns')).children.find((b) => b.dataset.act === id);

test('eine Taste liefert ihre id, Schliessen und Escape liefern null (nie "yes")', async () => {
  let d = open(); button(d, 'yes').click();
  assert.equal(await d.promise, 'yes');
  d = open(); last.xButton();
  assert.equal(await d.promise, null);
  d = open(); press(last, 'Escape');
  assert.equal(await d.promise, null);
  assert.ok(last.closed);
});

test('Schliessen des Eigentuemers (Fenster zu, Abmelden) bricht ab: close() ohne Ergebnis', async () => {
  const d = open();
  last.close();
  assert.equal(await d.promise, null);
});

test('close(result) und spaeteres Schliessen: das erste Ergebnis gilt, einmal', async () => {
  const d = open();
  d.close('yes'); last.close(); d.close(null);
  assert.equal(await d.promise, 'yes');
});

test('ein Dialog darf das Schliessen ablehnen (cancel verhindern): Fenster und Versprechen bleiben offen', async () => {
  const d = open();
  d.el.addEventListener('cancel', (e) => e.preventDefault());
  last.xButton(); press(last, 'Escape');
  assert.equal(last.closed, false);
  let settled = false; d.promise.then(() => { settled = true; });
  await new Promise((r) => setTimeout(r, 5));
  assert.equal(settled, false);
  button(d, 'no').click();
  assert.equal(await d.promise, 'no');
});

test('mit key oeffnet ein Dialog nur einmal; nach dem Schliessen wieder', async () => {
  const a = open({ key: 'edit:1' }), b = open({ key: 'edit:1' });
  assert.equal(a, b);
  a.close();
  await a.promise;
  const c = open({ key: 'edit:1' });
  assert.notEqual(a, c);
  c.close();
});

test('ohne Desktop (kein Fenster-Host oder nicht html.desktop) gibt es kein Fenster', () => {
  const dt = globalThis.document.documentElement;
  globalThis.document.documentElement = { classList: { contains: () => false } };
  assert.equal(open(), null);
  globalThis.document.documentElement = dt;
});

// showDialog: die festen <dialog>-Elemente (Step-up, Host-Aktion, Konto ...) im Fenster
globalThis.document.createComment = () => ({ parentNode: null, replaceWith() {}, remove() {} });
function dialog() {
  const l = new EventTarget();
  const d = {
    open: false, closes: 0, parentNode: null, classList: { add() {}, remove() {} }, querySelector: () => null, contains: () => false, before() {}, remove() {}, focus() {},
    setAttribute() { d.open = true; },
    close() { if (!d.open) return; d.open = false; d.closes++; l.dispatchEvent(new Event('close')); },
    addEventListener: (...a) => l.addEventListener(...a), removeEventListener: (...a) => l.removeEventListener(...a), fire: (t) => l.dispatchEvent(new Event(t)),
  };
  return d;
}
const tick = () => new Promise((r) => setTimeout(r, 5));

test('showDialog: Schliessen des Fensters (Taste, Escape, Eigentuemer, Abmelden) schliesst das <dialog> = abgebrochen, nie bestaetigt', async () => {
  for (const how of ['x', 'escape', 'owner']) {
    const d = dialog(); const h = showDialog(d, { title: 'Freigabe' });
    assert.ok(d.open);
    if (how === 'x') last.xButton(); else if (how === 'escape') press(last, 'Escape'); else last.close();
    assert.equal(await h.promise, null);
    await tick();
    assert.equal(d.open, false); assert.equal(d.closes, 1);        // die App sieht ihr close-Ereignis (Step-up: nicht freigegeben; Host-Aktion: nichts gesendet)
  }
});

test('showDialog: dlg.close() der App schliesst das Fenster; ein zweiter Aufruf holt das offene Fenster; ein altes close-Ereignis schliesst ein neu geoeffnetes nicht', async () => {
  const d = dialog(); const h = showDialog(d);
  assert.equal(showDialog(d), h);
  d.close(); await tick();
  assert.equal(last.closed, true);
  const h2 = showDialog(d); assert.notEqual(h2, h);
  d.fire('close');                                                // spaet eingetroffenes Ereignis des ersten Schliessens, der Dialog ist wieder offen
  assert.equal(last.closed, false);
  d.close(); await tick();
  assert.equal(last.closed, true);
});
