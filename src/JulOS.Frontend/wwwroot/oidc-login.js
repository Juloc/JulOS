/* Anmelden auf der Seite der Anmelde-Zentrale (/oidc/authorize ohne Sitzung, docs/SIGN-IN.md). Die Seite wird vom Server gebaut; dieses Skript schickt die Eingabe an den
 * vorhandenen Weg /api/auth/login (mit allen seinen Schutzmassnahmen: Sperre, zweiter Faktor, Verlauf) und laedt danach die Anfrage der App neu. Nur eigene Adresse, kein Fremdcode. */
const form = document.getElementById('oidcLogin');
if (form) {
  const msg = document.getElementById('msg');
  const next = form.dataset.next || '';
  // nur zurueck zur eigenen Anfrage, nie irgendwohin
  const safe = next.startsWith('/oidc/authorize?') || next.startsWith('/oidc/end-session?');
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const button = form.querySelector('button[type=submit]');
    button.disabled = true; msg.textContent = '';
    try {
      const r = await fetch('/api/auth/login', {
        method: 'POST', headers: { 'X-JulOS': '1', 'Content-Type': 'application/json' },
        body: JSON.stringify({ user: form.user.value.trim(), password: form.password.value, code: form.code.value.replace(/\s+/g, '') }),
      });
      if (!r.ok) {
        let data = null; try { data = await r.json(); } catch { /* kein JSON */ }
        msg.textContent = (data && data.error) || 'Fehler ' + r.status;
        form.password.value = ''; form.code.value = ''; button.disabled = false;
        return;
      }
      form.password.value = ''; form.code.value = '';
      location.replace(safe ? next : '/');
    } catch { msg.textContent = 'Server nicht erreichbar.'; button.disabled = false; }
  });
}
