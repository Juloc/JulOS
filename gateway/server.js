'use strict';
/*
 * JulOS Gateway
 * Browser (Handy) <-WebSocket-> guacamole-lite <-> guacd <-RDP-> xrdp auf dieser VM
 *
 * - Ziel (Host/Port/Benutzer) ist fest in der Umgebung gesetzt, der Client liefert nur das Passwort.
 * - Das Passwort wird nicht gespeichert, nur in einem kurzlebigen, verschluesselten Token weitergereicht.
 */
const crypto = require('crypto');
const fs = require('fs');
const http = require('http');
const path = require('path');
const express = require('express');
const GuacamoleLite = require('guacamole-lite');

const PORT = parseInt(process.env.PORT || '8080', 10);
const KEY = process.env.TOKEN_KEY || '';
if (KEY.length !== 32) {
  console.error('TOKEN_KEY muss genau 32 Zeichen lang sein (siehe .env).');
  process.exit(1);
}
const GUACD_HOST = process.env.GUACD_HOST || 'guacd';
const GUACD_PORT = parseInt(process.env.GUACD_PORT || '4822', 10);
const RDP_HOST = process.env.RDP_HOST || 'host.docker.internal';
const RDP_PORT = process.env.RDP_PORT || '3389';
const RDP_USER = process.env.RDP_USER || '';
if (!RDP_USER) {
  console.error('RDP_USER fehlt (Benutzername auf dem Zielrechner, siehe .env.example).');
  process.exit(1);
}
const TOKEN_TTL_MS = 30 * 1000; // Token ist nur 30 s gueltig (Schutz gegen Wiederverwenden)

function encryptToken(obj) {
  const iv = crypto.randomBytes(16);
  const cipher = crypto.createCipheriv('aes-256-cbc', Buffer.from(KEY), iv);
  let enc = cipher.update(JSON.stringify(obj), 'utf8', 'base64');
  enc += cipher.final('base64');
  return Buffer.from(JSON.stringify({ iv: iv.toString('base64'), value: enc })).toString('base64');
}

const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const num = (v, d) => (Number.isFinite(Number(v)) ? Number(v) : d);

