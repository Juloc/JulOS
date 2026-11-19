/* Service Worker: haelt die App-Huelle bereit (schneller Start, laeuft auch bei kurzem Netzausfall) und holt Updates zuerst
 * aus dem Netz. API-Aufrufe und WebSocket-Verbindungen werden nie zwischengespeichert. */
const VERSION = 'pd-v46';
const SHELL = ['/', '/app.js', '/stream.js', '/mouse.js', '/stream-audio.js', '/stream-audio-worklet.js', '/codecs.js', '/sessions.js', '/resume.js', '/terminal.js', '/terminal-session.js', '/terminal.css', '/clip.js', '/layoutlog.js', '/kbpan.js', '/shell.js', '/quick.js', '/folders.js', '/folder-dialog.js', '/prefs.js', '/scheme.js', '/account.js', '/qr.js', '/nettools.js', '/hosts.js', '/desktop.js', '/desktop-files.js', '/desktop-modules.js', '/modules.js', '/mobile-modules.js', '/desktop-remote.js', '/desktop.css', '/mobile.js', '/mobile.css', '/m3.css', '/style.css', '/manifest.webmanifest', '/icon.svg', '/icon-192.png', '/icon-512.png', '/perf.js', '/statusbar.js', '/popout.html', '/popout.js', '/popout.css', '/popout-keys.js', '/popout-main.js', '/login.js'];

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
