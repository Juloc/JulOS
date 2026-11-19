/* =====================================================================
 * Eingabeleiste fuer das Terminal (docs/CLI-SESSIONS.md): ein Mehrzeilen-Textfeld mit Senden-Knopf (Diktieren und lange Eingaben am Handy, ohne direkt ins
 * Terminal zu tippen), darueber die Schnelltasten (Esc, Tab, Umschalt+Tab, Strg+C, Strg+D, Pfeile, Bild auf/ab ... je nach Programm), dazu Kopieren, Einfuegen
 * und Schriftgroesse. Die Leiste kennt app.js nicht: sie bekommt die TerminalView und sendet ueber deren Funktionen (sendText, paste, copy, zoom).
 * Der reine Teil (Tastenentscheidung, Sendeplan, Hoehe des Felds, Tastenbytes) ist ohne DOM getestet (tests/frontend/terminal-input.test.mjs).
 * ===================================================================== */
import { t, tk } from './i18n.js';
import { keysymBytes } from './terminal.js';
import { quickKeys, primaryKeys, programOf, isAssistant } from './terminal-programs.js';

export const MAX_ROWS = 6;                 // so viele Zeilen waechst das Feld, danach scrollt es
export const ENTER_DELAY_MS = 120;         // zwischen eingefuegtem Text und Enter: manche Programme erkennen ein Einfuegen erst am Ende und nehmen ein sofortiges Enter als Teil davon

/**
 * Was bedeutet eine Taste im Textfeld? 'send' (Enter), sonst null (das Feld macht, was es immer tut: Umschalt+Enter ist eine neue Zeile).
 * Waehrend einer Eingabemethode (Diktat, Kompositionstext, isComposing) sendet Enter nicht: es bestaetigt dort die Zeichen.
 */
export function inputKeyAction(e) {
  if (e.isComposing || e.keyCode === 229) return null;
  if (e.key !== 'Enter') return null;
  if (e.shiftKey || e.altKey) return null;
  return 'send';
}

/**
 * Der Sendeplan fuer den Inhalt des Felds: text = was als Einfuegen geht (null: nichts), enter = danach Enter.
 * Zeilenenden am Ende fallen weg (Enter kommt ohnehin); ein leeres Feld sendet nur Enter (zum Bestaetigen einer Rueckfrage).
 */
export function sendPlan(raw) {
  const text = String(raw ?? '').replace(/\r\n?/g, '\n').replace(/\s+$/u, '');
  return { text: text === '' ? null : text, enter: true };
}

/**
 * Text in den Entwurf der Leiste legen, ohne ihn zu senden: die Leiste wird geoeffnet, der Entwurf ersetzt. Der Zustand gehoert der Sitzung, darum
 * geht das auch, bevor die Leiste existiert (sie entsteht erst beim Verbinden und liest den Entwurf dann). Gibt den Zustand zurueck.
 */
export function prefillState(state, text) {
  state.draft = String(text ?? '');
  state.open = true;
  return state;
}

/** Zeilen des Felds (1 bis max): nach Zeilenumbruechen, die Umbrueche der Breite faengt das Feld selbst durch seine Hoehe auf. */
export const rowsFor = (text, max = MAX_ROWS) => Math.min(max, Math.max(1, String(text ?? '').split('\n').length));

/**
 * Die letzte Adresse (https) im Terminal, zum Beispiel die Anmeldeseite, die Claude oder Codex ausgeben. lines: die Zeilen von oben nach unten als { text, wrapped };
 * eine umbrochene Zeile gehoert zur vorigen (lange Adressen laufen ueber mehrere Zeilen). Gibt null, wenn es keine gibt.
 */
export function findLastUrl(lines) {
  const paras = [];
  for (const l of lines) { if (l.wrapped && paras.length) paras[paras.length - 1] += l.text; else paras.push(l.text); }
  for (let i = paras.length - 1; i >= 0; i--) {
    const m = paras[i].trimEnd().match(/https:\/\/[^\s"'<>`)\]]+/g);
    if (m) return m[m.length - 1].replace(/[.,;:]+$/, '');
  }
  return null;
}

/** Bytes (als Text) einer Schnelltaste; Pfeile im Anwendungsmodus bekommen ihr ESC O. */
export function keyBytes(spec, appCursor = false) {
  if (spec.text !== undefined) return spec.text;
  return keysymBytes(spec.sym, spec.mods || {}, appCursor);
}

const el = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text) n.textContent = text; return n; };
const ICON = {
  send: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 20.5v-6.9l9-2.1-9-2.1V3.5L22 12z" fill="currentColor"/></svg>',
  newline: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 7v4H5.83l3.58-3.59L8 6l-6 6 6 6 1.41-1.41L5.83 13H21V7z" fill="currentColor"/></svg>',
  copy: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z" fill="currentColor"/></svg>',
  paste: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 2h-4.18C14.4.84 13.3 0 12 0S9.6.84 9.18 2H5c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm7 18H5V4h2v3h10V4h2v16z" fill="currentColor"/></svg>',
  down: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7.41 8.59 12 13.17l4.59-4.58L18 10l-6 6-6-6z" fill="currentColor"/></svg>',
  up: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7.41 15.41 12 10.83l4.59 4.58L18 14l-6-6-6 6z" fill="currentColor"/></svg>',
};

