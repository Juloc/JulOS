/* =====================================================================
 * Bausteine der Host-Oberflaeche (docs/ui-kit.html, Abschnitt "Hosts"): Symbole, Karte, Zeilen, Hinweise, Listen, Balken, Chips. Reine Funktionen ohne DOM, damit Handy und Desktop
 * dieselben Karten zeigen und Node sie testen kann. Das Aussehen steht in hostui.css (Farben und Masse kommen aus den Tokens der jeweiligen Oberflaeche).
 * Jeder Wert vom Agenten oder Server geht durch esc (der Agent ist nicht der Server); die Funktionen bekommen esc als Argument, wie im Rest der Oberflaeche.
 * ===================================================================== */

/** Symbole (Material Symbols, 24er Raster, nur Pfade): nie Emojis als Symbole */
const ICONS = {
  server: 'M20 13H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1v-6c0-.55-.45-1-1-1zM7 19c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zM20 3H4c-.55 0-1 .45-1 1v6c0 .55.45 1 1 1h16c.55 0 1-.45 1-1V4c0-.55-.45-1-1-1zM7 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z',
  more: 'M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z',
  refresh: 'M17.65 6.35A7.958 7.958 0 0 0 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08A5.99 5.99 0 0 1 12 18c-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z',
  close: 'M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
  info: 'M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z',
  ok: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z',
  warn: 'M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z',
  error: 'M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z',
  dashboard: 'M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z',
  speed: 'M20.38 8.57l-1.23 1.85a8 8 0 0 1-.22 7.58H5.07A8 8 0 0 1 15.58 6.85l1.85-1.23A10 10 0 0 0 3.35 19a2 2 0 0 0 1.72 1h13.85a2 2 0 0 0 1.74-1 10 10 0 0 0-.27-10.44zm-9.79 6.84a2 2 0 0 0 2.83 0l5.66-8.49-8.49 5.66a2 2 0 0 0 0 2.83z',
  memory: 'M15 9H9v6h6V9zm-2 4h-2v-2h2v2zm8-2V9h-2V7c0-1.1-.9-2-2-2h-2V3h-2v2h-2V3H9v2H7c-1.1 0-2 .9-2 2v2H3v2h2v2H3v2h2v2c0 1.1.9 2 2 2h2v2h2v-2h2v2h2v-2h2c1.1 0 2-.9 2-2v-2h2v-2h-2v-2h2zm-4 6H7V7h10v10z',
  disk: 'M2 20h20v-4H2v4zm2-3h2v2H4v-2zM2 4v4h20V4H2zm4 3H4V5h2v2zm-4 7h20v-4H2v4zm2-3h2v2H4v-2z',
  network: 'M13 22h8v-7h-3v-4h-5V9h3V2H8v7h3v2H6v4H3v7h8v-7H8v-2h8v2h-3v7z',
  update: 'M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z',
  download: 'M5 20h14v-2H5v2zM19 9h-4V3H9v6H5l7 7 7-7z',
  shield: 'M12 1 3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4z',
  gear: 'M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.488.488 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.484.484 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z',
  box: 'M20 2H4c-1 0-2 .9-2 2v3.01c0 .72.43 1.34 1 1.69V20c0 1.1 1.1 2 2 2h14c.9 0 2-.9 2-2V8.7c.57-.35 1-.97 1-1.69V4c0-1.1-1-2-2-2zm-5 12H9v-2h6v2zm5-7H4V4h16v3z',
  logs: 'M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-5 14H7v-2h7v2zm3-4H7v-2h10v2zm0-4H7V7h10v2z',
  tune: 'M3 17v2h6v-2H3zM3 5v2h10V5H3zm10 16v-2h8v-2h-8v-2h-2v6h2zM7 9v2H3v2h4v2h2V9H7zm14 4v-2H11v2h10zm-6-4h2V7h4V5h-4V3h-2v6z',
  history: 'M13 3a9 9 0 0 0-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42A8.954 8.954 0 0 0 13 21a9 9 0 0 0 0-18zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z',
  power: 'M13 3h-2v10h2V3zm4.83 2.17-1.42 1.42A6.92 6.92 0 0 1 19 12c0 3.87-3.13 7-7 7A6.995 6.995 0 0 1 7.58 6.58L6.17 5.17A8.932 8.932 0 0 0 3 12a9 9 0 0 0 18 0c0-2.74-1.23-5.18-3.17-6.83z',
  edit: 'M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a.996.996 0 0 0 0-1.41l-2.34-2.34a.996.996 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z',
  add: 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z',
  terminal: 'M20 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H4V8h16v10zm-2-1h-6v-2h6v2zM7.5 17l-1.41-1.41L8.67 13l-2.58-2.59L7.5 9l4 4-4 4z',
  back: 'M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z',
  chevron: 'M8.59 16.59 13.17 12 8.59 7.41 10 6l6 6-6 6z',
};
export const icon = (name, cls = '') => `<svg class="hu-i${cls ? ' ' + cls : ''}" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="${ICONS[name] || ICONS.info}"/></svg>`;
export const ICON_NAMES = Object.keys(ICONS);

const multi = (v, esc) => String(v).split('\n').map(esc).join('<br>');

