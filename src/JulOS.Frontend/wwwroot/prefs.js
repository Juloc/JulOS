/* =====================================================================
 * Einstellungen je Benutzer auf dem Server (docs/FOLDERS.md): die Tastenleiste ("keybar"), die Themenfarbe ("theme"), die Sprache ("lang") und das Aussehen ("appearance").
 * Die Seite behaelt ihre lokale Kopie (localStorage) und nutzt sie sofort; der Server macht sie auf jedem Geraet gleich.
 *  - Eine Aenderung wird lokal gespeichert, dann mit kurzer Verzoegerung zum Server geschickt (push). Klappt das nicht, bleibt sie als "offen" vermerkt
 *    und geht beim naechsten Abgleich hinaus.
 *  - Der Abgleich (sync) fragt den Server: ein dort gespeicherter Wert gilt und frischt die lokale Kopie auf; hat der Server noch keinen, geht der lokale hinaus
 *    (erster Aufruf); ist lokal noch etwas offen, geht dieses vor.
 *  - Ist der Server nicht erreichbar, bleibt alles, wie es lokal ist (Rueckfall).
 * Kein DOM, damit es sich mit node pruefen laesst.
 * ===================================================================== */
import { t } from './i18n.js';

const PENDING_KEY = 'pd.prefs.pending';
export const PREF_KEYS = ['keybar', 'theme', 'lang', 'appearance'];

/** Kleiner Aufruf der API (Aenderungen tragen die Sonderkopfzeile). Wirft bei einem Fehler. */
export async function jsonApi(path, { method = 'GET', body } = {}) {
  const headers = {};
  if (method !== 'GET') headers['X-JulOS'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const r = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!r.ok) throw Object.assign(new Error(t('term.errorStatus', { status: r.status })), { status: r.status });
  return r.status === 204 ? null : r.json();
}

/**
 * @param {{api?: Function, storage: Storage, local: () => {keybar?: object|null, theme?: object|null, lang?: object|null, appearance?: object|null}, delay?: number, schedule?: Function, cancel?: Function}} o
 * local: was dieses Geraet lokal hat (null: noch nie veraendert, also nichts hochzuladen)
 */
export function createPrefs({ api = jsonApi, storage, local, delay = 800, schedule = setTimeout, cancel = clearTimeout }) {
  const timers = new Map(), latest = new Map();
  const readPending = () => { try { const p = JSON.parse(storage.getItem(PENDING_KEY)); return p && typeof p === 'object' ? p : {}; } catch { return {}; } };
  const writePending = (p) => { try { if (Object.keys(p).length) storage.setItem(PENDING_KEY, JSON.stringify(p)); else storage.removeItem(PENDING_KEY); } catch { /* egal */ } };
  const mark = (key, state) => { const p = readPending(); if (state) p[key] = state; else delete p[key]; writePending(p); };

  async function send(key, value) {
    try {
      if (value === null) await api(`/api/settings/${key}`, { method: 'DELETE' });
      else await api(`/api/settings/${key}`, { method: 'PUT', body: { value } });
      if (latest.get(key) === value || !latest.has(key)) mark(key, null);
      return true;
    } catch { return false; }     // bleibt vermerkt, der naechste Abgleich versucht es erneut
  }

  const self = {
    /** Eine Aenderung (value) merken und bald zum Server schicken; null setzt den Wert auf den Standard zurueck. */
    push(key, value) {
      if (!PREF_KEYS.includes(key)) return;
      latest.set(key, value);
      mark(key, value === null ? 'reset' : 'set');
      if (timers.has(key)) cancel(timers.get(key));
      timers.set(key, schedule(() => { timers.delete(key); send(key, latest.get(key)); }, delay));
    },
    reset(key) { self.push(key, null); },

    /**
     * Gleicht mit dem Server ab. @returns {Promise<{keybar?: object, theme?: object, lang?: object}>} die Werte, die der Server hat und die diese Seite uebernehmen soll
     * (nichts bei Fehler, bei Offene-Aenderungen oder wenn der Server nichts hat).
     */
    async sync() {
      let server;
      try { server = (await api('/api/settings')).settings || {}; } catch { return {}; }
      const pending = readPending(), mine = local(), take = {};
      for (const key of PREF_KEYS) {
        if (pending[key] === 'reset') { await send(key, null); continue; }
        if (pending[key] === 'set' && mine[key]) { latest.set(key, mine[key]); await send(key, mine[key]); continue; }
        if (server[key]) take[key] = server[key];
        else if (mine[key]) { latest.set(key, mine[key]); await send(key, mine[key]); }          // erster Aufruf: der Server bekommt, was dieses Geraet hat
      }
      return take;
    },
  };
  return self;
}