/**
 * Die Leiste am unteren Rand der Terminal-Buehne. opts: { view: TerminalView, container: Buehne, program, state: { draft, open } }.
 * state gehoert der Sitzung (ueberlebt ein Neuverbinden: der Entwurf geht nicht verloren). Die Hoehe der Leiste steht als --term-bar-h an der Buehne,
 * das Terminal ueber der Leiste folgt ihr (terminal.css).
 */
export class TerminalBar {
  constructor(opts) {
    this.view = opts.view;
    this.container = opts.container;
    this.program = programOf({ kind: 'terminal', program: opts.program });
    this.state = opts.state;
    if (typeof this.state.open !== 'boolean') this.state.open = this.program !== 'shell';      // bei Programmen offen, bei der blanken Shell eingeklappt (wie bisher)
    this.destroyed = false;
    this.build();
    this.apply();
  }

  build() {
    this.root = el('div', 'term-bar');
    this.root.setAttribute('role', 'region');
    this.root.setAttribute('aria-label', t('term.barLabel'));

    // Zeile 1: wenige Schnelltasten mit kurzer Beschriftung, dazu "Mehr" (alle Tasten, Kopieren, Schrift, Hilfe)
    this.keys = el('div', 'term-keys');
    this.keys.setAttribute('role', 'toolbar');
    this.keys.setAttribute('aria-label', t('term.quickKeys'));
    this.renderKeys();
    this.moreBtn = this.button('term-more-btn', `<span>${t('term.more')}</span>${ICON.up}`, t('term.moreKeys'));
    this.moreBtn.setAttribute('aria-haspopup', 'dialog');

    // Zeile 2: Eingabe, Einfuegen (mit Beschriftung), neue Zeile, Senden
    const row = el('div', 'term-compose');
    this.field = el('textarea', 'term-input');
    this.field.rows = 1;
    this.field.placeholder = t('term.inputPlaceholder');
    this.field.setAttribute('aria-label', t('term.inputLabel'));
    this.field.setAttribute('enterkeyhint', 'send');
    this.field.setAttribute('autocapitalize', 'sentences');
    this.field.setAttribute('spellcheck', 'true');
    this.field.value = this.state.draft || '';
    this.pasteBtn = this.button('term-paste', `${ICON.paste}<span>${t('term.paste')}</span>`, t('term.pasteToInput'));
    this.newline = this.button('term-nl', ICON.newline, t('term.newline'));
    this.send = this.button('term-send', `${ICON.send}<span>${t('term.send')}</span>`, t('term.send'));
    this.send.classList.add('primary');
    row.append(this.field, this.pasteBtn, this.newline, this.send);

    this.top = el('div', 'term-top');
    this.top.append(this.keys, this.moreBtn);
    this.root.append(this.top, row);

    // Eingeklappt: nur ein kleiner Griff
    this.handle = this.button('term-handle', ICON.up, t('term.barShow'));

    this.container.append(this.root, this.handle);
    this.buildTip();

    this.field.addEventListener('keydown', (e) => {
      if (inputKeyAction(e) === 'send') { e.preventDefault(); this.submit(); }
    });
    this.field.addEventListener('input', () => { this.state.draft = this.field.value; this.grow(); });
    this.newline.addEventListener('click', () => this.insert('\n'));
    this.send.addEventListener('click', () => this.submit());
    this.pasteBtn.addEventListener('click', () => this.pasteFromClipboard());
    this.moreBtn.addEventListener('click', () => (this.more ? this.closeMore() : this.openMore()));
    this.handle.addEventListener('click', () => { this.setOpen(true); this.field.focus(); });

    // Die Hoehe der Leiste (Tastatur, Zeilen im Feld) bestimmt die Hoehe des Terminals darueber
    if (typeof ResizeObserver === 'function') {
      this.ro = new ResizeObserver(() => this.measure());
      this.ro.observe(this.root);
    }
  }

