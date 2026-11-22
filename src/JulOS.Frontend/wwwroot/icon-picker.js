import { PICK_KEYS, GLYPH_LABEL, COLORS, COLOR_LABEL, glyph, connectionIcon } from './icons.js';
import { withOverride } from './appearance.js';
import { t } from './i18n.js';

/* =====================================================================
 * Symbol einer Verbindung waehlen (docs/ICONS.md): eine Zeile im Dialog der Verbindung (index.html, #connIconRow) mit Vorschau, "Aendern ..." (Symbole der
 * Familie und Farben) und "Automatisch" (zurueck auf die Zuordnung aus icons.js). Gilt sofort und auf allen Geraeten: gespeichert wird im Aussehen
 * (appearance.icons.over), nicht in der Verbindung. Gemeinsam fuer Handy und Desktop.
 * ===================================================================== */
const $ = (s) => document.querySelector(s);

/** @param {{getAppearance: () => object, setAppearance: (a: object) => void, hosts: () => object[]}} o @returns {{show: (conn: object|null) => void}} */
export function initIconPicker({ getAppearance, setAppearance, hosts }) {
  const row = $('#connIconRow'), prev = $('#connIconPrev'), pick = $('#connIconPick');
  let conn = null, open = false;
  const over = () => (conn && getAppearance().icons.over[conn.id]) || {};

  function draw() {
    const i = connectionIcon(conn, hosts(), getAppearance());
    prev.innerHTML = glyph(i.key); prev.style.setProperty('--g1', i.g[0]); prev.style.setProperty('--g2', i.g[1]);
    $('#connIconAuto').hidden = i.auto;
    $('#connIconChange').setAttribute('aria-expanded', String(open));
    pick.hidden = !open;
    if (!open) return;
    pick.innerHTML = `<div class="glyphs" role="radiogroup" aria-label="${t('ico.title')}">${PICK_KEYS.map((k) => `<button type="button" class="pk" role="radio" aria-checked="${k === i.key}" data-k="${k}" aria-label="${t(GLYPH_LABEL[k])}" title="${t(GLYPH_LABEL[k])}">${glyph(k)}</button>`).join('')}</div>`
      + `<div class="colors" role="radiogroup" aria-label="${t('ico.color')}">${Object.entries(COLORS).map(([c, g]) => `<button type="button" class="cl" role="radio" aria-checked="${c === i.color}" data-c="${c}" style="--g1:${g[0]};--g2:${g[1]}" aria-label="${t(COLOR_LABEL[c])}" title="${t(COLOR_LABEL[c])}"></button>`).join('')}</div>`;
  }
  pick.addEventListener('click', (e) => {
    const b = e.target.closest('[data-k],[data-c]'); if (!b || !conn) return;
    setAppearance(withOverride(getAppearance(), conn.id, { ...over(), ...(b.dataset.k ? { k: b.dataset.k } : { c: b.dataset.c }) }));
    draw();
  });
  $('#connIconChange').addEventListener('click', () => { open = !open; draw(); });
  $('#connIconAuto').addEventListener('click', () => { if (!conn) return; setAppearance(withOverride(getAppearance(), conn.id, null)); open = false; draw(); });
  return {
    /** Beim Oeffnen des Dialogs: conn = die Verbindung (null bei einer neuen: sie hat noch keine Kennung, die Zeile bleibt weg) */
    show(c) { conn = c; open = false; row.hidden = !c; if (c) draw(); },
  };
}
