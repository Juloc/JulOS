/* =====================================================================
 * Workspace pro Geraet, Seite des Browsers (docs/MULTI-SCREEN.md, "Workspace on the server"): JulOS merkt sich, welche Fenster und Verbindungen auf diesem Geraet
 * offen sind und wo sie liegen, und stellt sie beim naechsten Oeffnen wieder her.
 *
 * Oben steht die reine Logik (kein DOM, getestet in tests/frontend/desktop-workspace.test.mjs): Geraetekennung und -name, das Dokument aus der Fensterliste,
 * der Plan fuers Wiederherstellen, Fenster in den sichtbaren Bereich schieben, die Entscheidung bei einem Konflikt (409), die Wartezeit nach Fehlern.
 * Darunter createWorkspace: Laden, entprelltes Speichern, Konflikte, Schliessen der Seite, mehrere Browserfenster. Alles, was Fenster betrifft, liefert desktop.js
 * ueber h; hier steht nichts ueber das Aussehen. Ins Dokument kommen nur Kennungen, Geometrie und Zustand, nie ein Passwort oder ein Ziel.
 * ===================================================================== */

export const SYNC_MS = 800;              // so lange muss Ruhe sein, bevor gespeichert wird
export const MAX_WAIT_MS = 6000;         // bei ununterbrochenem Ziehen spaetestens dann
const MAX_WINDOWS = 32;
const ID = /^[A-Za-z0-9_:.-]{1,64}$/, TOKEN = /^[a-z0-9_-]{1,30}$/, DEVICE = /^[A-Za-z0-9_-]{8,64}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ZONES = ['left', 'right', 'tl', 'tr', 'bl', 'br', 'max'];
const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-';   // 64 Zeichen: ein Byte & 63 ist ohne Verzerrung

/* ---------- Geraet ---------- */
/** Zufaellige Geraetekennung (22 Zeichen, 132 Bit); random fuellt ein Uint8Array (crypto.getRandomValues) */
export function newDeviceId(random) {
  const b = new Uint8Array(22); random(b);
  return [...b].map((x) => ALPHABET[x & 63]).join('');
}
export const isDeviceId = (s) => typeof s === 'string' && DEVICE.test(s);

/** Anzeigename des Geraets aus dem User-Agent, ohne Sprache ("Chrome · Linux"), hoechstens 40 Zeichen */
export function deviceName(ua = '', platform = '') {
  const browser = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : '';
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'macOS' : /CrOS/.test(ua) ? 'ChromeOS' : /Linux/.test(ua) ? 'Linux' : '';
  return ([browser, os].filter(Boolean).join(' · ') || String(platform || '').trim() || 'Browser').slice(0, 40);
}

/* ---------- Dokument ---------- */
const int = (n, lo, hi) => Math.min(hi, Math.max(lo, Math.round(Number.isFinite(n) ? n : 0)));
const rectOf = (r) => ({ x: int(r.x, -100000, 100000), y: int(r.y, -100000, 100000), w: int(r.w, 1, 100000), h: int(r.h, 1, 100000) });

/**
 * Das Dokument aus der Liste der offenen Fenster (in der Reihenfolge der Pillen). Ein Eintrag der Liste:
 * { id, kind: 'app' | 'connection', app | connectionId, x, y, w, h, z (Stapelwert, nur die Reihenfolge zaehlt), minimized, maximized, snap (Zone oder null), restore ({x,y,w,h} oder null), active }.
 * Eintraege, die der Server ablehnen wuerde (Kennung, App, keine UUID, ueber 32), fallen hier schon weg; Verbindungen ohne UUID sind Quick Connect und gehoeren nicht ins Dokument.
 * view: { w, h } der Anzeige; collapsed: der Dock ist eingeklappt.
 */