/** Zeilen "Name  Wert" in einer Karte; rows: [[name, wert, 'warn'?]]; leere Werte entfallen, ein Wert darf mehrere Zeilen haben */
export const dl = (rows, esc) => {
  const shown = rows.filter((r) => r[1] !== '' && r[1] != null);
  return shown.length ? `<div class="hu-kv">${shown.map(([k, v, cls]) => `<div class="hu-kvr${/\bwarn\b/.test(cls || '') ? ' bad' : ''}"><span class="k">${esc(k)}</span><span class="v">${multi(v, esc)}</span></div>`).join('')}</div>` : '';
};

/** Ein Hinweis in ruhigem Text; warn hebt ihn hervor */
export const noteP = (text, esc, warn = false) => `<p class="hu-note${warn ? ' warn' : ''}">${esc(text)}</p>`;

/** Eine Aussage mit Symbol und Ton: ok, info, warn oder bad (die Kernaussage eines Abschnitts: "Updates: 3 verfuegbar") */
export const callout = (text, esc, tone = 'info', detail = '') => `<div class="hu-msg ${tone}">${icon(tone === 'ok' ? 'ok' : tone === 'bad' ? 'error' : tone === 'warn' ? 'warn' : 'info')}<div><b>${esc(text)}</b>${detail ? `<span>${esc(detail)}</span>` : ''}</div></div>`;
export const sumP = (text, esc, warn = false) => callout(text, esc, warn ? 'warn' : 'info');

export const subH = (text, esc) => `<h4 class="hu-sub">${esc(text)}</h4>`;

/** Chip mit Text; tone: '' | ok | warn | bad */
export const chip = (text, esc, tone = '', ico = '') => `<span class="hu-chip${tone ? ' ' + tone : ''}">${ico ? icon(ico) : ''}<span>${esc(text)}</span></span>`;

/** Ein Balken (Prozent 0 bis 100); label wird nur vorgelesen. Ab warnAt Prozent amber, ab badAt rot */
export function bar(pct, esc, label = '', warnAt = 80, badAt = 90) {
  const raw = Number(pct) || 0, p = Math.max(0, Math.min(100, Math.round(raw)));
  const tone = raw >= badAt ? ' bad' : raw >= warnAt ? ' warn' : '';
  return `<span class="hu-bar${tone}" role="meter" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${p}"${label ? ` aria-label="${esc(label)}"` : ''}><i style="width:${p}%"></i></span>`;
}

/** Eine Kennzahl: Beschriftung, Wert, Balken (pct null: kein Balken), optional eine Zeile darunter */
export const meter = (m, esc) => `<div class="hu-meter${m.warn ? ' warn' : ''}"><div class="hu-mh">${m.icon ? icon(m.icon) : ''}<span class="hu-ml">${esc(m.label)}</span><span class="hu-mv">${esc(m.value)}</span></div>${m.pct == null ? '' : bar(m.pct, esc, m.label, m.warnAt, m.badAt)}${m.sub ? `<span class="hu-ms">${esc(m.sub)}</span>` : ''}</div>`;

/** Eine Karte mit Titel (und Symbol, rechts optional fertiges HTML) und Inhalt */
export const card = (title, inner, esc, o = {}) => `<section class="hu-card${o.cls ? ' ' + o.cls : ''}"${o.id ? ` data-card="${esc(o.id)}"` : ''}>${title ? `<header class="hu-ch">${o.icon ? icon(o.icon) : ''}<h4>${esc(title)}</h4>${o.aside ? `<span class="hu-ca">${o.aside}</span>` : ''}</header>` : ''}${inner}</section>`;

/** Eine Liste aus { name, detail, badge? (Text, rot, oder { text, tone }), off?, actions? (fertiges HTML der Aktionstasten, nie Text des Agenten) } */
export const listBlock = (items, esc) => `<ul class="hu-list">${items.map((i) => { const b = typeof i.badge === 'string' ? (i.badge ? { text: i.badge, tone: 'bad' } : null) : i.badge; return `<li class="${[i.off ? 'off' : '', b ? 'tagged' : ''].filter(Boolean).join(' ')}"><div class="hu-lt"><span class="hu-ln">${esc(i.name)}</span>${b ? `<span class="hu-chip ${esc(b.tone)}">${esc(b.text)}</span>` : ''}</div>${i.detail ? `<span class="hu-ld">${esc(i.detail)}</span>` : ''}${i.actions || ''}</li>`; }).join('')}</ul>`;

/** Ein leerer Zustand mit klarer naechster Handlung; action ist fertiges HTML einer Taste */
export const empty = (title, text, esc, action = '', ico = 'server') => `<div class="hu-empty">${icon(ico, 'big')}<b>${esc(title)}</b><span>${esc(text)}</span>${action}</div>`;

/** Tasten: tone filled | tonal | text | danger; data: Attribute als Objekt (Werte werden geschuetzt) */
export const button = (text, esc, o = {}) => `<button type="button"${o.disabled ? ' disabled' : ''} class="hu-btn ${o.tone || 'tonal'}${o.sm ? ' sm' : ''}${o.cls ? ' ' + o.cls : ''}"${Object.entries(o.data || {}).map(([k, v]) => ` data-${k}="${esc(v)}"`).join('')}${o.aria ? ` aria-label="${esc(o.aria)}"` : ''}${o.title ? ` title="${esc(o.title)}"` : ''}>${o.icon ? icon(o.icon) : ''}${text ? `<span>${esc(text)}</span>` : ''}</button>`;