// Betrieb hinter einem HTTPS-Reverse-Proxy (Caddy): Ist PROXY_SECRET gesetzt, wird jede Anfrage
// (HTTP und WebSocket) abgelehnt, die nicht den geheimen Header X-Proxy-Secret mitbringt.
// So ist das Gateway nur noch ueber den Proxy erreichbar, nicht mehr direkt per HTTP im LAN.
const PROXY_SECRET = process.env.PROXY_SECRET || '';
function proxyOk(headerValue) {
  if (!PROXY_SECRET) return true;
  const a = Buffer.from(String(headerValue || ''));
  const b = Buffer.from(PROXY_SECRET);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

const app = express();
app.disable('x-powered-by');
// echte Client-Adresse aus X-Forwarded-For nur dann vertrauen, wenn der Proxy-Header stimmt
app.set('trust proxy', PROXY_SECRET ? true : false);
if (PROXY_SECRET) {
  app.use((req, res, next) => {
    if (proxyOk(req.get('x-proxy-secret'))) return next();
    res.status(403).type('text/plain').send('Nur ueber den HTTPS-Proxy erreichbar.');
  });
}
app.use(express.json({ limit: '4kb' }));
app.use((req, res, next) => {
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('Referrer-Policy', 'no-referrer');
  res.setHeader('X-Frame-Options', 'DENY');
  if (req.path.startsWith('/api/')) res.setHeader('Cache-Control', 'no-store');
  next();
});

// Client-Bibliothek (ESM) aus dem npm-Paket
app.use('/lib', express.static(path.join(__dirname, 'node_modules/guacamole-common-js/dist/esm'), { index: false, maxAge: '1d' }));
app.use(express.static(path.join(__dirname, 'public'), { maxAge: 0, etag: true }));

// Bremse gegen Durchprobieren von Passwoertern
const attempts = new Map();
function tooMany(ip) {
  const now = Date.now();
  const list = (attempts.get(ip) || []).filter((t) => now - t < 60 * 1000);
  list.push(now);
  attempts.set(ip, list);
  return list.length > 15;
}
setInterval(() => {
  const now = Date.now();
  for (const [ip, list] of attempts) if (!list.some((t) => now - t < 60 * 1000)) attempts.delete(ip);
}, 5 * 60 * 1000).unref();

app.get('/api/info', (req, res) => res.json({ user: RDP_USER }));

app.post('/api/token', (req, res) => {
  if (tooMany(req.ip)) return res.status(429).json({ error: 'Zu viele Versuche, bitte kurz warten.' });
  const b = req.body || {};
  if (typeof b.password !== 'string' || !b.password || b.password.length > 256) {
    return res.status(400).json({ error: 'Passwort fehlt.' });
  }
  // Remote-Aufloesung: Vielfaches von 4 (Breite) bzw. 2 (Hoehe), sinnvolle Grenzen
  const width = clamp(Math.round(num(b.width, 900)), 400, 4096) & ~3;
  const height = clamp(Math.round(num(b.height, 1600)), 300, 4096) & ~1;
  const dpi = clamp(Math.round(num(b.dpi, 96)), 72, 288);

  const settings = {
    hostname: RDP_HOST,
    port: RDP_PORT,
    username: RDP_USER,
    password: b.password,
    security: 'any',
    'ignore-cert': true,
    'resize-method': 'display-update',
    width,
    height,
    dpi,
    'color-depth': 24,
    'server-layout': 'de-de-qwertz',
    timezone: 'Europe/Berlin',
    'enable-font-smoothing': true,
    'enable-wallpaper': false,
    'enable-theming': true,
    'enable-full-window-drag': false,
    'enable-desktop-composition': false,
    'enable-menu-animations': false,
    'disable-audio': true,
    'clipboard-encoding': 'UTF-8',
    'client-name': 'julos',
  };
  const sid = crypto.randomBytes(16).toString('hex');
  sessions.set(sid, { created: Date.now(), open: false, openedAt: 0 });
  res.json({ token: encryptToken({ expiration: Date.now() + TOKEN_TTL_MS, sid, connection: { type: 'rdp', settings } }), sid, width, height });
});

// Diagnose vom Client (nur Messwerte, kein Text): wird ins Gateway-Log geschrieben
const logHits = new Map();
app.post('/api/log', express.json({ limit: '12kb' }), (req, res) => {
  const now = Date.now();
  const hits = (logHits.get(req.ip) || []).filter((t) => now - t < 60000); hits.push(now); logHits.set(req.ip, hits);
  if (hits.length > 60) return res.status(429).end();
  const list = Array.isArray(req.body && req.body.events) ? req.body.events.slice(0, 40) : [];
  for (const ev of list) {
    const safe = {};
    for (const [k, v] of Object.entries(ev || {})) if (/^[a-zA-Z0-9_-]{1,16}$/.test(k) && (typeof v === 'number' || typeof v === 'boolean' || (typeof v === 'string' && v.length <= 24))) safe[k] = v;
    console.log(new Date().toISOString().slice(11, 19), 'Client', req.ip, JSON.stringify(safe));
  }
  res.status(204).end();
});

// Foto-Upload: wird in einen gemeinsamen Ordner geschrieben; ein Helfer in der VM-Sitzung (tools/julos-clip.py)
// legt das Bild in die Zwischenablage und quittiert mit <datei>.ok oder <datei>.err
const UPLOAD_DIR = process.env.UPLOAD_DIR || '/uploads';
const EXT = { 'image/png': 'png', 'image/jpeg': 'jpg', 'image/webp': 'webp', 'image/gif': 'gif' };
const upHits = new Map();
app.post('/api/upload', express.raw({ type: Object.keys(EXT), limit: '14mb' }), async (req, res) => {
  const now = Date.now();
  const hits = (upHits.get(req.ip) || []).filter((t) => now - t < 60000); hits.push(now); upHits.set(req.ip, hits);
  if (hits.length > 20) return res.status(429).json({ error: 'Zu viele Uploads, bitte kurz warten.' });
  const type = String(req.headers['content-type'] || '').split(';')[0].trim();
  if (!EXT[type] || !Buffer.isBuffer(req.body) || !req.body.length) return res.status(400).json({ error: 'Kein gueltiges Bild (png, jpg, webp, gif).' });
  const name = `${Date.now()}-${crypto.randomBytes(4).toString('hex')}.${EXT[type]}`;
  const tmp = path.join(UPLOAD_DIR, name + '.tmp');
  const dst = path.join(UPLOAD_DIR, name);
  try {
    fs.writeFileSync(tmp, req.body, { mode: 0o644 });
    fs.renameSync(tmp, dst);
  } catch (e) {
    return res.status(500).json({ error: 'Upload-Ordner nicht beschreibbar.' });
  }
  for (let i = 0; i < 40; i++) { // bis 8 s auf den Helfer warten
    await new Promise((r) => setTimeout(r, 200));
    if (fs.existsSync(dst + '.ok')) { try { fs.unlinkSync(dst + '.ok'); } catch { /* egal */ } return res.json({ ok: true }); }
    if (fs.existsSync(dst + '.err')) {
      let msg = ''; try { msg = fs.readFileSync(dst + '.err', 'utf8').slice(0, 80); fs.unlinkSync(dst + '.err'); } catch { /* egal */ }
      return res.status(500).json({ error: 'Helfer: ' + msg });
    }
  }
  res.status(504).json({ error: 'Der Helfer in der VM-Sitzung antwortet nicht (laeuft tools/julos-clip.py?).' });
});

// ---- Sitzungen: Fenster-/Agent-Zugriff nur fuer eine Verbindung, die wirklich steht (RDP-Anmeldung hat geklappt,
//      erkennbar daran, dass die Verbindung mehr als 8 s offen bleibt). Eine abgelehnte Anmeldung faellt vorher weg.
const sessions = new Map();
const SESSION_MIN_OPEN_MS = 8000;
setInterval(() => {
  const now = Date.now();
  for (const [sid, s] of sessions) if (!s.open && now - s.created > 120000) sessions.delete(sid);
}, 30000).unref();
function needSession(req, res, next) {
  const s = sessions.get(String(req.get('x-session') || ''));
  if (!s || !s.open || Date.now() - s.openedAt < SESSION_MIN_OPEN_MS) return res.status(401).json({ error: 'Keine gueltige Sitzung.' });
  next();
}

// ---- Agent-Proxy (Agent-API v1, siehe docs/AGENT_API.md): spricht ueber einen Unix-Socket mit dem Agenten in der VM-Sitzung
const AGENT_SOCK = process.env.AGENT_SOCKET || '/uploads/.agent/agent.sock';
function agentRequest(method, p, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : null;
    const req = http.request({ socketPath: AGENT_SOCK, path: p, method, timeout: 8000,
      headers: data ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) } : {} }, (r) => {
      let buf = '';
      r.on('data', (c) => { buf += c; if (buf.length > 600000) req.destroy(new Error('Antwort zu gross')); });
      r.on('end', () => { try { resolve({ status: r.statusCode, body: JSON.parse(buf || '{}') }); } catch { reject(new Error('Agent-Antwort ungueltig')); } });
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('Agent-Zeitueberschreitung')));
    if (data) req.write(data);
    req.end();
  });
}
const viaAgent = (method, pathFn) => async (req, res) => {
  try { const r = await agentRequest(method, pathFn(req), method === 'POST' ? (req.body || {}) : null); res.status(r.status).json(r.body); }
  catch (e) { res.status(502).json({ error: 'Agent nicht erreichbar', detail: String(e.message).slice(0, 80) }); }
};
app.get('/api/agent/health', async (req, res) => {
  try { const r = await agentRequest('GET', '/v1/health'); res.json({ available: true, ...r.body }); } catch { res.json({ available: false }); }
});
app.get('/api/agent/windows', needSession, viaAgent('GET', () => '/v1/windows'));
app.get('/api/agent/apps', needSession, viaAgent('GET', () => '/v1/apps'));
app.post('/api/agent/windows/:id/:act', needSession, (req, res, next) => {
  if (!/^\d{1,12}$/.test(req.params.id) || !['activate', 'close', 'minimize'].includes(req.params.act)) return res.status(400).json({ error: 'ungueltig' });
  next();
}, viaAgent('POST', (req) => `/v1/windows/${req.params.id}/${req.params.act}`));
app.post('/api/agent/apps/launch', needSession, viaAgent('POST', () => '/v1/apps/launch'));
app.post('/api/agent/mode', needSession, viaAgent('POST', () => '/v1/mode'));