export function buildDoc(list, view, collapsed = false) {
  const seen = new Set(), kept = [];
  for (const x of list) {
    if (!x || kept.length >= MAX_WINDOWS || !ID.test(x.id || '') || seen.has(x.id)) continue;
    if (x.kind === 'app' ? !TOKEN.test(x.app || '') : !(x.kind === 'connection' && UUID.test(x.connectionId || ''))) continue;
    seen.add(x.id); kept.push(x);
  }
  const rank = new Map([...kept].sort((a, b) => (a.z || 0) - (b.z || 0)).map((x, i) => [x.id, i + 1]));    // Stapelfolge als 1..n: kleine, stabile Zahlen
  const windows = kept.map((x) => {
    const zone = x.snap && (x.snap === 'max' || ZONES.includes(x.snap)) ? x.snap : null;
    const state = x.minimized ? 'min' : x.maximized ? 'max' : zone ? 'snapped' : 'normal';
    const w = { id: x.id, kind: x.kind };
    if (x.kind === 'app') w.app = x.app; else w.connectionId = x.connectionId.toLowerCase();
    Object.assign(w, rectOf(x), { z: rank.get(x.id), state });
    if (zone && (state === 'snapped' || state === 'min')) w.snap = zone;          // minimiert merkt sich die Zone darunter (auch 'max'), damit es so zurueckkommt
    if (state !== 'normal' && x.restore) w.restore = rectOf(x.restore);
    return w;
  });
  const doc = { screens: [{ id: 's1', x: 0, y: 0, w: int(view.w, 1, 100000), h: int(view.h, 1, 100000) }], windows };
  const focus = kept.find((x) => x.active && !x.minimized);
  if (focus) doc.focus = focus.id;
  doc.dock = { collapsed: !!collapsed, side: 'bottom', order: kept.map((x) => x.id) };
  return doc;
}

/**
 * Was beim Start wiederhergestellt wird: erst die Apps, dann die Verbindungen, je in der Reihenfolge des Docks. Der Server hat das Dokument schon geprueft; hier fliegt raus,
 * was es nicht mehr gibt: eine App, die diese Version nicht kennt (appKey liefert null) oder die schon ein anderer Eintrag meint (appKey liefert die Kennung des Fensters),
 * und eine geloeschte Verbindung (connections: Menge der Kennungen). Der Rest bleibt, wie er ist; ins Sichtbare schiebt erst fitIntoView.
 * @returns {{ apps: object[], conns: object[], order: string[], focus: string|null }}
 */
export function planRestore(doc, { appKey, connections }) {
  const empty = { apps: [], conns: [], order: [], focus: null };
  if (!doc || !Array.isArray(doc.windows)) return empty;
  const pos = new Map(((doc.dock && doc.dock.order) || []).map((id, i) => [id, i]));
  const sorted = doc.windows.filter((w) => w && ID.test(w.id || '')).map((w, i) => ({ w, i }))
    .sort((a, b) => (pos.has(a.w.id) ? pos.get(a.w.id) : 1e6 + a.i) - (pos.has(b.w.id) ? pos.get(b.w.id) : 1e6 + b.i)).map((x) => x.w);
  const taken = new Set(), apps = [], conns = [];
  for (const w of sorted) {
    if (w.kind === 'app') {
      const key = appKey(w.app);
      if (!key || taken.has(key)) continue;
      taken.add(key); apps.push({ ...w, key });
    } else if (w.kind === 'connection' && typeof w.connectionId === 'string' && connections.has(w.connectionId.toLowerCase())) conns.push({ ...w, connectionId: w.connectionId.toLowerCase() });
  }
  const all = [...apps, ...conns];
  const focus = all.some((w) => w.id === doc.focus) ? doc.focus : null;
  return { apps, conns, order: all.map((w) => w.id), focus };
}

/** Ein Rechteck so, dass das Fenster ganz sichtbar liegt (nur wenn die Anzeige zu klein ist, ragt etwas heraus); barH: die Leiste oben */
export function fitIntoView(r, view, barH, minW, minH) {
  const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
  const w = clamp(r.w, minW, view.w), h = clamp(r.h, minH, view.h - barH);
  return { w, h, x: clamp(r.x, 0, Math.max(0, view.w - w)), y: clamp(r.y, barH, Math.max(barH, view.h - h)) };
}

/** Ob der Zustand einer Zone des Fenstermanagers entspricht (sonst steht das Fenster einfach frei) */
export const isZone = (z) => ZONES.includes(z);

