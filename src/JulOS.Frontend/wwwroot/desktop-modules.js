/* =====================================================================
 * Fenster Caddy und DDNS im Desktop (nur lesen). Wird von desktop.js erst beim ersten Oeffnen geladen.
 * Caddy: Liste Domain -> Ziel aus der Konfiguration, "Erreichbarkeit pruefen" (TCP-Test nur zu den gelisteten Zielen).
 * DDNS: aktuelle oeffentliche Adresse, Zustand je Name, Zeit der letzten Pruefung. Nichts hier aendert etwas am Server oder an Caddy.
 * deps: { el, btn, ic, api }
 * ===================================================================== */
import { routeParts, sourceLabel, reachMap, reachText, reachClass, ddnsStateLabel, ddnsStateClass, ddnsHeadline, ago, ddnsOutcomeText, historyRow, ddnsControlLine, certClass, certStateText, certDetail, certSummary, caddyHealthText, caddyHealthClass, logClass } from './modules.js';
import { t, tp, fmtDay, fmtTime, fmtDate } from './i18n.js';
import { serverMsg } from './i18n-errors.js';
import { mountWidgets } from './widgets.js';

const text = (tag, cls, content) => { const e = document.createElement(tag); if (cls) e.className = cls; e.textContent = content; return e; };

