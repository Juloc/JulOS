/* =====================================================================
 * Fenster Caddy und DDNS im Desktop (nur lesen). Wird von desktop.js erst beim ersten Oeffnen geladen.
 * Caddy: Liste Domain -> Ziel aus der Konfiguration, "Erreichbarkeit pruefen" (TCP-Test nur zu den gelisteten Zielen).
 * DDNS: aktuelle oeffentliche Adresse, Zustand je Name, Zeit der letzten Pruefung. Nichts hier aendert etwas am Server oder an Caddy.
 * deps: { el, btn, ic, api }
 * ===================================================================== */
import { routeParts, sourceLabel, reachMap, reachText, reachClass, ddnsStateLabel, ddnsStateClass, ddnsHeadline, ago } from './modules.js';
import { t, tp } from './i18n.js';
import { serverMsg } from './i18n-errors.js';

const text = (tag, cls, content) => { const e = document.createElement(tag); if (cls) e.className = cls; e.textContent = content; return e; };

export function mountCaddy(body, { el, btn, ic, api }) {
  body.innerHTML = '';
  const root = el('div', 'dk-mod');
  const tool = el('div', 'dk-mtool');
  const note = text('span', 'dk-mnote', t('desktop.loadingDots'));
  const reload = btn('dk-btn', ic('refresh') + t('common.reload'));
  const check = btn('dk-btn', ic('activity') + t('desktop.checkReach'));
  tool.append(note, reload, check);
  const list = el('div', 'dk-mlist'); list.setAttribute('aria-live', 'polite');
  const foot = text('p', 'dk-mfoot', t('desktop.caddyFoot'));
  root.append(tool, list, foot); body.appendChild(root);

  let routes = [], reach = new Map(), seq = 0;

  function draw() {
    list.replaceChildren();
    if (!routes.length) { list.appendChild(text('div', 'dk-fempty', t('desktop.noEntries'))); return; }
    for (const r of routes) {
      const p = routeParts(r);
      const row = el('div', 'dk-row dk-mrow');
      const main = el('span', 'dk-tt');
      const b = text('b', '', p.domain + (p.path ? '  ' + p.path : ''));
      const i = text('i', '', p.kind + ' → ' + p.target);
      i.title = p.target;
      main.append(b, i);
      row.appendChild(main);
      if (r.kind === 'proxy') {
        const states = (r.targets || []).map((target) => reach.get(target)).filter(Boolean);
        if (states.length) {
          const worst = states.find((s) => s.state === 'down') || states.find((s) => s.state === 'skipped') || states[0];
          const dot = text('span', 'dk-mstate ' + reachClass(worst), reachText(worst));
          row.appendChild(dot);
        }
      }
      list.appendChild(row);
    }
  }

  async function load() {
    const my = ++seq;
    note.textContent = t('desktop.loadingDots');
    try {
      const l = await api('/api/modules/caddy/routes');
      if (my !== seq) return;
      routes = l.routes || []; reach = new Map();
      draw();
      note.textContent = t('desktop.caddyNote', { count: tp('desktop.entries', routes.length), source: sourceLabel(l.source) }) + (l.warnings && l.warnings.length ? ' · ' + l.warnings.join(' ') : '');
    } catch (e) {
      if (my !== seq) return;
      list.replaceChildren(text('div', 'dk-fempty', serverMsg(e.message) || t('desktop.notLoaded'))); note.textContent = '';
    }
  }
  async function probe() {
    check.disabled = true; note.textContent = t('desktop.checkingTargets');
    try {
      reach = reachMap(await api('/api/modules/caddy/reachability'));
      draw();
      const down = [...reach.values()].filter((u) => u.state === 'down').length;
      note.textContent = reach.size ? (down ? tp('desktop.checkedDown', reach.size, { down }) : tp('desktop.checkedAll', reach.size)) : t('desktop.noTargets');
    } catch (e) { note.textContent = serverMsg(e.message) || t('desktop.checkFailed'); }
    finally { check.disabled = false; }
  }
  reload.addEventListener('click', load);
  check.addEventListener('click', probe);
  load();
  return { reload: load };
}

export function mountDdns(body, { el, btn, ic, api }) {
  body.innerHTML = '';
  const root = el('div', 'dk-mod');
  const head = el('div', 'dk-card dk-mhead');
  const list = el('div', 'dk-mlist'); list.setAttribute('aria-live', 'polite');
  const reload = btn('dk-btn', ic('refresh') + t('desktop.checkNow'));
  const foot = text('p', 'dk-mfoot', t('desktop.ddnsFoot'));
  root.append(head, list, reload, foot); body.appendChild(root);
  let seq = 0;

  async function load() {
    const my = ++seq;
    reload.disabled = true;
    try {
      const s = await api('/api/modules/ddns/status');
      if (my !== seq) return;
      head.replaceChildren();
      const top = el('div', 'dk-mtop');
      top.append(text('span', 'dk-mstate big ' + ddnsStateClass(s.state), ddnsStateLabel(s.state)), text('span', 'dk-mnote', t('desktop.checkedAgo', { when: ago(s.checkedAt) })));
      head.append(top, text('p', '', ddnsHeadline(s)));
      head.append(text('p', 'dk-mnote', s.publicIp ? (s.source ? t('desktop.publicIpSource', { ip: s.publicIp, source: s.source }) : t('desktop.publicIp', { ip: s.publicIp })) : t('desktop.publicIpUnknown')));
      if (s.message && s.state !== 'unknown') head.append(text('p', 'dk-mnote', s.message));
      list.replaceChildren();
      if (!s.hosts.length) list.appendChild(text('div', 'dk-fempty', t('desktop.noNames')));
      for (const h of s.hosts) {
        const row = el('div', 'dk-row dk-mrow');
        const main = el('span', 'dk-tt');
        main.append(text('b', '', h.host), text('i', '', h.addresses && h.addresses.length ? t('desktop.resolves', { list: h.addresses.join(', ') }) : t('desktop.noResolve')));
        row.append(main, text('span', 'dk-mstate ' + ddnsStateClass(h.state), ddnsStateLabel(h.state)));
        list.appendChild(row);
      }
    } catch (e) {
      if (my !== seq) return;
      head.replaceChildren(text('p', '', serverMsg(e.message) || t('desktop.notLoaded'))); list.replaceChildren();
    } finally { if (my === seq) reload.disabled = false; }
  }
  reload.addEventListener('click', load);
  load();
  return { reload: load };
}
