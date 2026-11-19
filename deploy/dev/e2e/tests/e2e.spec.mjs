import { test, expect } from '@playwright/test';
import { existsSync } from 'node:fs';
import { AUTH_FILE, RDP, SSH, STATE_FILE, loadState, newPassword, saveState, totp } from './helpers.mjs';

const DESKTOP = { viewport: { width: 1280, height: 800 } };
const PHONE = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };

/** Sammelt Seitenfehler und Konsolenfehler einer Seite (die Tests pruefen am Ende, dass es keine gab). */
function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  return errors;
}

/** Neue Seite mit der Anmeldung aus Test 1 (Cookie), damit nicht jeder Ablauf neu anmelden muss. */
async function signedIn(browser, options = DESKTOP) {
  if (!existsSync(AUTH_FILE)) throw new Error('Keine Anmeldung aus Test 1 vorhanden (er muss zuerst laufen und bestehen).');
  const context = await browser.newContext({ ...options, storageState: AUTH_FILE, baseURL: 'http://127.0.0.1:8080' });
  const page = await context.newPage();
  return { context, page, errors: watchErrors(page) };
}

test.describe('Ersteinrichtung', () => {
  test.describe.configure({ retries: 0 });   // ein zweiter Versuch fiele auf ein schon eingerichtetes Konto: Fehler gleich zeigen
  test('1 Ersteinrichtung und Anmeldung mit Passwort und Authenticator-Code', async ({ page, context }) => {
    const errors = watchErrors(page);
    const user = 'e2e', password = newPassword();
    await page.goto('/');
    await expect(page.locator('#setupForm')).toBeVisible();
    await page.fill('#setupCode', process.env.E2E_SETUP_CODE || '');
    await page.fill('#setupUser', user);
    await page.fill('#setupPw', password);
    await page.fill('#setupPw2', password);
    await page.click('#setupForm button[type=submit]');
    await expect(page.locator('#setupSecretForm')).toBeVisible();
    const secret = (await page.locator('#setupSecret').textContent()).trim();
    const recovery = (await page.locator('#setupRecovery').textContent()).trim().split(/\s+/);
    expect(recovery.length).toBeGreaterThan(3);
    await page.fill('#setupTotp', totp(secret));
    await page.click('#setupSecretForm button[type=submit]');
    await expect(page.locator('#desktop')).toBeVisible();

    // Neues Gerät: ohne Cookie landet man bei der Anmeldung; Passwort plus Code des nächsten Zeitschritts (der Server nimmt +-1, der aktuelle ist verbraucht)
    await context.clearCookies();
    await page.reload();
    await expect(page.locator('#loginForm')).toBeVisible();
    await page.fill('#loginUser', user);
    await page.fill('#loginPw', 'falsch-' + password);
    await page.fill('#loginCode', totp(secret, 1));
    await page.click('#loginBtn');
    await expect(page.locator('#loginMsg')).not.toBeEmpty();           // falsches Passwort wird abgelehnt
    await page.fill('#loginPw', password);
    await page.fill('#loginCode', totp(secret, 1));
    await page.click('#loginBtn');
    await expect(page.locator('#desktop')).toBeVisible();
    await expect(page.locator('#loginForm')).toBeHidden();
    await context.storageState({ path: AUTH_FILE });
    saveState({ user, password });
    expect(errors.filter((e) => !/401|403/.test(e))).toEqual([]);
  });
});

const API = { 'X-JulOS': '1', Origin: 'http://127.0.0.1:8080' };   // so erkennt der Server Aufrufe der eigenen Seite (Fremd-Ursprung wird abgelehnt)
const DESKTOP_CONN = { name: 'E2E Desktop', kind: 'desktop', host: RDP.host, user: RDP.user };
const SSH_CONN = { name: 'E2E Terminal', kind: 'terminal', host: SSH.host, port: SSH.port, user: SSH.user };

const tile = (page, name) => page.locator('#desktop .dk-tile', { hasText: name }).first(); // die Kachel steht auch unter "Zuletzt benutzt"

