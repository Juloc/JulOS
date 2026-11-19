/* =====================================================================
 * Karten und Detail-Sheets fuer Caddy und DDNS in der Handy-Shell (nur lesen). Wird von mobile.js erst geladen, wenn der Server
 * mindestens ein Modul eingeschaltet hat. deps: { icon, esc, api, wireSheet, app }
 * ===================================================================== */
import { routeParts, sourceLabel, reachMap, reachText, reachClass, ddnsStateLabel, ddnsStateClass, ddnsHeadline, ago } from './modules.js';

const META = {
  caddy: { title: 'Caddy', sub: 'Domains und ihre Ziele', ic: 'lock' },
  ddns: { title: 'DDNS', sub: 'Öffentliche Adresse und Namen', ic: 'dns' },
};

/** Kurzer Zustand fuer die Karte; kein Abruf schreibt etwas, und ein Fehler macht die Karte nur still. */
async function summary(kind, api) {
  if (kind === 'caddy') {
    const l = await api('/api/modules/caddy/routes');
    const n = new Set(l.routes.map((r) => r.domain)).size;
    return { text: l.source === 'none' ? 'Keine Quelle lesbar' : `${n} ${n === 1 ? 'Domain' : 'Domains'}`, cls: l.source === 'none' ? 'warn' : '' };
  }
  const s = await api('/api/modules/ddns/status');
  return { text: ddnsStateLabel(s.state), cls: s.state === 'synced' ? 'ok' : s.state === 'notSynced' ? 'warn' : '' };
}

export function renderCards(box, mods, { icon, esc, api }) {
  const kinds = ['caddy', 'ddns'].filter((k) => mods[k]);
  if (!kinds.length) { box.replaceChildren(); return; }
  box.innerHTML = `<div class="m3-sec">${icon('dns')}<span>Module</span></div>
    <div class="m3-card acts">${kinds.map((k) => `<button type="button" class="mb m3-li m3-state" data-act="mod" data-mod="${k}"><span class="lead" data-lead="${k}">${icon(META[k].ic)}</span><span class="txt"><span class="hl">${META[k].title}</span><span class="sl" data-sum="${k}">${esc(META[k].sub)}</span></span></button>`).join('')}</div>`;
  for (const k of kinds) {
    summary(k, api).then((s) => {
      const sl = box.querySelector(`[data-sum="${k}"]`), lead = box.querySelector(`[data-lead="${k}"]`);
      if (sl) sl.textContent = s.text;
      if (lead && s.cls) lead.classList.add(s.cls);
    }).catch(() => { /* die Karte bleibt bei ihrer Beschriftung */ });
  }
}

const row = (esc, main, sub, chip, cls) => `<div class="m3-li static"><span class="txt"><span class="hl">${esc(main)}</span><span class="sl">${esc(sub)}</span></span>${chip ? `<span class="tr m3mod ${cls || ''}">${esc(chip)}</span>` : ''}</div>`;

async function caddyBody({ esc, api }, check) {
  const l = await api('/api/modules/caddy/routes');
  const reach = check ? reachMap(await api('/api/modules/caddy/reachability')) : new Map();
  const rows = l.routes.map((r) => {
    const p = routeParts(r);
    const states = r.kind === 'proxy' ? (r.targets || []).map((t) => reach.get(t)).filter(Boolean) : [];
    const worst = states.find((s) => s.state === 'down') || states.find((s) => s.state === 'skipped') || states[0];
    return row(esc, p.domain + (p.path ? ' ' + p.path : ''), p.kind + ' → ' + p.target, worst ? reachText(worst) : '', reachClass(worst));
  });
  return `<p class="hint">Quelle: ${esc(sourceLabel(l.source))}.${l.warnings.length ? ' ' + esc(l.warnings.join(' ')) : ''}</p>
    <div class="m3-card acts" style="margin:0">${rows.join('') || '<p class="empty-hint">Keine Einträge gefunden.</p>'}</div>
    <p class="hint" style="margin-top:12px">Nur Ansicht: JulOS ändert Caddy nie. Zugangsdaten und Kopfzeilen der Konfiguration werden nicht angezeigt.</p>`;
}

async function ddnsBody({ esc, api }) {
  const s = await api('/api/modules/ddns/status');
  const rows = s.hosts.map((h) => row(esc, h.host, h.addresses.length ? 'löst auf: ' + h.addresses.join(', ') : 'löst nicht auf', ddnsStateLabel(h.state), ddnsStateClass(h.state)));
  return `<p class="hint"><b>${esc(ddnsStateLabel(s.state))}</b> · geprüft ${esc(ago(s.checkedAt))}<br>${esc(ddnsHeadline(s))}<br>${s.publicIp ? 'Öffentliche Adresse: ' + esc(s.publicIp) + (s.source ? ' (Quelle: ' + esc(s.source) + ')' : '') : 'Öffentliche Adresse: unbekannt'}</p>
    <div class="m3-card acts" style="margin:0">${rows.join('') || '<p class="empty-hint">Keine Namen eingestellt.</p>'}</div>
    <p class="hint" style="margin-top:12px">Nur Ansicht: JulOS schreibt nichts, es gibt kein Token. Neu gemessen wird höchstens einmal je Minute.</p>`;
}

export async function openSheet(kind, deps) {
  const { esc, app, wireSheet, icon } = deps;
  let dlg = document.getElementById('mModDlg');
  if (!dlg) { dlg = document.createElement('dialog'); dlg.id = 'mModDlg'; dlg.className = 'm3sheet'; app.appendChild(dlg); wireSheet(dlg); }
  const m = META[kind];
  const draw = async (check) => {
    dlg.innerHTML = `<h2>${m.title}</h2><p class="hint">Wird geladen …</p>`;
    let body;
    try { body = kind === 'caddy' ? await caddyBody(deps, check) : await ddnsBody(deps); }
    catch (e) { body = `<p class="hint">${esc(e.message || 'Nicht geladen.')}</p>`; }
    dlg.innerHTML = `<h2>${m.title}</h2>${body}
      <div class="row" style="margin-top:14px">${kind === 'caddy' ? `<button type="button" class="mb m3-btn tonal m3-state" data-a="check">${icon('check')}Erreichbarkeit prüfen</button>` : ''}<button type="button" class="mb m3-btn filled m3-state" data-a="reload">${icon('history')}Neu laden</button></div>`;
  };
  dlg.onclick = (e) => { const b = e.target.closest('[data-a]'); if (!b) return; draw(b.dataset.a === 'check'); };
  if (!dlg.open) dlg.showModal();
  await draw(false);
}
