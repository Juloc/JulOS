/* =====================================================================
 * Pop-out, Seite des Hauptfensters: eine Stream-Sitzung in ein eigenes Browserfenster loesen (window.open) und wieder anheften.
 * Beschreibung: docs/SESSIONS.md, Abschnitt "Eigenes Fenster (Pop-out)".
 *
 * Es gibt keinen zweiten RDP-Login: das Fenster nimmt dieselbe Sitzung des Servers mit dem Sitzungs-Token auf (resume.js, ScreenRelay). Das
 * Hauptfenster kappt dazu nur seine Leitung (der Server haelt die Sitzung ohne Browser) und uebergibt das Token ueber einen Eintrag in
 * window.__julosPopouts, den das Fenster beim Laden von window.opener liest (gleiche Herkunft; das Token steht nie in einer Adresse).
 * ===================================================================== */

import { t } from './i18n.js';

export const REGISTRY = '__julosPopouts';

/** Nur auf grossen Bildschirmen mit Maus (wie die Desktop-Oberflaeche): das Fenster braucht Platz und Tastatur. */
export const popoutAllowed = ({ width, fine }) => width >= 1000 && !!fine;

export const popoutUrl = (connId, sid) => `/popout.html?c=${encodeURIComponent(connId)}&s=${encodeURIComponent(sid)}`;

/** Grosse und Lage des neuen Fensters: etwas kleiner als der Bildschirm, nicht kleiner als 640 x 400. */
export function popoutFeatures(screenInfo = {}) {
  const sw = screenInfo.availWidth || 1280, sh = screenInfo.availHeight || 800;
  const w = Math.max(640, Math.min(1280, Math.round(sw * 0.7))), h = Math.max(400, Math.min(800, Math.round(sh * 0.7)));
  return `popup=yes,width=${w},height=${h},left=${Math.round((sw - w) / 2)},top=${Math.round((sh - h) / 2)}`;
}

/** Ist so ein Wechsel moeglich? Die Sitzung muss stehen, ein Bild gezeichnet haben und ein Token des Servers haben. */
export function canPopOut(session) {
  const c = session.client;
  return !!(c && session.connected && c.drawn && session.resume && session.resume.token && !session.popped);
}

/**
 * Oeffnet das Fenster. Gibt { ok: true, win, stop } oder { ok: false, reason } zurueck. Muss direkt aus einem Klick kommen (sonst blockiert
 * der Browser das Fenster). extra: weitere Angaben fuer das Fenster ({ perf: Statusleiste an }). back() wird gerufen, wenn das Fenster zu ist oder die Sitzung zurueck will (einmal).
 */
export function openPopout(session, back, extra = {}, host = globalThis) {
  if (!canPopOut(session)) return { ok: false, reason: t('popout.errNotReady') };
  const win = host.open(popoutUrl(session.conn.id, session.id), 'julos-popout-' + session.id, popoutFeatures(host.screen));
  if (!win) return { ok: false, reason: t('popout.errBlocked') };
  let done = false, timer = 0;
  const finish = () => { if (done) return; done = true; clearInterval(timer); delete host[REGISTRY]?.[session.id]; back(); };
  host[REGISTRY] = host[REGISTRY] || {};
  host[REGISTRY][session.id] = {
    token: session.resume.token, keep: Math.round(session.resume.keepMs / 1000), k: session.k, title: session.title, name: session.conn.name,
    quality: session.quality, ...extra,
    back: finish,                       // das Fenster ist zu oder will zurueck
    ended: null,                        // vom Fenster gesetzt: die Sitzung endet (Hauptfenster schliesst sie), das Fenster beendet den Server
  };
  // Ein Fenster, das der Browser beendet (nicht durch uns geschlossen), meldet sich nicht immer ab: nachsehen
  timer = setInterval(() => { if (win.closed) finish(); }, 600);
  return {
    ok: true, win,
    /**
     * Zurueck ins Hauptfenster: das Fenster schliessen, ohne dass es back() noch einmal ausloest. Der Eintrag bleibt noch kurz stehen: das Fenster
     * liest ihn beim Schliessen, um die Leitung nur zu kappen (und die Sitzung nicht zu beenden).
     */
    stop: () => { done = true; clearInterval(timer); try { win.close(); } catch { /* egal */ } setTimeout(() => { delete host[REGISTRY]?.[session.id]; }, 2000).unref?.(); },
    /** Die Sitzung endet (Hauptfenster schliesst sie): dem Fenster sagen, dass es den Server beenden und zugehen soll. */
    end: () => { const e = host[REGISTRY]?.[session.id]; done = true; clearInterval(timer); try { e && e.ended && e.ended(); } catch { /* egal */ } delete host[REGISTRY]?.[session.id]; try { win.close(); } catch { /* egal */ } },
  };
}