/** Legt eine Verbindung über den Dialog an (Desktop-Oberfläche: Kachel "Verbindung hinzufügen" auf der Startseite). */
async function addConnection(page, c) {
  await page.locator('#desktop [data-act="add"]').first().click();
  await expect(page.locator('#connDlg')).toBeVisible();
  await page.fill('#connName', c.name);
  await page.selectOption('#connKind', c.kind);
  await page.fill('#connHost', c.host);
  if (c.port) await page.fill('#connPort', c.port);
  await page.fill('#connUser', c.user);
  await page.click('#connSave');
  await expect(page.locator('#connDlg')).toBeHidden();
  await expect(tile(page, c.name)).toBeVisible();
}

/** Öffnet die Sitzung einer Verbindung wie ein Benutzer: Kachel, Passwort tippen, Verbinden. */
async function connect(page, name, password) {
  await tile(page, name).click();
  await expect(page.locator('#startDlg')).toBeVisible();
  await page.fill('#startPw', password);
  await page.click('#startGo');
  await expect(page.locator('#startDlg')).toBeHidden();
}

/** Wie viel Bild die Sitzung zeigt: Größe und Anzahl verschiedener Farben (grob abgetastet) der inhaltsreichsten Zeichenfläche. */
const picture = (page) => page.evaluate(() => {
  const probe = document.createElement('canvas'); probe.width = 48; probe.height = 27;
  const g = probe.getContext('2d', { willReadFrequently: true });
  let best = { w: 0, h: 0, colors: 0, max: 0 };
  for (const c of document.querySelectorAll('.sv-display canvas')) {
    if (c.width < 200 || c.height < 100) continue;                 // der Mauszeiger und andere kleine Flächen zählen nicht
    g.clearRect(0, 0, 48, 27); g.drawImage(c, 0, 0, 48, 27);
    const d = g.getImageData(0, 0, 48, 27).data, seen = new Set(); let max = 0;
    for (let i = 0; i < d.length; i += 4) { seen.add(((d[i] >> 4) << 8) | ((d[i + 1] >> 4) << 4) | (d[i + 2] >> 4)); max = Math.max(max, d[i], d[i + 1], d[i + 2]); }
    if (seen.size > best.colors) best = { w: c.width, h: c.height, colors: seen.size, max };
  }
  return best;
});

/** Wartet auf ein echtes Bild der VM (nicht schwarz, mehrere Farben) und prüft, dass die Bildschirmverbindung des Streams offen ist. */
async function waitForPicture(page, sockets) {
  await expect.poll(async () => { const p = await picture(page); return p.max > 10 && p.colors >= 4; }, { message: 'die VM liefert ein Bild', timeout: 40_000, intervals: [500] }).toBe(true);
  expect(sockets.some((u) => u.includes('/api/remote/screen')), 'die Bildschirmverbindung des Streams ist offen').toBe(true);
}

const watchSockets = (page) => { const urls = []; page.on('websocket', (ws) => urls.push(ws.url())); return urls; };

test('2 Verbindungen anlegen (Desktop und Terminal) und nach dem Neuladen wiederfinden', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  // Wiederholung: Reste eines halb gelaufenen ersten Versuchs wegräumen
  for (const c of (await (await page.request.get('/api/connections')).json()).connections) await page.request.delete('/api/connections/' + c.id, { headers: API });
  await page.goto('/');
  await expect(page.locator('#desktop')).toBeVisible();
  await addConnection(page, DESKTOP_CONN);
  await addConnection(page, SSH_CONN);
  await page.reload();
  await expect(tile(page, DESKTOP_CONN.name)).toContainText(RDP.host);
  await expect(tile(page, SSH_CONN.name)).toContainText(SSH.host);
  expect(errors.filter((e) => !/401|403/.test(e))).toEqual([]);
  await context.close();
});

test('3 RDP-Stream verbindet und liefert ein Bild', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  const sockets = watchSockets(page);
  await page.goto('/');
  await connect(page, DESKTOP_CONN.name, RDP.password);
  await waitForPicture(page, sockets);
  await expect(page.locator('.dk-win .sv-overlay').first()).toBeHidden();      // keine Fehler- oder Ladeanzeige mehr
  expect(errors).toEqual([]);
  await context.close();
});

/** Fensterliste des Ziel-Desktops über den Agenten (läuft in der Sitzung der VM und kennt die echten Fenster). */
async function windowsOf(page, id) {
  const r = await page.request.get('/api/agent/windows?c=' + id);
  return r.ok() ? (await r.json()).windows : [];
}
const connectionId = async (page, name) => (await (await page.request.get('/api/connections')).json()).connections.find((c) => c.name === name).id;

