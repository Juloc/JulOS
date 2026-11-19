/* =====================================================================
 * Pop-out-Fenster: zeigt eine Stream-Sitzung des Hauptfensters in einem eigenen Browserfenster (popout-main.js oeffnet es).
 * Es loggt sich nicht noch einmal am Ziel an: das Token der laufenden Sitzung kommt aus dem Hauptfenster (window.opener), der Server haengt
 * den neuen WebSocket an dieselbe RDP-Sitzung (ScreenRelay, Sitzungserhalt). "Wieder anheften" oder Schliessen gibt die Sitzung dem
 * Hauptfenster zurueck; es nimmt sie mit demselben Token wieder auf. Beschreibung: docs/SESSIONS.md.
 *
 * Bewusst schlicht: Bild, Maus, Hardware-Tastatur (physische Tasten, popout-keys.js), Zwischenablage (Text), Ton, Statusleiste, Bildrate.
 * Touch, Datei-Uebertragung, Sondertastenleiste und Fensterliste der VM gibt es nur im Hauptfenster.
 * ===================================================================== */
import { StreamClient } from './stream.js';
import { Resume } from './resume.js';
import { StatusBar } from './statusbar.js';
import { MouseInput, mouseState } from './mouse.js';
import { scancodeOf, isPasteShortcut } from './popout-keys.js';
import { QUALITIES, normalizeQuality } from './perf.js';
import { REGISTRY } from './popout-main.js';
import { t, applyDom } from './i18n.js';
import { serverMsg } from './i18n-errors.js';

