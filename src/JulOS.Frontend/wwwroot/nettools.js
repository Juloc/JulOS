/* Netzwerkwerkzeuge (Ping, DNS-Abfrage, Port-Test): ein schlichter Dialog #netDlg (index.html), gemeinsam fuer Handy und Desktop.
 * Die Pruefungen laufen auf dem Server, nicht auf diesem Geraet, und nur gegen Ziele der gespeicherten Verbindungen (der Server prueft das;
 * hier gibt es kein freies Eingabefeld fuer Adressen). Die Textausgabe steht in reinen Funktionen (getestet in tests/frontend/nettools.test.mjs). */

import { t } from './i18n.js';
import { showDialog } from './desktop-window.js';

const $ = (s) => document.querySelector(s);

/** "22, 3389 443" -> { ports: [22, 3389, 443] } oder { error }. Hoechstens 10, jeder 1 bis 65535, ohne Doppelte. */
export function parsePorts(text) {
  const parts = String(text || '').split(/[\s,;]+/).filter(Boolean);
  if (!parts.length) return { error: t('nettools.needPort') };
  const ports = [];
  for (const p of parts) {
    if (!/^\d{1,5}$/.test(p) || Number(p) < 1 || Number(p) > 65535) return { error: t('nettools.badPort', { p }) };
    if (!ports.includes(Number(p))) ports.push(Number(p));
  }
  if (ports.length > 10) return { error: t('nettools.tooManyPorts') };
  return { ports };
}

/** Vorschlag fuer das Portfeld: die Ports der gespeicherten Verbindungen zu diesem Ziel, sonst SSH und RDP. */
export function defaultPorts(connections, host) {
  const own = (connections || []).filter((c) => String(c.host).toLowerCase() === String(host).toLowerCase()).map((c) => c.port);
  const list = [...new Set(own.length ? own : [22, 3389])].slice(0, 10);
  return list.join(', ');
}

const REPLY = { ok: (r) => t('nettools.replyOk', { ms: r.ms }), timeout: () => t('nettools.replyTimeout'), error: () => t('nettools.replyError'), unavailable: () => t('nettools.replyUnavailable') };

export function describePing(r) {
  const lines = [`PING ${r.host}${r.address && r.address !== r.host ? ` (${r.address})` : ''}`];
  for (const x of r.replies || []) lines.push(`${x.seq}: ${(REPLY[x.status] || REPLY.error)(x)}`);
  if (r.sent) lines.push(t('nettools.pingStats', { sent: r.sent, received: r.received }) + (r.received ? t('nettools.pingTimes', { min: r.minMs, avg: r.avgMs, max: r.maxMs }) : ''));
  if (r.note) lines.push(r.note);
  return lines.join('\n');
}

export function describeDns(r) {
  const head = r.addresses && r.addresses.length ? `${r.host} → ${r.addresses.join(', ')}  (${r.ms} ms)` : t('nettools.noAddress', { host: r.host });
  return r.note ? `${head}\n${r.note}` : head;
}

const STATE = { open: () => t('nettools.stateOpen'), closed: () => t('nettools.stateClosed'), filtered: () => t('nettools.stateFiltered'), error: () => t('nettools.stateError') };
export function describePorts(r) {
  return [`PORTS ${r.host}`, ...(r.results || []).map((x) => `${x.port}  ${STATE[x.state] ? STATE[x.state]() : x.state}${x.ms !== undefined && x.ms !== null && x.state === 'open' ? `  (${x.ms} ms)` : ''}`)].join('\n');
}

/** Gibt die Funktion zurueck, die den Dialog oeffnet. connections() liefert die gespeicherten Verbindungen (fuer den Port-Vorschlag). */
export function initNetTools({ api, setMsg, connections }) {
  const dlg = $('#netDlg');
  const out = $('#netOut'), msg = $('#netMsg'), sel = $('#netTarget');
  const buttons = ['#netPing', '#netDns', '#netPortGo'].map((s) => $(s));
  const busy = (on) => { for (const b of buttons) b.disabled = on; };

  dlg.addEventListener('close', () => { out.textContent = ''; setMsg(msg, ''); });
  dlg.addEventListener('click', (e) => { if (e.target.closest('[data-act=close]')) dlg.close(); });
  sel.addEventListener('change', () => { $('#netPorts').value = defaultPorts(connections(), sel.value); });

  async function run(path, body, describe) {
    if (!sel.value) return setMsg(msg, t('nettools.noTarget'));
    busy(true); setMsg(msg, ''); out.textContent = t('nettools.running');
    try { out.textContent = describe(await api(path, { method: 'POST', body: { host: sel.value, ...body } })); }
    catch (e) { out.textContent = ''; setMsg(msg, e.message); }
    finally { busy(false); }
  }
  $('#netPing').addEventListener('click', () => run('/api/tools/ping', { count: 4 }, describePing));
  $('#netDns').addEventListener('click', () => run('/api/tools/dns', {}, describeDns));
  $('#netPortGo').addEventListener('click', () => {
    const p = parsePorts($('#netPorts').value);
    if (p.error) return setMsg(msg, p.error);
    run('/api/tools/ports', { ports: p.ports }, describePorts);
  });

  return async () => {
    out.textContent = ''; setMsg(msg, '');
    sel.replaceChildren();
    showDialog(dlg, { system: true, width: 480 });
    try {
      const { targets } = await api('/api/tools/targets');
      for (const tg of targets) { const o = document.createElement('option'); o.value = o.textContent = tg; sel.appendChild(o); }
      if (!targets.length) setMsg(msg, t('nettools.noTargets'));
      $('#netPorts').value = defaultPorts(connections(), sel.value);
    } catch (e) { setMsg(msg, e.message); }
    busy(!sel.value);
  };
}