test('4 Klick und Tastatur erreichen den Desktop im Ziel', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  const sockets = watchSockets(page);
  await page.goto('/');
  const id = await connectionId(page, DESKTOP_CONN.name);
  await connect(page, DESKTOP_CONN.name, RDP.password);
  await waitForPicture(page, sockets);
  await expect.poll(async () => { const r = await page.request.get('/api/agent/health?c=' + id); return r.ok() && (await r.json()).available; }, { message: 'der Agent im Ziel meldet sich', timeout: 40_000 }).toBe(true);
  const terminals = async () => (await windowsOf(page, id)).filter((w) => /terminal/i.test(w.app));
  const closeAll = async () => { for (const w of await terminals()) await page.request.post(`/api/agent/windows/${w.id}/close`, { headers: API }); };
  await closeAll();                                                 // Fenster früherer Läufe (der Desktop des Ziels wird wieder aufgenommen) stören nicht
  await expect.poll(async () => (await terminals()).length).toBe(0);

  // Tastenkombination: Strg+Alt+T öffnet im Ziel ein Terminal (Einstellung des Test-Desktops)
  await page.keyboard.press('Control+Alt+t');
  await expect.poll(async () => (await terminals()).length, { message: 'Strg+Alt+T hat im Ziel ein Terminal geöffnet', timeout: 15_000 }).toBe(1);

  // Text: mit Sonderzeichen ins Terminal tippen; der Befehl setzt den Fenstertitel, den der Agent meldet
  const marker = 'e2e-' + Date.now().toString(36);
  await page.keyboard.type(String.raw`printf '\033]0;${marker}\007'`);
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await terminals())[0]?.title?.includes(marker), { message: 'getippter Text kam im Ziel an (Fenstertitel)', timeout: 15_000 }).toBe(true);

  // Klick: auf "Maximieren" in der Titelleiste des neuen Terminals (es öffnet immer oben links in derselben Größe; Maße des Test-Desktops)
  const canvas = await page.locator('.sv-display canvas').first().boundingBox();
  expect((await terminals())[0].maximized).toBe(false);
  await page.mouse.click(canvas.x + 788, canvas.y + 42);
  await expect.poll(async () => (await terminals())[0]?.maximized, { message: 'der Klick erreichte die Titelleiste im Ziel', timeout: 10_000 }).toBe(true);

  await closeAll();
  expect(errors).toEqual([]);
  await context.close();
});

