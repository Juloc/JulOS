/* Push-Meldungen im Service Worker (docs/PUSH.md); sw.js laedt diese Datei mit importScripts. Die Nachricht vom Server enthaelt nur Namen und Codes
 * ({v, n, items: [{h: Hostname, r: Regel, k: alert|ok, id: Host}]}); der Text entsteht hier, in der Sprache des Geraets. Es wird nie mehr gezeigt als Hostname und Art
 * des Ereignisses: kein Pfad, keine Ausgabe, kein Geheimnis. Jede Push-Nachricht zeigt eine Benachrichtigung (der Browser verlangt das). */
const PUSH_TEXT = {
  de: {
    rule: { security: 'Neue Sicherheitsupdates', disk: 'Platte fast voll', service: 'Dienst oder Container ausgefallen', offline: 'Agent offline', reboot: 'Neustart nötig', test: 'Testmeldung: Meldungen funktionieren', passkey_added: 'Neuer Passkey eingerichtet', passkey_removed: 'Passkey entfernt' },
    ok: { offline: 'Agent wieder online', disk: 'Platte wieder in Ordnung', service: 'Dienst oder Container wieder in Ordnung' },
    many: (n) => `JulOS: ${n} Meldungen`, more: (n) => `und ${n} weitere`, plain: 'Neue Meldung',
  },
  en: {
    rule: { security: 'New security updates', disk: 'Disk almost full', service: 'Service or container failed', offline: 'Agent offline', reboot: 'Restart needed', test: 'Test message: notifications work', passkey_added: 'New passkey set up', passkey_removed: 'Passkey removed' },
    ok: { offline: 'Agent back online', disk: 'Disk back to normal', service: 'Service or container back to normal' },
    many: (n) => `JulOS: ${n} messages`, more: (n) => `and ${n} more`, plain: 'New message',
  },
};

/** Titel, Text, Tag und Ziel einer Benachrichtigung aus dem Inhalt der Push-Nachricht (rein, ohne Service-Worker-Umgebung). */
function describePush(payload, lang) {
  const tx = PUSH_TEXT[String(lang || '').toLowerCase().startsWith('de') ? 'de' : 'en'];
  const items = payload && Array.isArray(payload.items) ? payload.items.filter((i) => i && typeof i.r === 'string') : [];
  const clean = (s) => String(s == null ? '' : s).replace(/[\u0000-\u001f\u007f]/g, '').slice(0, 40);
  const line = (i) => (i.k === 'ok' && tx.ok[i.r]) || tx.rule[i.r] || tx.plain;
  const link = (i) => (/^[0-9a-f-]{36}$/i.test(i.id || '') ? `/#host=${i.id.toLowerCase()}` : '/');
  if (items.length === 0) return { title: 'JulOS', body: tx.plain, tag: 'julos-alerts', url: '/' };
  if (items.length === 1) { const i = items[0]; return { title: i.r === 'test' ? 'JulOS' : clean(i.h) || 'JulOS', body: line(i), tag: `${i.id || clean(i.h)}:${i.r}:${i.k}`, url: link(i) }; }
  const total = Number.isInteger(payload.n) && payload.n >= items.length ? payload.n : items.length;
  const hosts = new Set(items.map((i) => i.id || i.h));
  const lines = items.map((i) => `${clean(i.h)}: ${line(i)}`);
  if (total > items.length) lines.push(tx.more(total - items.length));
  return { title: tx.many(total), body: lines.join('\n'), tag: 'julos-alerts', url: hosts.size === 1 ? link(items[0]) : '/' };
}

if (typeof self !== 'undefined' && self.registration && typeof self.addEventListener === 'function') {
  self.addEventListener('push', (e) => {
    let payload = null;
    try { payload = e.data ? e.data.json() : null; } catch { payload = null; }
    const m = describePush(payload, self.navigator && self.navigator.language);
    e.waitUntil(self.registration.showNotification(m.title, { body: m.body, tag: m.tag, renotify: true, icon: '/icon-192.png', badge: '/icon-32.png', data: { url: m.url } }));
  });

  self.addEventListener('notificationclick', (e) => {
    e.notification.close();
    const url = (e.notification.data && e.notification.data.url) || '/';
    e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((list) => {
      const open = list.find((c) => new URL(c.url).origin === self.location.origin);
      if (open) { open.postMessage({ julos: 'open', hash: url.startsWith('/#') ? url.slice(1) : '' }); return open.focus(); }
      return self.clients.openWindow(url);
    }));
  });
}