/* ---------- Konflikt und Fehler ---------- */
/**
 * Der Server hat beim Speichern 409 geantwortet: jemand anderes (ein zweites Browserfenster dieses Geraets) hat inzwischen geschrieben. Es gilt die neuere Aenderung:
 * 'overwrite' = unser Stand ist neuer oder gleich alt, wir senden ihn mit der Version des Servers noch einmal; 'adopt' = der Server wurde deutlich spaeter beschrieben als
 * unsere Aenderung entstand, er behaelt seinen Stand und wir merken uns nur seine Version (die naechste eigene Aenderung ueberschreibt wieder).
 * pendingAgeMs: wie lange unsere ungesicherte Aenderung alt ist; serverAgeMs: wie lange der Stand des Servers alt ist (beides Zeitspannen, damit Uhren nicht stimmen muessen;
 * die Sekundenrundung der HTTP-Zeit gleicht die Toleranz aus). hasDoc false = das Geraet wurde auf dem Server vergessen: neu anlegen.
 */
export function resolveConflict({ pendingAgeMs, serverAgeMs, version, hasDoc }) {
  if (!hasDoc) return { action: 'overwrite', version: 0 };
  if (Number.isFinite(serverAgeMs) && serverAgeMs + 1500 < pendingAgeMs) return { action: 'adopt', version };
  return { action: 'overwrite', version };
}

/** Wartezeit nach dem n-ten Fehler in Folge: 2 s, 4 s, 8 s ... bis 60 s; ein Retry-After des Servers (Sekunden) gilt mindestens */
export const retryDelay = (failures, retryAfterSec = 0) => Math.max(Math.min(60000, 2000 * 2 ** Math.max(0, failures - 1)), (retryAfterSec > 0 ? Math.min(retryAfterSec, 300) : 0) * 1000);

/* =====================================================================
 * Controller
 * ===================================================================== */
const MAX_CONFLICTS = 3;                  // so viele 409 in Folge, dann Pause (kein Dauerfeuer zwischen zwei Browserfenstern)

/**
 * h: root (Element der Fenster), ls ({get,set} fuer Einstellungen dieses Geraets), storage (localStorage), channel (BroadcastChannel oder null), now,
 *    collect() -> { list, view, collapsed } fuer buildDoc, appKey(token), connectionIds() -> Set (Kleinbuchstaben), whenReady() -> Promise (Module sind geladen),
 *    openApp(token) -> Promise<window|null>, openConnection(connectionId) -> window (ein Platzhalter "Wieder verbinden"), reconnect(window), canAutoConnect(connectionId),
 *    place(window, entry), finish(entries, order, focus)
 * Rueckgabe: start, stop, changed, forget, option, setOption, hint.
 */