  /** Knoepfe nehmen dem Feld nie den Fokus (die Bildschirmtastatur bleibt offen). */
  button(cls, html, label, text = false) {
    const b = el('button', cls);
    b.type = 'button';
    if (text) b.textContent = html; else b.innerHTML = html;
    b.setAttribute('aria-label', label);
    b.title = label;
    b.addEventListener('pointerdown', (e) => e.preventDefault());
    b.addEventListener('mousedown', (e) => e.preventDefault());
    return b;
  }

  renderKeys() {
    this.keys.replaceChildren();
    for (const spec of primaryKeys(this.program)) this.keys.appendChild(this.keyButton(spec, 'term-key'));
    if (this.more) this.closeMore();
  }
  keyButton(spec, cls) {
    const b = this.button(cls, spec.label, t(spec.aria), true);
    b.dataset.key = spec.id;
    b.addEventListener('click', () => this.pressKey(spec));
    return b;
  }

  /** Das Blatt "Mehr Tasten": alle Tasten mit Namen, Kopieren, Einfuegen, Schrift, Adresse oeffnen, Hilfe und Einklappen; jede Taste hat eine Beschriftung */
  openMore() {
    const panel = el('div', 'term-more');
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', t('term.moreKeys'));
    const head = el('div', 'term-more-h');
    head.append(el('b', '', t('term.moreKeys')));
    const done = this.button('term-more-done', t('common.done'), t('common.done'), true);
    done.addEventListener('click', () => this.closeMore());
    head.append(done);
    const grid = el('div', 'term-more-keys');
    for (const spec of quickKeys(this.program)) {
      const b = this.keyButton(spec, 'term-key big');
      b.replaceChildren(el('b', '', spec.label), el('small', '', t(spec.aria)));
      grid.append(b);
    }
    const acts = el('div', 'term-more-acts');
    const act = (label, fn) => { const b = this.button('term-act', label, label, true); b.addEventListener('click', fn); acts.append(b); return b; };
    act(t('term.copy'), () => this.copySelection());
    act(t('term.paste'), () => { this.closeMore(); this.pasteFromClipboard(); });
    act(t('term.fontSmaller'), () => this.view.zoom(-1));
    act(t('term.fontLarger'), () => this.view.zoom(1));
    if (isAssistant({ kind: 'terminal', program: this.program })) {
      act(t('term.openUrl'), () => this.openUrl());
      act(t('term.tipShow'), () => { this.closeMore(); this.showTip(true); });
    }
    act(t('term.barHide'), () => { this.closeMore(); this.setOpen(false); });
    panel.append(head, grid, acts);
    this.container.append(panel);
    this.more = panel;
    this.moreBtn.setAttribute('aria-expanded', 'true');
  }
  closeMore() {
    if (this.more) { this.more.remove(); this.more = null; }
    this.moreBtn.removeAttribute('aria-expanded');
  }

  /** Ersthinweis fuer Claude und Codex: so klappt die Anmeldung. Er erscheint einmal je Programm, bis er mit "Verstanden" geschlossen wird. */
  buildTip() {
    this.tip = null;
    if (!isAssistant({ kind: 'terminal', program: this.program })) return;
    let seen = false; try { seen = localStorage.getItem('pd.termTip.' + this.program) === '1'; } catch { /* egal */ }
    if (!seen) this.showTip(false);
  }
  showTip(force) {
    if (this.tip) return;
    const name = this.program === 'codex' ? 'Codex' : 'Claude';
    const tip = el('div', 'term-tip');
    tip.setAttribute('role', 'region');
    tip.setAttribute('aria-label', t('term.tipTitle', { name }));
    tip.append(el('b', '', t('term.tipTitle', { name })));
    const steps = el('ol', ''); for (const k of [tk('term.tipStep1'), tk('term.tipStep2'), tk('term.tipStep3')]) steps.append(el('li', '', t(k)));
    tip.append(steps);
    const row = el('div', 'term-tip-act');
    const open = this.button('term-act', t('term.openUrl'), t('term.openUrl'), true); open.addEventListener('click', () => this.openUrl());
    const ok = this.button('term-act primary', t('term.tipOk'), t('term.tipOk'), true);
    ok.addEventListener('click', () => { try { localStorage.setItem('pd.termTip.' + this.program, '1'); } catch { /* egal */ } tip.remove(); this.tip = null; });
    row.append(open, ok); tip.append(row);
    this.container.append(tip);
    this.tip = tip;
  }
  /** Die letzte Adresse im Terminal oeffnen (nur https, nur auf Tipp): die Anmeldeseite von Claude oder Codex */
  openUrl() {
    const buf = this.view.term && this.view.term.buffer && this.view.term.buffer.active;
    const lines = [];
    if (buf) for (let i = Math.max(0, buf.length - 400); i < buf.length; i++) { const ln = buf.getLine(i); if (ln) lines.push({ text: ln.translateToString(false), wrapped: ln.isWrapped }); }
    const url = findLastUrl(lines);
    if (!url) { this.view.notify(t('term.noUrl')); return; }
    let host = ''; try { host = new URL(url).host; } catch { this.view.notify(t('term.noUrl')); return; }
    this.view.notify(t('term.openingUrl', { host }));
    window.open(url, '_blank', 'noopener,noreferrer');
  }