test('5 Natives SSH-Terminal zeigt eine Eingabeaufforderung und führt Befehle aus', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  await context.addInitScript(() => localStorage.setItem('pd.termGl', 'false'));   // Text im DOM statt auf der WebGL-Fläche (vorhandener Schalter, lesbar für den Test)
  await page.goto('/');
  await connect(page, SSH_CONN.name, SSH.password);
  const rows = page.locator('.sv-terminal .xterm-rows');
  await expect(rows).toContainText(/[$#] *$/m, { timeout: 30_000 });                 // Eingabeaufforderung der Shell
  await page.keyboard.type('echo e2e-$((6*7))');
  await page.keyboard.press('Enter');
  await expect(rows).toContainText('e2e-42');                                          // die Ausgabe (die Eingabezeile selbst zeigt $((6*7)))
  expect(errors).toEqual([]);
  await context.close();
});

test('6 Handy- und Desktop-Oberfläche laden ohne Konsolenfehler', async ({ browser }) => {
  const checks = [
    { name: 'phone', url: '/?phone', options: PHONE, root: '#mobile', tile: '#mobile [data-conn]', before: (page) => page.locator('#mobile').getByText('Dienste', { exact: true }).last().click() },
    { name: 'desktop', url: '/?desktop', options: DESKTOP, root: '#desktop', tile: '#desktop .dk-tile' },
  ];
  for (const c of checks) {
    const { context, page, errors } = await signedIn(browser, c.options);
    await page.goto(c.url);
    await expect(page.locator(c.root), c.name).toBeVisible();
    if (c.before) await c.before(page);                            // Handy: die Verbindungen stehen unter "Dienste"
    await expect(page.locator(c.tile).filter({ hasText: DESKTOP_CONN.name }).first(), c.name).toBeVisible();
    await page.waitForLoadState('networkidle');
    expect(errors, c.name).toEqual([]);
    await context.close();
  }
});

test('7 Sprache: auf Englisch umschalten, Überschrift und Kachel sind englisch, danach zurück auf Deutsch', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  await page.request.delete('/api/settings/lang', { headers: API });            // Wiederholung: Rest eines halb gelaufenen ersten Versuchs wegräumen
  await page.goto('/?desktop');
  await page.evaluate(() => localStorage.removeItem('pd.lang'));
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('lang', 'de');
  await page.locator('#desktop [data-go="einstellungen"], #desktop [data-nav="einstellungen"]').first().click();
  await expect(page.locator('#desktop h3', { hasText: 'Einstellungen' })).toBeVisible();
  await page.locator('#desktop [data-lang="en"]').click();                       // lädt die Seite neu
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await expect(page.locator('#desktop')).toBeVisible();
  await expect(page.locator('#desktop [data-act="add"]', { hasText: 'New connection' }).first()).toBeVisible();
  await expect.poll(async () => (await (await page.request.get('/api/settings')).json()).settings.lang?.lang, { message: 'der Server hat die Sprache gemerkt' }).toBe('en');
  await page.locator('#desktop [data-go="einstellungen"], #desktop [data-nav="einstellungen"]').first().click();
  await expect(page.locator('#desktop h3', { hasText: 'Settings' })).toBeVisible();
  await page.locator('#desktop [data-lang="de"]').click();
  await expect(page.locator('html')).toHaveAttribute('lang', 'de');
  await expect(page.locator('#desktop [data-act="add"]', { hasText: 'Neue Verbindung' }).first()).toBeVisible();
  await expect.poll(async () => (await (await page.request.get('/api/settings')).json()).settings.lang?.lang).toBe('de');
  expect(errors).toEqual([]);
  await context.close();
});

/** Die Zwischenablage der VM setzen und lesen: der kleine Dienst in der Sitzung des Ziels (deploy/dev/rdp-target/clip-probe.py, xclip). */
const probe = (path, init) => fetch(`http://${RDP.host}:9777${path}`, init);