export function mountCaddy(body, { el, btn, ic, api }) {
  body.innerHTML = '';
  const root = el('div', 'dk-mod');
  const status = el('div', 'dk-mtool');
  const health = text('span', 'dk-mstate', t('desktop.loadingDots'));
  const note = text('span', 'dk-mnote', '');
  const reload = btn('dk-btn', ic('refresh') + t('common.reload'));
  status.append(health, note, reload);
  const tabs = el('div', 'dk-mtabs'); tabs.setAttribute('role', 'tablist');
  const TABS = [['routes', t('desktop.tabRoutes')], ['certs', t('desktop.tabCerts')], ['config', t('desktop.tabConfig')], ['log', t('desktop.tabLog')]];
  const tabBtns = TABS.map(([id, label]) => { const b = btn('dk-btn dk-mtab', label); b.setAttribute('role', 'tab'); b.dataset.tab = id; tabs.appendChild(b); return b; });
  const pane = el('div', 'dk-mlist'); pane.setAttribute('aria-live', 'polite');
  const foot = text('p', 'dk-mfoot', t('desktop.caddyFoot'));
  root.append(status, tabs, pane, foot); body.appendChild(root);

  let tab = 'routes', seq = 0;
  const empty = (msg) => pane.replaceChildren(text('div', 'dk-fempty', msg));
  const fail = (e, my) => { if (my === seq) empty(serverMsg(e.message) || t('desktop.notLoaded')); };
  const row = (title, sub, chipText, chipCls) => {
    const r = el('div', 'dk-row dk-mrow'); const main = el('span', 'dk-tt');
    main.append(text('b', '', title), text('i', '', sub)); r.appendChild(main);
    if (chipText) r.appendChild(text('span', 'dk-mstate ' + chipCls, chipText));
    return r;
  };

  async function loadHealth() {
    try {
      const h = await api('/api/modules/caddy/health');
      health.className = 'dk-mstate big ' + caddyHealthClass(h); health.textContent = caddyHealthText(h);
      note.textContent = h.warning || (h.upstreams && h.upstreams.length ? tp('desktop.caddyUpstreams', h.upstreams.length) : '');
    } catch (e) { health.className = 'dk-mstate big'; health.textContent = serverMsg(e.message) || t('desktop.notLoaded'); }
  }

  async function showRoutes(my) {
    const [l, h] = await Promise.all([api('/api/modules/caddy/routes'), api('/api/modules/caddy/health').catch(() => null)]);
    if (my !== seq) return;
    const stats = new Map(((h && h.upstreams) || []).map((u) => [u.address, u]));
    pane.replaceChildren();
    if (!l.routes.length) pane.appendChild(text('div', 'dk-fempty', t('desktop.noEntries')));
    for (const r of l.routes) {
      const p = routeParts(r);
      const row2 = row(p.domain + (p.path ? '  ' + p.path : ''), p.kind + ' → ' + p.target, '', '');
      if (r.kind === 'proxy') {
        const s = (r.targets || []).map((x) => stats.get(x)).find((x) => x && x.fails > 0);
        if (s) row2.appendChild(text('span', 'dk-mstate bad', tp('desktop.upstreamFails', s.fails)));
        const states = (r.targets || []).map((x) => reach.get(x)).filter(Boolean);
        if (states.length) { const w = states.find((x) => x.state === 'down') || states.find((x) => x.state === 'skipped') || states[0]; row2.appendChild(text('span', 'dk-mstate ' + reachClass(w), reachText(w))); }
      }
      pane.appendChild(row2);
    }
    const actions = el('div', 'dk-mtool'); const check = btn('dk-btn', ic('activity') + t('desktop.checkReach'));
    check.addEventListener('click', async () => {
      check.disabled = true;
      try { reach = reachMap(await api('/api/modules/caddy/reachability')); await showRoutes(++seq); } catch (e) { note.textContent = serverMsg(e.message) || t('desktop.checkFailed'); } finally { check.disabled = false; }
    });
    actions.append(check, text('span', 'dk-mnote', t('desktop.caddyNote', { count: tp('desktop.entries', l.routes.length), source: sourceLabel(l.source) }) + (l.warnings && l.warnings.length ? ' · ' + l.warnings.join(' ') : '')));
    pane.prepend(actions);
  }

  async function showCerts(my) {
    const c = await api('/api/modules/caddy/certificates');
    if (my !== seq) return;
    pane.replaceChildren();
    const sum = certSummary(c.certificates);
    if (sum.warn || sum.bad) pane.appendChild(text('div', 'dk-mnote', tp('desktop.certsAttention', sum.warn + sum.bad)));
    if (!c.certificates.length) pane.appendChild(text('div', 'dk-fempty', t('desktop.noCerts')));
    for (const x of c.certificates) pane.appendChild(row(x.domain, certDetail(x, (d) => fmtDay(d)), certStateText(x), certClass(x)));
    if (c.ca) pane.appendChild(row(t('desktop.internalCa', { name: c.ca.name }), c.ca.notAfter ? tp('modules.certDaysLeft', c.ca.daysLeft, { date: fmtDay(c.ca.notAfter) }) : c.ca.intermediate || '', '', ''));
    pane.appendChild(text('div', 'dk-mnote', t('desktop.certsNote', { when: ago(c.checkedAt) })));
  }

  async function showConfig(my) {
    const c = await api('/api/modules/caddy/config');
    if (my !== seq) return;
    pane.replaceChildren();
    pane.appendChild(text('div', 'dk-mnote', t('desktop.configNote', { source: sourceLabel(c.source) }) + (c.warning ? ' · ' + c.warning : '')));
    if (c.text) pane.appendChild(text('pre', 'dk-mpre', c.text)); else pane.appendChild(text('div', 'dk-fempty', t('desktop.noEntries')));
  }

  async function showLog(my) {
    const l = await api('/api/modules/caddy/log');
    if (my !== seq) return;
    pane.replaceChildren();
    if (!l.available) { pane.appendChild(text('div', 'dk-fempty', l.warning ? t('desktop.logUnreadable') : t('desktop.logOff'))); return; }
    pane.appendChild(text('div', 'dk-mnote', t('desktop.logNote', { count: l.total })));
    for (const h of l.hosts) pane.appendChild(row(h.host, t('desktop.logHost', { n: h.requests, e4: h.errors4xx, e5: h.errors5xx }), '', ''));
    for (const e of l.entries) pane.appendChild(row(`${e.method} ${e.host}${e.path}`, `${fmtTime(e.time)} · ${e.ms} ms`, String(e.status), logClass(e.status)));
  }

  let reach = new Map();
  async function load() {
    const my = ++seq;
    for (const b of tabBtns) b.setAttribute('aria-selected', String(b.dataset.tab === tab));
    empty(t('desktop.loadingDots'));
    loadHealth();
    try { await { routes: showRoutes, certs: showCerts, config: showConfig, log: showLog }[tab](my); } catch (e) { fail(e, my); }
  }
  tabs.addEventListener('click', (e) => { const b = e.target.closest('[data-tab]'); if (b && b.dataset.tab !== tab) { tab = b.dataset.tab; load(); } });
  reload.addEventListener('click', load);
  load();
  return { reload: load };
}

