/* Service Worker: haelt die App-Huelle bereit (schneller Start, laeuft auch bei kurzem Netzausfall) und holt Updates zuerst
 * aus dem Netz. API-Aufrufe und WebSocket-Verbindungen werden nie zwischengespeichert. */
const VERSION = 'pd-v78';
const SHELL = ['/', '/i18n.js', '/i18n-errors.js', '/i18n/de.js', '/i18n/en.js', '/app.js', '/stream.js', '/mouse.js', '/stream-audio.js', '/audiodiag.js', '/stream-audio-worklet.js', '/codecs.js', '/sessions.js', '/resume.js', '/link-health.js', '/terminal.js', '/terminal-session.js', '/terminal-programs.js', '/terminal-input.js', '/terminal.css', '/clip.js', '/clipdiag.js', '/clipperm.js', '/layoutlog.js', '/kbpan.js', '/shell.js', '/quick.js', '/folders.js', '/folder-dialog.js', '/prefs.js', '/scheme.js', '/account.js', '/security.js', '/stepup.js', '/passkeys.js', '/passkeys.css', '/qr.js', '/nettools.js', '/hosts.js', '/hostinfo.js', '/hostactions.js', '/hostactions-dialog.js', '/hosttasks.js', '/settings.js', '/desktop.js', '/desktop-files.js', '/files-app.js', '/files-core.js', '/files.css', '/desktop-modules.js', '/modules.js', '/mobile-modules.js', '/desktop-remote.js', '/desktop.css', '/mobile.js', '/mobile.css', '/m3.css', '/style.css', '/manifest.webmanifest', '/icon.svg', '/icon-32.png', '/icon-180.png', '/icon-192.png', '/icon-512.png', '/perf.js', '/statusbar.js', '/popout.html', '/popout.js', '/popout.css', '/popout-keys.js', '/popout-main.js', '/login.js', '/heimnetz.js', '/widgets.js', '/widgets.css', '/hostpage.js', '/hostui.css', '/hostagent.js', '/hostview.js', '/hostui.js', '/hostlogin.js', '/alerts.js', '/alerts.css', '/sw-push.js', '/desktop-window.js', '/desktop-snap.js', '/desktop-keys.js', '/desktop-wm.js', '/desktop-pills.js', '/desktop-workspace.js', '/dialogwin.css', '/files-ui.js', '/files-explorer.js', '/files-menu.js', '/files-editor.js', '/editor-logic.js', '/editor.css', '/vendor/codemirror/6.43.15/codemirror.mjs', '/vendor/codemirror/6.43.15/js-yaml.mjs', '/files-tree.js', '/confirm.js', '/files-viewer.js', '/viewer.css', '/desktop-taskbar.js', '/desktop-taskbar-geom.js', '/icons.js', '/icons.css', '/appearance.js', '/icon-picker.js', '/ai.js', '/ai.css', '/oidc-apps.js', '/oidc.css', '/touch-gestures.js', '/touch-ui.js', '/touch-settings.js', '/touch.css', '/clip-panel.js', '/keys-shortcuts.js', '/keys-sticky.js', '/kb-input.js'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(VERSION).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});
self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys().then((ks) => Promise.all(ks.filter((k) => k !== VERSION).map((k) => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener('fetch', (e) => {
  const req = e.request, url = new URL(req.url);
  if (req.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/') || url.pathname.startsWith('/oidc/') || url.pathname.startsWith('/.well-known/')) return;     // die Anmelde-Zentrale nie aus dem Speicher
  e.respondWith(
    fetch(req).then((res) => {
      if (res.ok) { const copy = res.clone(); caches.open(VERSION).then((c) => c.put(req, copy)); }
      return res;
    }).catch(() => caches.match(req).then((m) => m || (req.mode === 'navigate' ? caches.match('/') : Response.error())))
  );
});