test('8 Zwischenablage: Text aus der VM im Browser, Markieren mit Umschalt+Pfeilen und Strg+C in einem echten Fenster, Strg+V mit Zeilenumbruch in die VM, Diagnose', async ({ browser }) => {
  test.setTimeout(100_000);
  const { context, page, errors } = await signedIn(browser);
  await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: 'http://127.0.0.1:8080' });
  const sockets = watchSockets(page);
  await page.goto('/');
  const id = await connectionId(page, DESKTOP_CONN.name);
  await connect(page, DESKTOP_CONN.name, RDP.password);
  await waitForPicture(page, sockets);
  await page.bringToFront();
  await expect.poll(async () => { const r = await page.request.get('/api/agent/health?c=' + id); return r.ok() && (await r.json()).available; }, { message: 'der Agent im Ziel meldet sich', timeout: 40_000 }).toBe(true);
  const readClip = () => page.evaluate(() => navigator.clipboard.readText());
  const stamp = Date.now().toString(36);

  // 1) Text, der in der VM kopiert wird (xclip, wie Strg+C in einem Programm), liegt gleich in der Zwischenablage des Browsers. Der Kanal der VM braucht nach dem
  //    Verbinden einen Augenblick: die Kopie wird bis zum Erfolg wiederholt.
  const fromVm = `aus der VM äöü ${stamp}`;
  await expect.poll(async () => { await probe('/set', { method: 'POST', body: fromVm }); await page.waitForTimeout(700); return readClip(); }, { message: 'Text aus der VM erscheint in navigator.clipboard', timeout: 40_000, intervals: [100] }).toBe(fromVm);

  // 2) Ein echtes Programmfenster der VM: Eingabefeld mit Text; Umschalt+Pos1 und Umschalt+Pfeile markieren, Strg+C legt die Markierung in die Zwischenablage des Browsers
  await probe('/entry', { method: 'POST', body: 'Hallo Welt' });
  const entryWindow = async () => (await windowsOf(page, id)).find((w) => w.title === 'clip-entry');
  await expect.poll(async () => !!(await entryWindow()), { message: 'das Eingabefenster der VM erscheint', timeout: 20_000 }).toBe(true);
  await page.request.post(`/api/agent/windows/${(await entryWindow()).id}/activate`, { headers: API });
  await expect.poll(async () => (await entryWindow())?.active, { message: 'das Eingabefenster hat den Fokus', timeout: 10_000 }).toBe(true);
  await page.keyboard.press('End');
  await page.keyboard.press('Shift+Home');                        // alles markieren
  await page.keyboard.press('Control+c');
  await expect.poll(readClip, { message: 'Umschalt+Pos1 markiert, Strg+C kopiert in den Browser', timeout: 15_000 }).toBe('Hallo Welt');
  await page.keyboard.press('Home');
  for (let i = 0; i < 4; i++) await page.keyboard.press('Shift+ArrowRight');    // "Hall" markieren
  await page.keyboard.press('Control+c');
  await expect.poll(readClip, { message: 'Umschalt+Pfeil markiert genau vier Zeichen', timeout: 15_000 }).toBe('Hall');

  // 3) Strg+V im Browser: der Text (mit Zeilenumbruch) liegt danach als LF, ohne CR, in der Zwischenablage der VM
  const toVm = `Zeile eins ${stamp}\nZeile zwei ä`;
  await page.evaluate((v) => navigator.clipboard.writeText(v), toVm);
  await page.keyboard.press('Control+v');
  await expect.poll(async () => { const r = await probe('/get'); return r.ok ? await r.text() : null; }, { message: 'Strg+V legte den Text in die Zwischenablage der VM (LF, kein CR)', timeout: 15_000, intervals: [300] }).toBe(toVm);

  // 4) Die Diagnose im Dialog zeigt Kanal, beide Richtungen und keinen Fehler; ohne Inhalt
  await page.evaluate(() => document.querySelector('#clipDlg').showModal());
  const diag = page.locator('#clipDiagList');
  await expect(diag).toContainText('Kanal der VM: bereit');
  await expect(diag).toContainText('Aus der VM:');
  await expect(diag).toContainText('im Browser abgelegt');
  await expect(diag).toContainText(/In die VM: .*Zeichen; die VM hat den Text abgeholt/);
  await expect(diag).not.toContainText(stamp);
  await page.evaluate(() => document.querySelector('#clipDlg').close());

  await probe('/entry-close', { method: 'POST' });

const PROG_CLAUDE = { name: 'E2E Claude', kind: 'terminal', host: SSH.host, port: Number(SSH.port), username: SSH.user, program: 'claude', workDir: '/tmp' };
const PROG_CODEX = { name: 'E2E Codex', kind: 'terminal', host: SSH.host, port: Number(SSH.port), username: SSH.user, program: 'codex' };

