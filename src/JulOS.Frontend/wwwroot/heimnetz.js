/* =====================================================================
 * Karte "Heimnetz": Dienste, Caddy (Zustand, Domains, Zertifikate) und DDNS auf einen Blick mit Ampel. Gemeinsam fuer die Startseite des Desktops
 * und die Startseite des Handys. Nur Module, die der Server eingeschaltet hat, kommen vor; jeder Abruf scheitert fuer sich (ein Modul, das nicht
 * antwortet, ist grau, nicht der Rest). Die Auswertung (summarize) ist reine Logik und in tests/frontend/modules.test.mjs geprueft.
 * ===================================================================== */
import { t, tp, tk } from './i18n.js';
import { certSummary } from './modules.js';

const RANK = { idle: 0, ok: 1, warn: 2, bad: 3 };
export const worst = (levels) => levels.reduce((a, b) => (RANK[b] > RANK[a] ? b : a), 'idle');

/** data: { services?: {list, statuses}, caddy?: {health, certs, routes}, ddns?: status } -> { level, items: [{key, level, text}] } */
export function summarize(data) {
  const items = [];
  if (data.services) {
    const list = data.services.list || [], by = new Map((data.services.statuses || []).map((s) => [s.id, s]));
    const st = list.map((s) => by.get(s.id));
    const down = list.filter((_, i) => st[i] && st[i].state === 'down');
    const up = st.filter((s) => s && s.state === 'up').length;
    let level = 'idle', text = t('heimnetz.noServices');
    if (list.length) {
      level = down.length ? 'bad' : up === list.length ? 'ok' : 'idle';
      text = down.length ? t('heimnetz.servicesDown', { up, total: list.length, names: down.slice(0, 3).map((s) => s.name).join(', ') }) : up === list.length ? tp('heimnetz.servicesUp', list.length) : t('heimnetz.servicesSome', { up, total: list.length });
    }
    items.push({ key: 'services', level, text });
  }
  if (data.caddy) {
    const { health, certs, routes } = data.caddy;
    const sum = certSummary(certs && certs.certificates);
    const domains = routes ? new Set(routes.map((r) => r.domain)).size : 0;
    let level = 'idle';
    const parts = [];
    if (health && health.state === 'down') { level = 'bad'; parts.push(t('heimnetz.caddyDown')); }
    else {
      if (health && health.state === 'up') { level = 'ok'; parts.push(t('heimnetz.caddyUp')); }
      if (domains) parts.push(tp('heimnetz.domains', domains));
      if (sum.bad) { level = 'bad'; parts.push(tp('heimnetz.certsBad', sum.bad)); }
      else if (sum.warn) { level = worst([level, 'warn']); parts.push(tp('heimnetz.certsWarn', sum.warn)); }
      else if (sum.total) parts.push(tp('heimnetz.certsOk', sum.total));
    }
    items.push({ key: 'caddy', level, text: parts.join(' · ') || t('heimnetz.caddyUnknown') });
  }
  if (data.ddns) {
    const s = data.ddns;
    const level = s.state === 'synced' ? 'ok' : s.state === 'notSynced' ? 'warn' : 'idle';
    const text = s.state === 'synced' ? t('heimnetz.ddnsSynced', { ip: s.publicIp || '' }) : s.state === 'notSynced' ? tp('heimnetz.ddnsOff', (s.hosts || []).filter((h) => h.state !== 'synced').length) : t('heimnetz.ddnsUnknown');
    items.push({ key: 'ddns', level, text });
  }
  return { level: worst(items.map((i) => i.level)), items };
}

const LEVEL_TEXT = { ok: tk('heimnetz.levelOk'), warn: tk('heimnetz.levelWarn'), bad: tk('heimnetz.levelBad'), idle: tk('heimnetz.levelIdle') };
const LABEL = { services: tk('heimnetz.services'), caddy: tk('heimnetz.caddy'), ddns: tk('heimnetz.ddns') };

/** Holt, was die eingeschalteten Module melden; ein Fehler macht nur dieses Modul stumm. */
export async function loadHeimnetz(api, mods) {
  const soft = (p) => p.catch(() => null);
  const data = {};
  const jobs = [];
  if (mods.services) jobs.push(Promise.all([soft(api('/api/modules/services')), soft(api('/api/modules/services/status'))]).then(([l, s]) => { data.services = { list: (l && l.services) || [], statuses: (s && s.statuses) || [] }; }));
  if (mods.caddy) jobs.push(Promise.all([soft(api('/api/modules/caddy/health')), soft(api('/api/modules/caddy/certificates')), soft(api('/api/modules/caddy/routes'))]).then(([health, certs, r]) => { data.caddy = { health, certs, routes: r && r.routes }; }));
  if (mods.ddns) jobs.push(soft(api('/api/modules/ddns/status')).then((s) => { if (s) data.ddns = s; else data.ddns = { state: 'unknown', hosts: [] }; }));
  await Promise.all(jobs);
  return data;
}

let cache = null;   // { at, data }: die Karte wird beim Neuzeichnen der Startseite sofort aus dem Speicher gefuellt
export const resetHeimnetz = () => { cache = null; };
const FRESH_MS = 30000;

/**
 * Zeichnet die Karte in box und haelt sie aktuell (alle 60 s, solange box im Dokument steht). deps: { api, mods, open(key), esc }.
 * Ohne eingeschaltetes Modul bleibt box leer.
 */
export function mountHeimnetz(box, { api, mods, open, esc }) {
  const any = mods.services || mods.caddy || mods.ddns;
  box.replaceChildren(); box.classList.add('wd');
  if (!any) return { destroy() {} };
  let timer = null;
  const draw = (data) => {
    const sum = summarize(data);
    box.innerHTML = `<div class="hn"><div class="hn-head"><span class="wd-dot ${sum.level}"></span><b>${esc(t(LEVEL_TEXT[sum.level]))}</b></div>
      ${sum.items.map((i) => `<button type="button" class="hn-row" data-open="${i.key}"><span class="wd-dot ${i.level}"></span><span class="hn-name">${esc(t(LABEL[i.key]))}</span><span class="hn-text">${esc(i.text)}</span></button>`).join('')}</div>`;
  };
  const load = async (force) => {
    if (!force && cache && Date.now() - cache.at < FRESH_MS) return;
    const data = await loadHeimnetz(api, mods);
    cache = { at: Date.now(), data };
    if (box.isConnected) draw(data);
  };
  if (cache) draw(cache.data); else box.innerHTML = `<div class="hn"><div class="hn-head"><span class="wd-dot idle"></span><b>${esc(t('common.loading'))}</b></div></div>`;
  box.onclick = (e) => { const b = e.target.closest('[data-open]'); if (b) open(b.dataset.open); };
  load(false);
  timer = setInterval(() => { if (!box.isConnected) return clearInterval(timer); if (!document.hidden) load(true); }, 60000);
  return { destroy() { clearInterval(timer); } };
}