  /** Programm geaendert (der Server hat zum Beispiel auf die Shell zurueckgegriffen): andere Schnelltasten. */
  setProgram(program) {
    program = programOf({ kind: 'terminal', program });
    if (program === this.program) return;
    this.program = program;
    this.renderKeys();
  }

  pressKey(spec) {
    if (!this.view.connected) return;
    const bytes = keyBytes(spec, !!this.view.term?.modes.applicationCursorKeysMode);
    if (bytes) this.view.sendText(bytes);
  }

  /** Inhalt des Felds senden: als Einfuegen (Klammer-Modus, wenn das Programm ihn kann), kurz danach Enter. */
  submit() {
    if (!this.view.connected) { this.view.notify(t('term.notConnected')); return; }
    const plan = sendPlan(this.field.value);
    if (plan.text !== null) this.view.paste(plan.text);
    if (plan.enter) {
      const bracketed = !!this.view.term?.modes.bracketedPasteMode;
      if (plan.text !== null && bracketed) setTimeout(() => { if (!this.destroyed) this.view.sendText('\r'); }, ENTER_DELAY_MS);
      else this.view.sendText('\r');
    }
    this.field.value = ''; this.state.draft = '';
    this.grow();
    this.field.focus();
  }

  /** Entwurf von aussen setzen (zum Beispiel der Installationsbefehl des Host-Agenten): steht im Feld, der Benutzer sendet ihn selbst mit Enter. */
  setDraft(text) {
    prefillState(this.state, text);
    this.field.value = this.state.draft;
    this.apply();
    this.field.selectionStart = this.field.selectionEnd = this.field.value.length;
  }

  insert(text) {
    const f = this.field, a = f.selectionStart ?? f.value.length, b = f.selectionEnd ?? a;
    f.value = f.value.slice(0, a) + text + f.value.slice(b);
    f.selectionStart = f.selectionEnd = a + text.length;
    this.state.draft = f.value;
    this.grow();
    f.focus();
  }

  /** Auswahl im Terminal in die Zwischenablage (Browser-Berechtigung, Rueckfall ueber ein verstecktes Feld: siehe TerminalView.copy). */
  copySelection() {
    if (!this.view.getSelection()) { this.view.notify(t('term.nothingSelected')); return; }
    this.view.copy();
  }

  /** Einfuegen aus der Zwischenablage in das Textfeld (zum Durchsehen vor dem Senden); ohne Erlaubnis des Browsers der Hinweis, wie es sonst geht. */
  async pasteFromClipboard() {
    try {
      const text = await navigator.clipboard.readText();
      if (text) this.insert(text);
    } catch { this.view.notify(t('term.pasteDenied')); this.field.focus(); }
  }

  grow() {
    this.field.rows = rowsFor(this.field.value);
    this.measure();
  }

  measure() {
    if (this.destroyed) return;
    const h = this.state.open ? Math.ceil(this.root.getBoundingClientRect().height) : 0;
    this.container.style.setProperty('--term-bar-h', h + 'px');
  }

  setOpen(open) {
    this.state.open = !!open;
    this.apply();
  }

  apply() {
    this.root.hidden = !this.state.open;
    this.handle.hidden = this.state.open;
    this.container.classList.toggle('has-term-bar', this.state.open);
    this.grow();
  }

  focusInput() { if (this.state.open) this.field.focus(); }

  destroy() {
    this.destroyed = true;
    this.ro?.disconnect();
    this.root.remove(); this.handle.remove(); if (this.tip) this.tip.remove(); if (this.more) this.more.remove();
    this.container.style.removeProperty('--term-bar-h');
    this.container.classList.remove('has-term-bar');
  }
}