const $ = (id) => document.getElementById(id);
applyDom(document);                       // statische Texte der Seite in der gewaehlten Sprache
const store = {
  get(k, d) { try { const v = localStorage.getItem('pd.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('pd.' + k, JSON.stringify(v)); } catch { /* egal */ } },
};
const params = new URLSearchParams(location.search);
const sid = params.get('s'), connId = params.get('c') || '';

// Das Hauptfenster gibt Token und Einstellungen ueber window.opener; ohne es gibt es hier nichts zu zeigen
const handover = () => { try { return window.opener && !window.opener.closed ? window.opener[REGISTRY]?.[sid] || null : null; } catch { return null; } };
const h = handover();

const overlay = $('poOverlay'), msg = $('poMsg'), closeBtn = $('poClose');
const say = (text, { final = false } = {}) => { msg.textContent = text; overlay.hidden = !text; closeBtn.hidden = !final; };
closeBtn.addEventListener('click', () => window.close());

if (!h || !h.token) {
  $('poTitle').textContent = 'JulOS';
  for (const id of ['poQuality', 'poPerf', 'poBack']) $(id).hidden = true;
  $('poQuality').closest('label').hidden = true;
  say(t('popout.notOwned'), { final: true });
} else start(h);

function start(h) {
  document.title = h.name + ' – JulOS';
  $('poTitle').textContent = h.title || h.name;
  const stage = $('poStage'), displayHost = $('poDisplay'), touch = $('poTouch');
  const bar = new StatusBar($('poPerfBar'));
  const resume = new Resume(); resume.offered(h.token, h.keep); resume.confirm();
  let client = null, ended = false, quality = normalizeQuality(h.quality), k = h.k || 1, lastSent = { w: 0, h: 0 }, sizeTimer = 0, retryTimer = 0, pasteAt = 0;

  /* ---- Bedienelemente ---- */
  const select = $('poQuality');
  for (const q of QUALITIES) { const o = document.createElement('option'); o.value = q.id; o.textContent = q.label; o.title = q.hint; select.appendChild(o); }
  select.value = quality; bar.setQuality(quality);
  select.addEventListener('change', () => {
    quality = normalizeQuality(select.value); store.set('q.' + connId, quality);
    if (client) client.setQuality(quality);
    bar.setQuality(quality); touch.focus();
  });
  const perfBtn = $('poPerf');
  const showPerf = (on) => { perfBtn.setAttribute('aria-pressed', on ? 'true' : 'false'); bar.show(on); store.set('perf', on); };
  perfBtn.addEventListener('click', () => { showPerf(perfBtn.getAttribute('aria-pressed') !== 'true'); touch.focus(); });
  showPerf(!!h.perf);
  $('poBack').addEventListener('click', () => reattach());

  /* ---- Rueckgabe an das Hauptfenster ---- */
  // Die Leitung kappen wir nur (der Server haelt die Sitzung), das Hauptfenster nimmt sie mit dem Token wieder auf.
  function release() {
    ended = true; clearTimeout(sizeTimer); clearTimeout(retryTimer);
    if (client) { try { client.disconnect(true); } catch { /* egal */ } }
  }
  function reattach() {
    if (!handover()) { say(t('popout.mainGone')); setTimeout(() => say(''), 5000); return; }
    release();
    const live = handover();
    if (live) { try { live.back(); } catch { /* egal */ } }
    window.close();
    if (!window.closed) say(t('popout.backInMain'), { final: true });
  }
  // Das Hauptfenster schliesst die Sitzung (oder wird neu geladen): der Server soll sie nicht noch Minuten halten
  h.ended = () => { ended = true; clearTimeout(retryTimer); if (client) { try { client.disconnect(false); } catch { /* egal */ } } };
  window.addEventListener('pagehide', () => {
    if (ended) return;                   // schon abgegeben, beendet oder gescheitert: nichts mehr zu tun
    const live = handover();
    if (live === h) { release(); try { h.back(); } catch { /* egal */ } }       // Fenster zu: zurueck ins Hauptfenster
    else { ended = true; if (client) { try { client.disconnect(false); } catch { /* egal */ } } }   // niemand nimmt die Sitzung mehr: beenden
  });

  /* ---- Groesse: das Fenster bestimmt die Aufloesung des fernen Desktops ---- */
  const wanted = () => {
    const r = stage.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
    return { w: Math.max(400, Math.floor((r.width * dpr * k) / 4) * 4), h: Math.max(300, Math.floor((r.height * dpr * k) / 2) * 2) };
  };
  const fit = () => {
    if (!client) return;
    const d = client.getDisplay(), rw = d.getWidth();
    if (!rw) return;
    const dpr = window.devicePixelRatio || 1;
    let scale = stage.getBoundingClientRect().width / rw;
    if (Math.abs(scale * dpr - 1) < 0.02) scale = 1 / dpr;      // 1:1 auf den Geraetepixeln bleibt scharf
    d.scale(scale);
  };
  const pushSize = (force = false) => {
    if (!client || !client.connected) return;
    const s = wanted();
    if (!force && Math.abs(s.w - lastSent.w) < 8 && Math.abs(s.h - lastSent.h) < 16) return;
    lastSent = s; client.sendSize(s.w, s.h);
  };
  window.addEventListener('resize', () => { fit(); clearTimeout(sizeTimer); sizeTimer = setTimeout(() => { pushSize(); fit(); }, 350); });

  /* ---- Verbindung ---- */
  function connect() {
    if (ended) return;
    say(t('app.connecting'));
    const wsBase = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host;
    const c = new StreamClient(wsBase + '/api/remote/screen');
    client = c; c.quality = quality; bar.attach(c);
    const display = c.getDisplay();
    displayHost.replaceChildren(display.getElement());
    display.onresize = () => fit();
    display.showCursor(true);
    c.onsession = (o) => { if (client === c) resume.offered(o.token, o.keep); };
    c.onresumefailed = () => { if (client === c) finalMessage(t('popout.sessionGone')); };
    c.onstatechange = (st) => {
      if (client !== c) return;
      if (st === 3) {
        resume.connected(); say(''); fit(); lastSent = { w: 0, h: 0 }; pushSize(true);
        c.setPaused(document.hidden); startAudio(c); touch.focus();
      } else if (st === 5 && !ended) {
        if (c.drawn) resume.confirm();
        resume.lost();
        if (!resume.usable()) return finalMessage(t('popout.disconnected'));
        say(t('app.busyResuming'));
        retryTimer = setTimeout(connect, resume.nextDelay());
      }
    };
    c.onerror = (err) => {
      if (client !== c || ended) return;
      if (err && err.code === 520 && resume.usable()) return;        // Leitung weg: das Schliessen plant den naechsten Versuch
      finalMessage((err && err.message ? serverMsg(err.message) : '') || t('app.errConnection'));
    };
    c.onclipboard = (text) => {
      // Strg+C in der VM: Text in die Zwischenablage dieses Browsers (braucht Fokus und eine eben erfolgte Eingabe)
      try { if (navigator.clipboard && document.hasFocus()) navigator.clipboard.writeText(text).catch(() => {}); } catch { /* egal */ }
    };
    setupMouse(c, display);
    c.connect('resume=' + encodeURIComponent(resume.token));
  }
  function finalMessage(text) { ended = true; say(text); closeBtn.hidden = false; }

  function startAudio(c) {
    const a = c.audio;
    a.label = h.name; a.setVolume(store.get('volume', 1)); a.setActive(!document.hidden);
    if (store.get('audio', true)) a.enable().then(() => a.connected(), () => { /* Ton beginnt mit der ersten Eingabe */ });
  }

  /* ---- Maus ---- */
  function setupMouse(c, display) {
    const mouse = new MouseInput(touch);
    mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = (state) => {
      if (client !== c) return;
      const s = display.getScale() || 1;
      c.sendMouseState(mouseState(state.x / s, state.y / s, state.left, state.middle, state.right, state.up, state.down));
    };
    touch.focus();
  }

  /* ---- Tastatur: physische Tasten als Scancodes; Einfuegen ueber die Zwischenablage ---- */
  const down = new Set();
  let pasteKeys = [];
  const sendKey = (code, isDown) => {
    const sc = scancodeOf(code);
    if (!sc || !client) return false;
    if (isDown) down.add(code); else down.delete(code);
    client.key(sc[0], sc[1], isDown);
    return true;
  };
  const chord = (codes) => { for (const c of codes) sendKey(c, true); for (const c of [...codes].reverse()) sendKey(c, false); };
  window.addEventListener('keydown', (e) => {
    if (!client || !client.connected || ended) return;
    if (isPasteShortcut(e)) {            // das paste-Ereignis bringt den Text der lokalen Zwischenablage; erst dann geht die Taste hinaus
      pasteAt = Date.now();
      const keys = e.key === 'Insert' ? ['ShiftLeft', 'Insert'] : ['ControlLeft', 'KeyV'];
      setTimeout(() => { if (pasteAt) { pasteAt = 0; chord(keys); } }, 250);          // kein paste-Ereignis gekommen: die Taste trotzdem
      pasteKeys = keys; return;
    }
    if (scancodeOf(e.code)) { e.preventDefault(); sendKey(e.code, true); }
  }, true);
  window.addEventListener('paste', (e) => {
    if (!client || !pasteAt) return;
    e.preventDefault(); pasteAt = 0;
    const text = e.clipboardData && e.clipboardData.getData('text/plain');
    if (text) client.sendClipboardText(text);
    setTimeout(() => chord(pasteKeys), 60);
  });
  window.addEventListener('keyup', (e) => { if (!client || ended) return; if (scancodeOf(e.code)) { e.preventDefault(); sendKey(e.code, false); } }, true);
  // Fenster verliert den Fokus: alle gehaltenen Tasten loslassen (sonst bleibt am Ziel Strg oder Alt haengen)
  window.addEventListener('blur', () => { for (const code of [...down]) sendKey(code, false); });
  document.addEventListener('visibilitychange', () => { if (client) { client.setPaused(document.hidden); client.audio.setActive(!document.hidden); } });

  connect();
}