export function createWorkspace(h) {
  const store = h.storage, now = h.now || Date.now;
  let deviceId = null, version = 0, lastJson = '', active = false, signedIn = false, forgotten = false;
  let timer = null, dirtyAt = 0, inflight = false, failures = 0, conflicts = 0, blockedUntil = 0, channel = null, observer = null, starting = false;

  const remembering = () => h.ls.get('ws.remember', true) !== false;
  const option = (k) => (k === 'remember' ? (remembering() ? 'on' : 'off') : h.ls.get('ws.reconnect', 'ask') === 'auto' ? 'auto' : 'ask');
  const path = () => '/api/workspaces/' + deviceId;
  const name = () => deviceName(globalThis.navigator ? navigator.userAgent : '', globalThis.navigator ? navigator.platform : '');

  function getDeviceId() {
    let id = null;
    try { id = store.getItem('pd.deviceId'); } catch { /* ohne Speicher: eine Kennung nur fuer diese Seite */ }
    if (!isDeviceId(id)) {
      id = newDeviceId((b) => globalThis.crypto.getRandomValues(b));
      try { store.setItem('pd.deviceId', id); } catch { /* egal */ }
    }
    return id;
  }

  async function call(method, body, keepalive = false) {
    const headers = {};
    if (method !== 'GET') headers['X-JulOS'] = '1';
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const r = await fetch(path(), { method, headers, body: body === undefined ? undefined : JSON.stringify(body), keepalive });
    let data = null;
    if (r.status !== 204) { try { data = await r.json(); } catch { /* kein JSON */ } }
    const date = Date.parse(r.headers.get('date') || '');
    return { status: r.status, data, serverNow: Number.isFinite(date) ? date : now() };
  }

  const build = () => { const c = h.collect(); return buildDoc(c.list, c.view, c.collapsed); };

  /* ---------- Speichern ---------- */
  /** Eine Aenderung ist passiert (oder koennte): nach SYNC_MS Ruhe speichern, bei Dauerbewegung spaetestens nach MAX_WAIT_MS, nie vor Ablauf einer Fehlerpause */
  function changed() {
    if (!active) return;
    const t0 = now();
    if (!dirtyAt) dirtyAt = t0;
    clearTimeout(timer);
    timer = setTimeout(fire, Math.max(blockedUntil - t0, t0 - dirtyAt >= MAX_WAIT_MS ? 0 : SYNC_MS));
  }
  function fire() { timer = null; if (inflight) return changed(); flush(); }

  /** Sendet den Stand, wenn er sich vom letzten gesicherten unterscheidet. keepalive: beim Schliessen der Seite (der Browser schickt es noch zu Ende) */
  async function flush(keepalive = false) {
    if (!active || inflight) return;
    const doc = build(), json = JSON.stringify(doc);
    if (json === lastJson) { dirtyAt = 0; return; }
    inflight = true;
    const age = now() - (dirtyAt || now());
    try {
      const r = await call('PUT', { version, name: name(), doc }, keepalive);
      if (r.status === 200 && r.data) {
        version = r.data.version; lastJson = json; failures = 0; conflicts = 0; dirtyAt = 0; blockedUntil = 0;
        if (channel) channel.postMessage({ t: 'saved', version });
      } else if (r.status === 409 && r.data) {
        const c = r.data, hasDoc = !!c.doc && c.version > 0;
        const res = resolveConflict({ pendingAgeMs: age, serverAgeMs: r.serverNow - Date.parse(c.updatedAt), version: c.version || 0, hasDoc });
        version = res.version;
        if (res.action === 'adopt') { lastJson = json; dirtyAt = 0; conflicts = 0; }
        else if (++conflicts > MAX_CONFLICTS) { conflicts = 0; blockedUntil = now() + retryDelay(++failures); changed(); }
        else changed();
      } else if (r.status === 401 || r.status === 403) active = false;       // keine Sitzung: die Anmeldung kommt von shell.js, hier nicht weiter versuchen
      else { blockedUntil = now() + retryDelay(++failures, r.status === 429 && r.data ? r.data.retryAfter : 0); changed(); }
    } catch {
      blockedUntil = now() + retryDelay(++failures); changed();
    } finally { inflight = false; }
  }

  /* ---------- Wiederherstellen ---------- */
  async function restore() {
    let got = null;
    try { got = await call('GET'); } catch { /* nichts zu holen: das Geraet beginnt neu */ }
    if (!got || got.status !== 200 || !got.data || !got.data.doc) return 0;
    version = got.data.version || 0;
    await h.whenReady();
    const view = h.collect().view;
    const plan = planRestore(got.data.doc, { appKey: h.appKey, connections: h.connectionIds() });
    const auto = option('reconnect') === 'auto', entries = [];
    for (const e of plan.apps) {
      try { const w = await h.openApp(e.app); if (w) { h.place(w, e, view); entries.push({ win: w, entry: e }); } } catch { /* diese App kommt nicht wieder, die anderen schon */ }
    }
    for (const e of plan.conns) {
      const w = h.openConnection(e.connectionId);
      if (!w) continue;
      h.place(w, e, view); entries.push({ win: w, entry: e });
      if (auto && h.canAutoConnect(e.connectionId)) h.reconnect(w);        // nur ohne Hindernis (gespeichertes Passwort); sonst bleibt der Platzhalter mit seinem Knopf
    }
    h.finish(entries, plan.order, plan.focus);
    return entries.length;
  }

  /* ---------- Ablauf ---------- */
  function listen() {
    if (observer || !h.root) return;
    // Aenderungen an Fenstern: ein Fenster kommt oder geht (Kinder der Wurzel), Ort, Groesse, Stapel und Zustand (style und class eines Fensters)
    observer = new MutationObserver((records) => {
      for (const r of records) if (r.type === 'childList' ? r.target === h.root : r.target.classList && r.target.classList.contains('dk-win')) { changed(); break; }
    });
    observer.observe(h.root, { childList: true, attributes: true, attributeFilter: ['style', 'class'], subtree: true });
    const closing = () => { if (active && (timer || dirtyAt)) flush(true); };
    addEventListener('pagehide', closing);
    document.addEventListener('visibilitychange', () => { if (document.hidden) closing(); });
  }
  function openChannel() {
    if (channel || !h.channel) return;
    channel = h.channel(deviceId);
    if (!channel) return;
    channel.onmessage = (e) => {
      const m = e.data || {};
      // Ein anderes Browserfenster dieses Geraets hat gespeichert: ohne eigene ungesicherte Aenderung dessen Version uebernehmen (kein unnoetiger 409); mit einer entscheidet der Server
      if (m.t === 'saved' && !timer && !dirtyAt && Number.isInteger(m.version) && m.version > version) version = m.version;
      else if (m.t === 'forgot') { active = false; forgotten = true; clearTimeout(timer); }
    };
  }

  /** Nach der Anmeldung (Desktop): stellt wieder her (withRestore) und beginnt zu speichern. Gibt die Zahl der wiederhergestellten Fenster zurueck (0 = nichts, der Desktop zeigt die Startansicht). */
  async function start(withRestore = true) {
    signedIn = true;
    if (starting || forgotten || !remembering()) return 0;
    starting = true;
    let n = 0;
    try {
      deviceId = deviceId || getDeviceId(); openChannel();
      if (withRestore) n = await restore();
    } catch { /* ein kaputter Stand darf den Desktop nicht verhindern */ }
    starting = false;
    if (!signedIn) return n;       // waehrend des Ladens abgemeldet
    lastJson = withRestore ? JSON.stringify(build()) : ''; active = true; failures = conflicts = 0; blockedUntil = 0; dirtyAt = 0;
    listen();
    if (!withRestore) changed();            // wieder eingeschaltet: der jetzige Stand wird gesichert
    return n;
  }
  /** Abgemeldet oder Fenster werden abgeraeumt: nicht speichern (sonst stuende nachher ein leerer Desktop auf dem Server) */
  function stop() { signedIn = false; active = false; clearTimeout(timer); timer = null; dirtyAt = 0; }

  /** Dieses Geraet vergessen: der Server loescht den Stand, die Kennung geht, und diese Seite speichert nicht mehr (nach dem Neuladen beginnt das Geraet neu) */
  async function forget() {
    if (!deviceId) deviceId = getDeviceId();
    const was = active; active = false; clearTimeout(timer); timer = null;
    try {
      const r = await call('DELETE');
      if (r.status !== 204) throw new Error(String(r.status));
    } catch { active = was; return false; }
    forgotten = true; version = 0; lastJson = '';
    if (channel) channel.postMessage({ t: 'forgot' });
    try { store.removeItem('pd.deviceId'); } catch { /* egal */ }
    return true;
  }

  /** Einstellung dieses Geraets: remember = on | off, reconnect = ask | auto. "Fenster merken" an: der jetzige Stand wird gesichert; aus: nichts mehr wird gespeichert oder geladen. */
  function setOption(key, value) {
    if (key === 'remember') {
      h.ls.set('ws.remember', value !== 'off');
      if (value === 'off') { active = false; clearTimeout(timer); timer = null; }
      else if (signedIn && !forgotten && !active) start(false);
    } else if (key === 'reconnect') h.ls.set('ws.reconnect', value === 'auto' ? 'auto' : 'ask');
  }

  return { start, stop, changed, forget, option, setOption };
}