app.get('/api/health', (req, res) => res.json({ ok: true }));

const server = http.createServer(app);

// Kurzes Zugriffs-Log zur Fehlersuche (ohne Query-Strings, Header-Werte und Bodys, also ohne Token/Passwoerter)
if (process.env.LOG_REQUESTS !== '0') {
  const ts = () => new Date().toISOString().replace('T', ' ').slice(0, 19);
  server.on('connection', (sock) => console.log(ts(), 'verbunden', (sock.remoteAddress || '?').replace('::ffff:', '')));
  server.on('clientError', (err, sock) => console.log(ts(), 'Client-Fehler', err.code || err.message, (sock.remoteAddress || '?').replace('::ffff:', '')));
  server.on('request', (req) => console.log(ts(), 'Anfrage', req.method, (req.url || '').split('?')[0], (req.socket.remoteAddress || '?').replace('::ffff:', '')));
  server.on('upgrade', (req) => console.log(ts(), 'WebSocket', (req.socket.remoteAddress || '?').replace('::ffff:', '')));
}

const callbacks = {
  processConnectionSettings: (settings, callback) => {
    if (!settings.expiration || settings.expiration < Date.now()) return callback(new Error('Token abgelaufen'));
    callback(null, settings);
  },
};

const guac = new GuacamoleLite(
  {
    server,
    // WebSocket-Aufbau ebenfalls nur mit gueltigem Proxy-Header (wenn PROXY_SECRET gesetzt ist)
    verifyClient: (info, cb) => (proxyOk(info.req.headers['x-proxy-secret']) ? cb(true) : cb(false, 403, 'Forbidden')),
  },
  { host: GUACD_HOST, port: GUACD_PORT },
  { crypt: { cypher: 'AES-256-CBC', key: KEY }, log: { level: 'ERRORS' } },
  callbacks
);

server.listen(PORT, '0.0.0.0', () => console.log(`JulOS Gateway laeuft auf Port ${PORT}, Ziel ${RDP_USER}@${RDP_HOST}:${RDP_PORT}`));

guac.on('open', (conn) => {
  const s = sessions.get(conn && conn.connectionSettings && conn.connectionSettings.sid);
  if (s) { s.open = true; s.openedAt = Date.now(); }
});
guac.on('close', (conn) => {
  const sid = conn && conn.connectionSettings && conn.connectionSettings.sid;
  if (sid) sessions.delete(sid);
});
