// Platzhalter fuer das Server-Geruest: zeigt nur, ob Server und Datenbank antworten. Der echte Client folgt in Schritt 5.
fetch('/api/health').then((r) => r.json()).then((h) => {
  document.getElementById('state').textContent = h.ok ? `Server läuft (Version ${h.version}), Datenbank bereit.` : 'Server läuft, Datenbank nicht erreichbar.';
}).catch(() => { document.getElementById('state').textContent = 'Server nicht erreichbar.'; });
