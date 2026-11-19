/* =====================================================================
 * Karten und Detail-Sheets fuer Caddy und DDNS in der Handy-Shell (nur lesen). Wird von mobile.js erst geladen, wenn der Server
 * mindestens ein Modul eingeschaltet hat. deps: { icon, esc, api, wireSheet, app }
 * ===================================================================== */
import { t, tk, tp } from './i18n.js';
import { mountWidgets } from './widgets.js';
import { routeParts, sourceLabel, reachMap, reachText, reachClass, ddnsStateLabel, ddnsStateClass, ddnsHeadline, ago } from './modules.js';

const META = {
  caddy: { title: 'Caddy', sub: tk('mobile.modCaddySub'), ic: 'lock' },
  ddns: { title: 'DDNS', sub: tk('mobile.modDdnsSub'), ic: 'dns' },
};

/** Kurzer Zustand fuer die Karte; kein Abruf schreibt etwas, und ein Fehler macht die Karte nur still. */
async function summary(kind, api) {
  if (kind === 'caddy') {
    const l = await api('/api/modules/caddy/routes');
    const n = new Set(l.routes.map((r) => r.domain)).size;
    return { text: l.source === 'none' ? t('mobile.modNoSource') : tp('mobile.modDomains', n), cls: l.source === 'none' ? 'warn' : '' };
  }
  const s = await api('/api/modules/ddns/status');
  return { text: ddnsStateLabel(s.state), cls: s.state === 'synced' ? 'ok' : s.state === 'notSynced' ? 'warn' : '' };
}

export function renderCards(box, mods, { icon, esc, api }) {
  const kinds = ['caddy', 'ddns'].filter((k) => mods[k]);
  if (!kinds.length) { box.replaceChildren(); return; }
  box.innerHTML = `<div class="m3-sec">${icon('dns')}<span>${esc(t('mobile.modules'))}</span></div>
    <div class="m3-card acts">${kinds.map((k) => `<button type="button" class="mb m3-li m3-state" data-act="mod" data-mod="${k}"><span class="lead" data-lead="${k}">${icon(META[k].ic)}</span><span class="txt"><span class="hl">${META[k].title}</span><span class="sl" data-sum="${k}">${esc(t(META[k].sub))}</span></span></button>`).join('')}</div>`;
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
    const states = r.kind === 'proxy' ? (r.targets || []).map((tg) => reach.get(tg)).filter(Boolean) : [];
    const worst = states.find((s) => s.state === 'down') || states.find((s) => s.state === 'skipped') || states[0];
    return row(esc, p.domain + (p.path ? ' ' + p.path : ''), p.kind + ' → ' + p.target, worst ? reachText(worst) : '', reachClass(worst));
  });
  return `<p class="hint">${esc(t('mobile.modSource', { source: sourceLabel(l.source) }))}${l.warnings.length ? ' ' + esc(l.warnings.join(' ')) : ''}</p>
    <div class="m3-card acts" style="margin:0">${rows.join('') || `<p class="empty-hint">${esc(t('mobile.modNoEntries'))}</p>`}</div>
    <p class="hint" style="margin-top:12px">${esc(t('mobile.modCaddyNote'))}</p>`;
}

async function ddnsBody({ esc, api }) {
  const s = await api('/api/modules/ddns/status');
  const rows = s.hosts.map((h) => row(esc, h.host, h.addresses.length ? t('mobile.modResolves', { addrs: h.addresses.join(', ') }) : t('mobile.modNoResolve'), ddnsStateLabel(h.state), ddnsStateClass(h.state)));
  return `<p class="hint"><b>${esc(ddnsStateLabel(s.state))}</b> · ${esc(t('mobile.modChecked', { when: ago(s.checkedAt) }))}<br>${esc(ddnsHeadline(s))}<br>${s.publicIp ? esc(t('mobile.modPublicIp', { ip: s.publicIp })) + (s.source ? ' ' + esc(t('mobile.modIpSource', { source: s.source })) : '') : esc(t('mobile.modPublicIpUnknown'))}</p>
    <div class="m3-card acts" style="margin:0">${rows.join('') || `<p class="empty-hint">${esc(t('mobile.modNoNames'))}</p>`}</div>
    <p class="hint" style="margin-top:12px">${esc(t('mobile.modDdnsNote'))}</p>`;
}

export async function openSheet(kind, deps) {
  const { esc, app, wireSheet, icon } = deps;
  let dlg = document.getElementById('mModDlg');
  if (!dlg) { dlg = document.createElement('dialog'); dlg.id = 'mModDlg'; dlg.className = 'm3sheet'; app.appendChild(dlg); wireSheet(dlg); }
  const m = META[kind];
  const draw = async (check) => {
    dlg.innerHTML = `<h2>${m.title}</h2><p class="hint">${esc(t('mobile.loading'))}</p>`;
    let body;
    try { body = kind === 'caddy' ? await caddyBody(deps, check) : await ddnsBody(deps); }
    catch (e) { body = `<p class="hint">${esc(e.message || t('mobile.modNotLoaded'))}</p>`; }
    dlg.innerHTML = `<h2>${m.title}</h2>${body}
      <div class="row" style="margin-top:14px">${kind === 'caddy' ? `<button type="button" class="mb m3-btn tonal m3-state" data-a="check">${icon('check')}${esc(t('mobile.modCheckReach'))}</button>` : ''}<button type="button" class="mb m3-btn filled m3-state" data-a="reload">${icon('history')}${esc(t('common.reload'))}</button></div>`;
  };
  dlg.onclick = (e) => { const b = e.target.closest('[data-a]'); if (!b) return; draw(b.dataset.a === 'check'); };
  if (!dlg.open) dlg.showModal();
  await draw(false);
}

/** Dienste-Widgets auf der Dienste-Seite: Ueberschrift und das Raster aus widgets.js. */
export function mountServices(box, { api, wireSheet, icon, esc }) {
  box.innerHTML = `<div class="m3-sec">${icon('apps')}<span>${esc(t('mobile.widgets'))}</span></div><div class="wd-host"></div>`;
  return mountWidgets(box.querySelector('.wd-host'), { api, mobile: true, wireSheet });
}