test('9 Programm-Sitzungen: Claude startet im Arbeitsverzeichnis, die Eingabeleiste sendet, Codex zeigt den Hinweis (nicht installiert)', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  await context.addInitScript(() => localStorage.setItem('pd.termGl', 'false'));   // Text im DOM statt auf der WebGL-Fläche
  for (const c of [PROG_CLAUDE, PROG_CODEX]) {
    const made = await page.request.post('/api/connections', { headers: API, data: c });
    expect(made.status(), c.name).toBe(200);
    expect((await made.json()).program).toBe(c.program);
  }
  // ungültiges Arbeitsverzeichnis: der Server lehnt es mit Meldung ab
  const bad = await page.request.post('/api/connections', { headers: API, data: { ...PROG_CLAUDE, name: 'E2E Böse', workDir: '/tmp; id' } });
  expect(bad.status()).toBe(400);

  await page.goto('/?desktop');
  await expect(tile(page, PROG_CLAUDE.name)).toBeVisible();
  await expect(tile(page, PROG_CLAUDE.name)).toContainText('Claude Code');            // eigene Kachel mit Namen des Programms
  await expect(page.locator('#desktop [data-act="claude"]').first()).toBeVisible();   // Ein-Tipp-Start in den Schnellaktionen

  // Claude: Start im Arbeitsverzeichnis (die Attrappe meldet es), danach ihre Eingabeaufforderung
  await connect(page, PROG_CLAUDE.name, SSH.password);
  const rows = page.locator('.sv-terminal .xterm-rows');
  await expect(rows).toContainText('fake-claude-start cwd=/tmp', { timeout: 30_000 });
  await expect(rows).toContainText('claude>');
  await expect(page.locator('.term-bar')).toBeVisible();                                // bei Programmen ist die Eingabeleiste offen
  for (const key of ['esc', 'shiftTab', 'tab', 'ctrlC', 'ctrlD', 'up', 'down', 'left', 'right', 'pgUp', 'pgDn']) await expect(page.locator(`.term-key[data-key="${key}"]`), key).toBeVisible();

  // Eingabeleiste: Enter sendet, Umschalt+Enter ist eine neue Zeile, der Knopf sendet mehrzeilig, das Feld ist danach leer
  const input = page.locator('.term-input');
  await input.click();
  await page.keyboard.type('hallo welt');
  await page.keyboard.press('Enter');
  await expect(rows).toContainText('echo: hallo welt');
  await expect(input).toHaveValue('');
  await page.keyboard.type('zeile eins');
  await page.keyboard.press('Shift+Enter');
  await page.keyboard.type('zeile zwei');
  await expect(input).toHaveValue('zeile eins\nzeile zwei');
  await page.locator('.term-send').click();
  await expect(rows).toContainText('echo: zeile eins');
  await expect(rows).toContainText('echo: zeile zwei');
  await expect(input).toHaveValue('');

  // Schnelltaste: Strg+D beendet das Programm, die Sitzung meldet das Ende (und es gibt kein stilles Neuverbinden)
  await page.locator('.term-key[data-key="ctrlD"]').click();
  await expect(rows).toContainText('fake-claude-ende');
  await expect(page.locator('.sv-overlay')).toBeVisible();

  // Codex: nicht installiert -> verständlicher Hinweis, es läuft eine normale Shell
  await page.goto('/?desktop');
  await connect(page, PROG_CODEX.name, SSH.password);
  const rows2 = page.locator('.sv-terminal .xterm-rows');
  await expect(rows2).toContainText('codex ist auf diesem Rechner nicht installiert oder nicht im PATH', { timeout: 30_000 });
  await expect(page.locator('.term-banner')).toContainText('codex ist auf diesem Rechner nicht installiert');
  await expect(rows2).toContainText(/[$#] *$/m, { timeout: 30_000 });
  await page.keyboard.type('echo shell-$((6*7))');
  await page.keyboard.press('Enter');
  await expect(rows2).toContainText('shell-42');
  expect(errors).toEqual([]);
  await context.close();
});

test('10 Programm-Kacheln am Handy: Symbol und Schnellstart "Claude" in den Diensten', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser, PHONE);
  await page.goto('/?phone');
  await expect(page.locator('#mobile')).toBeVisible();
  await page.locator('#mobile').getByText('Dienste', { exact: true }).last().click();
  const claudeTile = page.locator('#mobile [data-conn]').filter({ hasText: PROG_CLAUDE.name }).first();
  await expect(claudeTile).toBeVisible();
  await expect(claudeTile).toHaveClass(/k-claude/);
  await expect(page.locator('#mobile [data-conn]').filter({ hasText: PROG_CODEX.name }).first()).toHaveClass(/k-codex/);
  await page.locator('#mobile [data-act="prog"][data-prog="claude"]').click();          // genau eine Kachel: öffnet sie (Passwortdialog)
  await expect(page.locator('#startDlg')).toBeVisible();
  expect(errors).toEqual([]);
  await context.close();
});

test('11 Programm-Sitzung mit tmux: ohne tmux auf dem Ziel läuft eine normale Sitzung mit sichtbarem Hinweis', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  await context.addInitScript(() => localStorage.setItem('pd.termGl', 'false'));
  const made = await page.request.post('/api/connections', { headers: API, data: { ...PROG_CLAUDE, name: 'E2E Claude tmux', keepAlive: true } });
  expect(made.status()).toBe(200);
  expect((await made.json()).keepAlive).toBe(true);
  await page.goto('/?desktop');
  await connect(page, 'E2E Claude tmux', SSH.password);
  const rows = page.locator('.sv-terminal .xterm-rows');
  await expect(rows).toContainText('tmux ist auf diesem Rechner nicht installiert', { timeout: 30_000 });
  await expect(rows).toContainText('fake-claude-start cwd=/tmp');                      // Claude läuft trotzdem, nur ohne tmux
  await expect(rows).toContainText('claude>');
  expect(errors).toEqual([]);
  await context.close();
});
