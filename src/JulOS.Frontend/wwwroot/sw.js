/* Service Worker: haelt die App-Huelle bereit (schneller Start, laeuft auch bei kurzem Netzausfall) und holt Updates zuerst
 * aus dem Netz. API-Aufrufe und WebSocket-Verbindungen werden nie zwischengespeichert. */
const VERSION = 'pd-v34';
const SHELL = ['/', '/app.js', '/stream.js', '/stream-audio.js', '/stream-audio-worklet.js', '/codecs.js', '/sessions.js', '/terminal.js', '/terminal-session.js', '/terminal.css', '/clip.js', '/layoutlog.js', '/shell.js', '/desktop.js', '/desktop-files.js', '/desktop-remote.js', '/desktop.css', '/mobile.js', '/mobile.css', '/m3.css', '/style.css', '/lib/guacamole-common.min.js', '/manifest.webmanifest', '/icon.svg', '/icon-192.png', '/icon-512.png'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(VERSION).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});
self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys().then((ks) => Promise.all(ks.filter((k) => k !== VERSION).map((k) => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener('fetch', (e) => {
  const req = e.request, url = new URL(req.url);
  if (req.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/')) return;
  e.respondWith(
    fetch(req).then((res) => {
      if (res.ok) { const copy = res.clone(); caches.open(VERSION).then((c) => c.put(req, copy)); }
      return res;
    }).catch(() => caches.match(req).then((m) => m || (req.mode === 'navigate' ? caches.match('/') : Response.error())))
  );
});