export function mountDdns(body, { el, btn, ic, api }) {
  body.innerHTML = '';
  const root = el('div', 'dk-mod');
  const head = el('div', 'dk-card dk-mhead');
  const list = el('div', 'dk-mlist'); list.setAttribute('aria-live', 'polite');
  const reload = btn('dk-btn', ic('refresh') + t('desktop.checkNow'));
  const ctl = el('div', 'dk-card dk-mhead'); ctl.hidden = true;
  const ctlLine = text('p', 'dk-mnote', ''), ctlMsg = text('p', '', '');
  const update = btn('dk-btn', ic('cloud') + t('desktop.ddnsUpdate'));
  ctl.append(ctlLine, update, ctlMsg);
  const histTitle = text('h3', '', t('desktop.ddnsHistory')); histTitle.hidden = true;
  const hist = el('div', 'dk-mlist'); hist.hidden = true;
  const foot = text('p', 'dk-mfoot', t('desktop.ddnsFoot'));
  root.append(head, list, reload, ctl, histTitle, hist, foot); body.appendChild(root);
  let seq = 0;

  async function loadControl() {
    try {
      const info = await api('/api/modules/ddns/control');
      ctl.hidden = !info.canUpdate; ctlLine.textContent = ddnsControlLine(info);
      const rows = (await api('/api/modules/ddns/history')).entries || [];
      histTitle.hidden = hist.hidden = !(info.canUpdate || rows.length);
      hist.replaceChildren();
      if (!rows.length) hist.appendChild(text('div', 'dk-fempty', t('desktop.ddnsNoHistory')));
      for (const e of rows) {
        const h = historyRow(e, (d) => fmtDate(d));
        const row = el('div', 'dk-row dk-mrow'); const main = el('span', 'dk-tt');
        main.append(text('b', '', h.title + '  ' + h.sub), text('i', '', h.when + (h.meta ? ' · ' + h.meta : '')));
        row.append(main, text('span', 'dk-mstate ' + h.cls, h.state)); hist.appendChild(row);
      }
    } catch { ctl.hidden = true; }
  }

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
      loadControl();
    } catch (e) {
      if (my !== seq) return;
      head.replaceChildren(text('p', '', serverMsg(e.message) || t('desktop.notLoaded'))); list.replaceChildren();
    } finally { if (my === seq) reload.disabled = false; }
  }
  update.addEventListener('click', async () => {
    update.disabled = true; ctlMsg.textContent = '';
    try { const r = await api('/api/modules/ddns/update', { method: 'POST', body: {} }); ctlMsg.textContent = ddnsOutcomeText(r); ctlMsg.className = ''; await load(); }
    catch (e) { ctlMsg.textContent = serverMsg(e.message) || t('desktop.notLoaded'); }
    finally { update.disabled = false; }
  });
  reload.addEventListener('click', load);
  load();
  return { reload: load };
}

/** Dienste-Widgets: das Raster aus widgets.js im Fenster (Schreiben nur, wenn der Server "control" meldet). */
export function mountServices(body, { el, api }) {
  body.innerHTML = '';
  const root = el('div', 'dk-mod dk-wd');
  body.appendChild(root);
  const w = mountWidgets(root, { api, mobile: false });
  return { reload: w.reload };
}

export function mountModule(id, body, deps) {
  return id === 'caddy' ? mountCaddy(body, deps) : id === 'widgets' ? mountServices(body, deps) : mountDdns(body, deps);
}
