import { test, expect, chromium } from '@playwright/test';
import { existsSync } from 'node:fs';
import { AUTH_FILE, RDP, SSH, VNC, STATE_FILE, loadState, markStep, newPassword, nextCode, saveState, totp } from './helpers.mjs';

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
  // Fenster merken aus: die Ablaeufe starten sonst mit den Fenstern des vorigen Tests (die Wiederherstellung selbst wird von Hand im Browser geprueft)
  await context.addInitScript(() => localStorage.setItem('pd.dk.ws.remember', 'false'));
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
    saveState({ user, password, secret });
    markStep(Math.floor(Date.now() / 30000) + 1);                       // verbraucht: der Schritt der Anmeldung (und alle davor)
    expect(errors.filter((e) => !/401|403/.test(e))).toEqual([]);
  });
});

const API = { 'X-JulOS': '1', Origin: 'http://127.0.0.1:8080' };   // so erkennt der Server Aufrufe der eigenen Seite (Fremd-Ursprung wird abgelehnt)
const DESKTOP_CONN = { name: 'E2E Desktop', kind: 'desktop', host: RDP.host, user: RDP.user };
const SSH_CONN = { name: 'E2E Terminal', kind: 'terminal', host: SSH.host, port: SSH.port, user: SSH.user };

/** Gibt die Sitzung der Seite fuer Aktionen frei (zweiter Faktor): ein Code im naechsten freien Zeitschritt. Die Anmeldung aus Test 1 gilt nur 10 Minuten. */
async function approve(page) {
  const r = await page.request.post('/api/auth/step-up', { headers: API, data: { code: await nextCode(loadState().secret) } });
  expect(r.status()).toBe(200);
}

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
    { name: 'phone', url: '/?phone', options: PHONE, root: '#mobile', tile: '#mobile [data-conn]:visible', before: (page) => page.locator('#mobile').getByText('Verbindungen', { exact: true }).last().click() },
    { name: 'desktop', url: '/?desktop', options: DESKTOP, root: '#desktop', tile: '#desktop .dk-tile' },
  ];
  // The phone's Home page can hold a tile of the same connection (a desktop kept from an earlier test is "live" there): take the visible one.
  for (const c of checks) {
    const { context, page, errors } = await signedIn(browser, c.options);
    await page.goto(c.url);
    await expect(page.locator(c.root), c.name).toBeVisible();
    if (c.before) await c.before(page);                            // Handy: die Verbindungen stehen unter "Verbindungen"
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

test('8 Zwischenablage: Text aus der VM im Browser, Markieren mit Umschalt+Pfeilen und Strg+C in einem echten Fenster, Strg+V mit Zeilenumbruch in die VM, Diagnose @full', async ({ browser }) => {
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
  expect(errors).toEqual([]);
  await context.close();
});

test('9 VNC: verbinden (Passwort im Dialog der Sitzung), Bild, Klick und Taste erreichen das Ziel @full', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  const sockets = watchSockets(page);
  const conn = { name: 'E2E VNC', kind: 'vnc', host: VNC.host, user: '' };
  await page.goto('/');
  for (const c of (await (await page.request.get('/api/connections')).json()).connections.filter((x) => x.name === conn.name)) await page.request.delete('/api/connections/' + c.id, { headers: API });
  await page.reload();
  await addConnection(page, conn);
  await expect(tile(page, conn.name)).toContainText(VNC.host);
  await tile(page, conn.name).click();

  // Der Server verlangt ein Passwort: die Sitzung fragt in einem Dialog, nur nach dem Passwort (ein VNC-Server kennt keinen Benutzer)
  const login = page.locator('.term-dialog input[name=pw]');
  await expect(login).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.term-dialog input[name=user]')).toHaveCount(0);
  await login.fill('falsch123');
  await page.keyboard.press('Enter');
  await expect(page.locator('.term-dialog p', { hasText: /abgelehnt|rejected/ }).first()).toBeVisible({ timeout: 30_000 });    // abgelehnt: der Dialog kommt noch einmal, mit dem Hinweis
  await page.locator('.term-dialog input[name=pw]').fill(VNC.password);
  await page.keyboard.press('Enter');
  await waitForPicture(page, sockets);

  // Klick und Taste: das Fuehlerfenster im Desktop (300 x 300 bei 600,400) protokolliert, was bei ihm ankommt
  const read = async () => (await fetch(VNC.probe + '/input.log')).text();
  const shape = await page.locator('.sv-display canvas').first().evaluate((c) => ({ w: c.width, h: c.height }));
  const box = await page.locator('.sv-display canvas').first().boundingBox();
  const at = (x, y) => [box.x + (x * box.width) / shape.w, box.y + (y * box.height) / shape.h];
  const [cx, cy] = at(750, 550);
  await page.mouse.move(cx, cy);
  await page.mouse.click(cx, cy);
  await expect.poll(read, { message: 'der Klick kam im Fuehlerfenster an', timeout: 15_000 }).toContain('button 1,');
  await page.waitForTimeout(300);
  await page.keyboard.type('j');
  await expect.poll(read, { message: 'die Taste kam im Fuehlerfenster an', timeout: 15_000 }).toContain('keysym 0x6a');
  // Ton und Mikrofon: VNC kennt keins von beiden, und das Menü "Ton" sagt es, statt Knöpfe anzubieten, die nichts bewirken
  await page.locator('.dk-mi[data-menu="sound"]').click();
  await expect(page.locator('.dk-menu .dk-it')).toHaveCount(1);
  await expect(page.locator('.dk-menu')).toContainText('VNC überträgt keinen Ton und kein Mikrofon');
  await page.keyboard.press('Escape');
  expect(errors.filter((e) => !/401|403/.test(e))).toEqual([]);
  await context.close();
});

const PROG_CLAUDE = { name: 'E2E Claude', kind: 'terminal', host: SSH.host, port: Number(SSH.port), username: SSH.user, program: 'claude', workDir: '/tmp' };
const PROG_CODEX = { name: 'E2E Codex', kind: 'terminal', host: SSH.host, port: Number(SSH.port), username: SSH.user, program: 'codex' };

/** Räumt Verbindungen früherer Versuche weg (ein Wiederholungsversuch legt sie sonst doppelt an, und "Claude" fände zwei Kacheln). */
async function dropProgramConnections(page) {
  const list = (await (await page.request.get('/api/connections')).json()).connections;
  for (const c of list.filter((x) => /^E2E (Claude|Codex)/.test(x.name))) await page.request.delete(`/api/connections/${c.id}`, { headers: API });
}

test('10 Programm-Sitzungen: Claude startet im Arbeitsverzeichnis, die Eingabeleiste sendet, Codex zeigt den Hinweis (nicht installiert) @full', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  await context.addInitScript(() => localStorage.setItem('pd.termGl', 'false'));   // Text im DOM statt auf der WebGL-Fläche
  await dropProgramConnections(page);
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
  // Ersthinweis zur Anmeldung (einmal, bis "Verstanden"), dann wenige Tasten in der Zeile und der Rest im Blatt "Mehr Tasten"
  await expect(page.locator('.term-tip')).toContainText('meldest du dich');
  await page.locator('.term-tip .term-act.primary').click();
  await expect(page.locator('.term-tip')).toHaveCount(0);
  for (const key of ['esc', 'shiftTab', 'tab', 'ctrlC', 'up', 'down']) await expect(page.locator(`.term-bar .term-key[data-key="${key}"]`), key).toBeVisible();
  await page.locator('.term-more-btn').click();
  for (const key of ['esc', 'shiftTab', 'tab', 'ctrlC', 'ctrlD', 'up', 'down', 'left', 'right', 'pgUp', 'pgDn']) await expect(page.locator(`.term-more .term-key[data-key="${key}"]`), key).toBeVisible();
  await page.locator('.term-more .term-more-done').click();
  await expect(page.locator('.term-more')).toHaveCount(0);

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
  await page.locator('.term-more-btn').click();
  await page.locator('.term-more .term-key[data-key="ctrlD"]').click();
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

test('11 Programm-Kacheln am Handy: Symbol in den Verbindungen, Schnellstart "Claude" auf der Start-Seite @full', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser, PHONE);
  await page.goto('/?phone');
  await expect(page.locator('#mobile')).toBeVisible();
  await page.locator('#mobile').getByText('Verbindungen', { exact: true }).last().click();
  const claudeTile = page.locator('#mobile [data-conn]').filter({ hasText: PROG_CLAUDE.name }).first();
  await expect(claudeTile).toBeVisible();
  await expect(claudeTile).toHaveClass(/k-claude/);
  await expect(page.locator('#mobile [data-conn]').filter({ hasText: PROG_CODEX.name }).first()).toHaveClass(/k-codex/);
  await page.locator('#mobile').getByText('Home', { exact: true }).last().click();
  await page.locator('#mobile [data-act="prog"][data-prog="claude"]').click();          // Start-Seite, Programme: genau eine Kachel öffnet sie (Passwortdialog)
  await expect(page.locator('#startDlg')).toBeVisible();
  expect(errors).toEqual([]);
  await context.close();
});

test('12 Programm-Sitzung mit tmux: ohne tmux auf dem Ziel läuft eine normale Sitzung mit sichtbarem Hinweis @full', async ({ browser }) => {
  const { context, page, errors } = await signedIn(browser);
  await context.addInitScript(() => localStorage.setItem('pd.termGl', 'false'));
  await dropProgramConnections(page);
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

/* ---------- Ton und Mikrofon (Chromium mit Ersatzgeräten: das Mikrofon liefert einen Testton, die Freigabe wird ohne Rückfrage erteilt) ---------- */
// Nur für diese Abläufe: --use-fake-device-for-media-stream (Mikrofon ohne Hardware), --use-fake-ui-for-media-stream (die Freigabe erscheint nicht als Frage),
// --autoplay-policy=no-user-gesture-required (der AudioContext startet ohne Eingabe; das Verhalten ohne Eingabe prüfen die Tests im Browser-Modul)
const FAKE_MEDIA = ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required'];
/** Der kleine Dienst im Ziel für Ton (deploy/dev/rdp-target/clip-probe.py): Ton abspielen, Mikrofon aufnehmen und auswerten */
const tone = (seconds = 3) => probe(`/tone?s=${seconds}`, { method: 'POST' });
const recording = async () => (await probe('/record-level')).json();
/** Zustand von Ton und Mikrofon der aktiven Sitzung aus dem Browser (window.__pd gibt es nur mit ?debug) */
const audioState = (page) => page.evaluate(() => {
  const a = window.__pd.cur().client.audio;
  return { state: a.state, wanted: a.wanted, active: a.active, remote: { ...a.remote }, stats: { ...a.stats }, micOn: a.micOn, micChunks: a.micChunks, micLevel: a.micLevel, lastOn: a.lastOn };
});
const soundMenu = (page) => page.locator('.dk-mi[data-menu="sound"]');
const menuItem = (page, name) => page.locator('.dk-menu .dk-it', { hasText: name });

/** Öffnet die Sitzung des RDP-Ziels in einem eigenen Chromium und wartet auf das Bild; die Seite lädt mit ?debug */
async function openDesktopSession(browser, { microphone = false } = {}) {
  const s = await signedIn(browser, DESKTOP);
  if (microphone) await s.context.grantPermissions(['microphone'], { origin: 'http://127.0.0.1:8080' });     // der Zustand der Freigabe ist dann "erteilt"

  const sockets = watchSockets(s.page);
  await s.page.goto('/?debug');
  await connect(s.page, DESKTOP_CONN.name, RDP.password);
  await waitForPicture(s.page, sockets);
  await s.page.bringToFront();
  return s;
}

test('13 Ton: ein Ton im Desktop erreicht den Browser, der Worklet spielt ihn (Pegel, Proben), die Diagnose sagt es; die Sitzung im Hintergrund bekommt nichts @full', async () => {
  test.setTimeout(120_000);
  const browser = await chromium.launch({ args: FAKE_MEDIA });
  try {
    const { context, page, errors } = await openDesktopSession(browser);

    // 1) Die Verbindung hat den Ton-Kanal ausgehandelt und der AudioContext läuft; ohne Ton im Desktop kommt nichts
    await expect.poll(async () => { const a = await audioState(page); return a.state === 'running' && a.wanted && a.remote.output; }, { message: 'Ton-Kanal ausgehandelt, AudioContext läuft', timeout: 40_000 }).toBe(true);
    const idle = await audioState(page);
    expect(idle.lastOn, 'der Server hat "Ton an" gehört').toBe(true);

    // 2) Ein Ton im Desktop (paplay in der Sitzung): Blöcke kommen an, der Worklet gibt Proben aus, der Pegel entspricht dem Testton (12000 von 32768, etwa 0,37)
    let peak = 0, start = await audioState(page), last = start;
    await expect.poll(async () => {
      if (!(await page.evaluate(() => window.__toneUntil > Date.now()))) { await tone(3); await page.evaluate(() => { window.__toneUntil = Date.now() + 2500; }); }
      last = await audioState(page); peak = Math.max(peak, last.stats.peak);
      return last.stats.played - start.stats.played > 24000 && peak > 0.2;
    }, { message: 'Ton aus dem Desktop wird abgespielt', timeout: 60_000, intervals: [150] }).toBe(true);
    expect(peak, 'Pegel nahe am Testton').toBeLessThan(0.6);
    expect(last.stats.blocks - last.stats.ignored, 'Tonstücke kamen an').toBeGreaterThan(10);
    expect(last.stats.received, 'der Worklet bekam Proben').toBeGreaterThan(24000);

    // 3) Die Diagnose im Dialog zeigt Kanal, Wiedergabe und Pegel
    await soundMenu(page).click();
    await menuItem(page, 'Ton und Mikrofon: Diagnose').click();
    const diag = page.locator('#audioDiagList');
    await expect(diag).toContainText('Ton-Kanal des Desktops: vorhanden');
    await expect(diag).toContainText('Wiedergabe: läuft');
    await expect(diag).toContainText(/Vom Desktop angekommen: \d+ Stücke/);
    await expect(diag).toContainText(/Pegel \d+ %, Puffer/);
    await page.evaluate(() => document.querySelector('#audioDlg').close());

    // 4) Das Fenster wird minimiert (die Sitzung ist dann nicht vorn): der Server hört "Ton aus", im Browser kommt trotz Ton im Desktop nichts an
    await page.getByRole('dialog', { name: DESKTOP_CONN.name }).getByRole('button', { name: 'Minimieren' }).click();
    await expect.poll(async () => (await audioState(page)).active, { message: 'die Sitzung im Hintergrund ist nicht mehr aktiv', timeout: 10_000 }).toBe(false);
    expect((await audioState(page)).lastOn, 'der Server hört "Ton aus"').toBe(false);
    await page.waitForTimeout(500);
    const hidden0 = await audioState(page);
    await tone(2);
    await page.waitForTimeout(2500);
    const hidden1 = await audioState(page);
    expect(hidden1.stats.received, 'im Hintergrund wurde kein Ton abgespielt').toBe(hidden0.stats.received);
    expect(hidden1.stats.blocks, 'im Hintergrund schickte der Server nichts').toBe(hidden0.stats.blocks);

    // 5) Wieder nach vorn: der Ton kommt wieder an
    await page.locator('#dkSess').click();                                                  // Menü der offenen Sitzungen: die minimierte holt das Fenster zurück
    await menuItem(page, DESKTOP_CONN.name).click();
    await expect.poll(async () => (await audioState(page)).active, { message: 'die Sitzung ist wieder vorn', timeout: 10_000 }).toBe(true);
    const back0 = await audioState(page);
    await expect.poll(async () => { await tone(2); await page.waitForTimeout(1500); return (await audioState(page)).stats.received > back0.stats.received + 8000; }, { message: 'Ton kommt wieder an', timeout: 30_000 }).toBe(true);

    expect(errors.filter((e) => !/401|403/.test(e))).toEqual([]);
    await context.close();
  } finally { await browser.close(); }
});

test('14 Mikrofon: im Menü einschalten, Chromium liefert den Testton, im Desktop nimmt ein Programm auf und hört ihn; ausschalten beendet es @full', async () => {
  test.setTimeout(120_000);
  const browser = await chromium.launch({ args: FAKE_MEDIA });
  try {
    const { context, page, errors } = await openDesktopSession(browser, { microphone: true });
    await expect.poll(async () => (await audioState(page)).state, { message: 'Ton-Kanal steht', timeout: 40_000 }).toBe('running');

    // Im Desktop nimmt ein Programm auf (parecord in der Sitzung): so öffnet xrdp den Mikrofon-Kanal
    await expect.poll(async () => (await probe('/record-start', { method: 'POST' })).status, { message: 'im Desktop läuft eine Aufnahme', timeout: 40_000, intervals: [500] }).toBe(200);
    await soundMenu(page).click();
    await menuItem(page, /^Mikrofon/).click();
    await expect.poll(async () => (await audioState(page)).micOn, { message: 'das Mikrofon des Browsers läuft', timeout: 15_000 }).toBe(true);
    await expect(page.locator('#micLive')).toBeVisible();                                  // die rote Anzeige "Mikrofon an"
    await expect.poll(async () => { const a = await audioState(page); return a.remote.microphone && a.remote.recording; }, { message: 'der Desktop bietet das Mikrofon an und verlangt Ton', timeout: 40_000, intervals: [300] }).toBe(true);
    await expect.poll(async () => (await audioState(page)).micChunks, { message: 'der Browser schickt Mikrofon-Stücke', timeout: 15_000 }).toBeGreaterThan(50);

    // Die Aufnahme im Desktop enthält den Ton (Pegel über der Schwelle, nicht nur Stille)
    let level = { samples: 0, loud: 0, peak: 0 };
    await expect.poll(async () => { level = await recording(); return level.loud; }, { message: 'die Aufnahme im Desktop enthält Ton', timeout: 40_000, intervals: [500] }).toBeGreaterThan(1500);
    expect(level.peak, 'Pegel der Aufnahme').toBeGreaterThan(4000);

    // Die Diagnose nennt Kanal, Aufnahme und Freigabe
    await soundMenu(page).click();
    await menuItem(page, 'Ton und Mikrofon: Diagnose').click();
    const diag = page.locator('#audioDiagList');
    await expect(diag).toContainText('Mikrofon des Desktops: Kanal angeboten');
    await expect(diag).toContainText('Mikrofon: an, der Desktop nimmt auf');
    await expect(diag).toContainText('Mikrofon-Freigabe: erteilt');
    await page.evaluate(() => document.querySelector('#audioDlg').close());

    // Ausschalten (auch über die rote Anzeige): die Spur des Browsers endet
    await page.locator('#micLive').click();
    await expect.poll(async () => (await audioState(page)).micOn, { message: 'das Mikrofon ist aus', timeout: 10_000 }).toBe(false);
    await expect(page.locator('#micLive')).toBeHidden();
    await probe('/record-stop', { method: 'POST' });
    expect(errors.filter((e) => !/401|403/.test(e))).toEqual([]);
    await context.close();
  } finally { await browser.close(); }
});

test('15 Mikrofon verweigert: der Browser lehnt die Freigabe ab, die Sitzung nennt eine Anleitung für den Browser, das Mikrofon bleibt aus @full', async () => {
  test.setTimeout(120_000);
  const browser = await chromium.launch({ args: ['--use-fake-device-for-media-stream'] });
  try {
    const { context, page, errors } = await openDesktopSession(browser);
    await expect.poll(async () => (await audioState(page)).remote.output, { message: 'Ton-Kanal steht', timeout: 40_000 }).toBe(true);
    // Der Browser verweigert das Mikrofon für diese Seite (wie nach "Blockieren" in der Frage): über das Protokoll des Browsers gesetzt, der Kontext des Tabs ergibt sich aus dessen Ziel
    const cdp = await browser.newBrowserCDPSession();
    const target = (await cdp.send('Target.getTargets')).targetInfos.find((x) => x.type === 'page' && x.url.includes('127.0.0.1'));
    await cdp.send('Browser.setPermission', { permission: { name: 'microphone' }, setting: 'denied', origin: 'http://127.0.0.1:8080', browserContextId: target.browserContextId });
    await soundMenu(page).click();
    await menuItem(page, /^Mikrofon/).click();
    const toast = page.locator('.sv-toast');
    await expect(toast).toContainText('Der Browser hat das Mikrofon nicht freigegeben.', { timeout: 15_000 });
    await expect(toast).toContainText('Schloss oder Regler');                               // die Anleitung für Chromium
    expect((await audioState(page)).micOn).toBe(false);
    await expect(page.locator('#micLive')).toBeHidden();
    await soundMenu(page).click();
    await menuItem(page, 'Ton und Mikrofon: Diagnose').click();
    await expect(page.locator('#audioDiagList')).toContainText('Mikrofon: aus');
    await expect(page.locator('#audioDiagList')).toContainText('Mikrofon-Freigabe: verweigert. Klicke links neben der Adresse');
    await page.evaluate(() => document.querySelector('#audioDlg').close());
    expect(errors.filter((e) => !/401|403|NotAllowed|Permission/.test(e))).toEqual([]);
    await context.close();
  } finally { await browser.close(); }
});

test('16 Host-Login: einmal am Host setzen, die Dateien öffnen ohne Passwortfrage, eine Verbindung mit eigenem Benutzer bleibt davon unberührt @full', async ({ browser }) => {
  test.setTimeout(180_000);
  const { context, page, errors } = await signedIn(browser);
  await approve(page);                                                   // der Host-Login ist eine Aktion mit zweitem Faktor
  const wrong = 'falsch-' + newPassword();
  // Ein eigener Host mit einer Terminal-Verbindung ohne Benutzer und Passwort (Wegwerf-Ziel des Stacks); die Verbindungen der anderen Abläufe bleiben unberührt
  const host = await (await page.request.post('/api/hosts', { headers: API, data: { name: 'E2E Login-Host' } })).json();
  const conn = await (await page.request.post('/api/connections', { headers: API, data: { name: 'E2E Login-Terminal', kind: 'terminal', host: SSH.host, port: Number(SSH.port), username: '', hostId: host.id } })).json();
  const open = (data) => page.request.post('/api/files/remote/open', { headers: API, data });
  try {
    // Ohne Login fragt die Dateien-App nach dem Passwort
    const before = await open({ connection: conn.id });
    expect(before.status()).toBe(409);
    expect((await before.json()).code).toBe('password_required');

    // Der Login entsteht auf der Host-Seite (Karte "Login"): Benutzer und Passwort, das Passwortfeld ist leer und bleibt es
    await page.goto('/');
    await page.locator('.dk-nav', { hasText: 'Hosts' }).click();
    await page.locator('.hu-card', { hasText: 'E2E Login-Host' }).getByRole('button', { name: 'Details' }).click();
    await page.locator('#hit-login').click();
    await expect(page.locator('#hostLoginPw')).toHaveValue('');
    await expect(page.locator('#hostLoginState')).toContainText('noch keinen Login');
    await expect(page.locator('.hu-note', { hasText: 'sudo -i' })).toBeVisible();                       // Root: der Agent erledigt es, ein Root-Terminal ist sudo -i
    await page.fill('#hostLoginUser', SSH.user);
    await page.fill('#hostLoginPw', SSH.password);
    await page.click('[data-hl-save]');
    await expect(page.locator('#hostLoginState')).toContainText('Passwort gespeichert');
    await expect(page.locator('#hostLoginPw')).toHaveValue('');
    expect(await page.content()).not.toContain(SSH.password);                                              // nirgends in der Seite
    await expect(page.locator('#hip-login')).toContainText('nutzt den Host-Login');
    await page.screenshot({ path: '/out/host-login-card.png' });

    // Jetzt öffnet die Dateien-App ohne Frage: nur der Host-Schlüssel wird (wie immer) ausdrücklich bestätigt
    const key = await open({ connection: conn.id });
    expect((await key.json()).code).toBe('host_key_new');
    const k = await (await open({ connection: conn.id })).json();
    const opened = await open({ connection: conn.id, acceptHostKey: `${k.algorithm} ${k.fingerprint}` });
    expect(opened.status()).toBe(200);
    const list = await page.request.get(`/api/files/remote/list?c=${conn.id}`);
    expect(list.status()).toBe(200);
    expect((await list.json()).path).toContain('/');

    // Das Passwort kommt in keiner Antwort vor
    const bodies = [await (await page.request.get(`/api/hosts/${host.id}/login`)).text(), await (await page.request.get('/api/connections')).text(), await (await page.request.get('/api/hosts')).text()];
    for (const b of bodies) expect(b).not.toContain(SSH.password);
    const mine = (await (await page.request.get('/api/connections')).json()).connections.find((c) => c.id === conn.id);
    expect([mine.loginSource, mine.loginUser, mine.loginPassword]).toEqual(['host', SSH.user, true]);

    // Eine Verbindung mit eigenem Benutzer (und falschem Passwort) benutzt den Host-Login nicht: die Anmeldung scheitert dort
    const own = await (await page.request.post('/api/connections', { headers: API, data: { name: 'E2E Eigener Login', kind: 'terminal', host: SSH.host, port: Number(SSH.port), username: SSH.user, password: wrong, ownLogin: true, hostId: host.id } })).json();
    expect(own.loginSource).toBe('own');
    const k2 = await (await open({ connection: own.id })).json();
    const refused = await open({ connection: own.id, acceptHostKey: `${k2.algorithm} ${k2.fingerprint}` });
    expect(refused.status()).not.toBe(200);
    expect((await refused.json()).code).toMatch(/logon_failed|password_required/);

    // Den Login ändern beendet die gepoolte Sitzung, entfernen bringt die Frage zurück
    await page.click('[data-hl-remove]');
    await page.click('[data-hl-remove]');
    await expect(page.locator('#hostLoginState')).toContainText('noch keinen Login');
    const again = await open({ connection: conn.id });
    expect(again.status()).toBe(409);
    expect((await again.json()).code).toBe('password_required');
    expect(errors.filter((e) => !/401|403|409/.test(e))).toEqual([]);
  } finally {
    for (const c of (await (await page.request.get('/api/connections')).json()).connections.filter((x) => x.hostId === host.id)) await page.request.delete('/api/connections/' + c.id, { headers: API });
    await page.request.delete('/api/hosts/' + host.id, { headers: API });
    await context.close();
  }
});

test('17 Host-Login gilt auch für den Desktop: eine RDP-Verbindung ohne Benutzer und Passwort verbindet ohne Frage mit dem Login des Hosts @full', async ({ browser }) => {
  test.setTimeout(120_000);
  const { context, page, errors } = await signedIn(browser);
  const sockets = watchSockets(page);
  await approve(page);
  const host = await (await page.request.post('/api/hosts', { headers: API, data: { name: 'E2E Login-Desktop' } })).json();
  const conn = await (await page.request.post('/api/connections', { headers: API, data: { name: 'E2E Login-Desktop', kind: 'desktop', host: RDP.host, username: '', hostId: host.id } })).json();
  try {
    const login = await page.request.put(`/api/hosts/${host.id}/login`, { headers: API, data: { user: RDP.user, password: RDP.password } });
    expect(login.status()).toBe(200);
    await page.goto('/');
    await tile(page, conn.name).click();                                                                   // ohne Passwortdialog: der Server nimmt den Login des Hosts
    await expect(page.locator('#startDlg')).toBeHidden();
    await waitForPicture(page, sockets);
    expect(errors).toEqual([]);
  } finally {
    await page.request.delete('/api/connections/' + conn.id, { headers: API });
    await page.request.delete('/api/hosts/' + host.id, { headers: API });
    await context.close();
  }
});

test('18 Zweiter Faktor: bei "immer fragen" öffnet eine Aktion das Sheet, ein falscher Code bleibt davor stehen, der richtige führt die Aktion aus', async ({ browser }) => {
  test.setTimeout(180_000);
  const { context, page, errors } = await signedIn(browser);
  await approve(page);
  const set = await page.request.put('/api/security/step-up', { headers: API, data: { enabled: true, minutes: 0 } });     // gilt sofort, auch fuer die eben erteilte Freigabe
  expect(set.status()).toBe(200);
  try {
    // ohne Freigabe verweigert der Server den Weg mit einem festen Code
    const refused = await page.request.put('/api/security/hosts', { headers: API, data: { entries: [] } });
    expect(refused.status()).toBe(403);
    expect((await refused.json()).code).toBe('step_up_required');

    await page.goto('/?desktop');
    await page.locator('#desktop [data-go="einstellungen"], #desktop [data-nav="einstellungen"]').first().click();
    await page.locator('#desktop [data-act="security"]').click();
    await expect(page.locator('#securityDlg')).toBeVisible();
    await expect(page.locator('#securityDlg h3', { hasText: 'Vertraute Proxy-Adressen' })).toBeVisible();
    await expect(page.locator('#securityDlg h3', { hasText: 'Zweiter Faktor für Aktionen' })).toBeVisible();
    await page.screenshot({ path: '/out/security-page.png' });

    await page.click('#secBody [data-sec="save-step"]');                    // die Aktion kommt zurueck mit 403, das Sheet fragt nach dem Code
    await expect(page.locator('#stepUpDlg')).toBeVisible();
    await page.fill('#suCode', '000000');
    await page.click('#suForm button[type=submit]');
    await expect(page.locator('#suMsg')).not.toBeEmpty();                  // falscher Code: bleibt offen
    await expect(page.locator('#stepUpDlg')).toBeVisible();
    await page.screenshot({ path: '/out/step-up-sheet.png' });
    await page.fill('#suCode', await nextCode(loadState().secret));
    await page.click('#suForm button[type=submit]');
    await expect(page.locator('#stepUpDlg')).toBeHidden();
    await expect(page.locator('#secBody .msg.ok', { hasText: 'Gespeichert' })).toBeVisible();   // die Aktion wurde danach wiederholt
    await page.locator('#securityDlg [data-act="close"]').click();
  } finally {
    await context.close();                                                // der Stack ist wegwerf: "immer fragen" bleibt bis zum naechsten frischen Lauf
  }
  expect(errors.filter((e) => !/401|403/.test(e))).toEqual([]);          // der falsche Code (401) und die Aufforderung (403) stehen als Konsolenmeldung des Browsers
});

test('19 Zwischenablage: zwanzigmal nacheinander kopieren und einfügen, es kommt nie der alte Text an (die Taste folgt der Annahme der VM) @full', async ({ browser }) => {
  test.setTimeout(120_000);
  const { context, page, errors } = await signedIn(browser);
  await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: 'http://127.0.0.1:8080' });
  const sockets = watchSockets(page);
  await page.goto('/');
  const id = await connectionId(page, DESKTOP_CONN.name);
  await connect(page, DESKTOP_CONN.name, RDP.password);
  await waitForPicture(page, sockets);
  await page.bringToFront();
  await expect.poll(async () => { const r = await page.request.get('/api/agent/health?c=' + id); return r.ok() && (await r.json()).available; }, { message: 'der Agent im Ziel meldet sich', timeout: 40_000 }).toBe(true);
  await probe('/entry', { method: 'POST', body: '' });
  const entryWindow = async () => (await windowsOf(page, id)).find((w) => w.title === 'clip-entry');
  await expect.poll(async () => !!(await entryWindow()), { message: 'das Eingabefenster der VM erscheint', timeout: 20_000 }).toBe(true);
  await page.request.post(`/api/agent/windows/${(await entryWindow()).id}/activate`, { headers: API });
  await expect.poll(async () => (await entryWindow())?.active, { message: 'das Eingabefenster hat den Fokus', timeout: 10_000 }).toBe(true);
  const entryText = async () => { const r = await probe('/entry-text'); return r.ok ? await r.text() : null; };
  // der erste Einfuegevorgang wartet, bis der Kanal der VM bereit ist
  await page.evaluate(() => navigator.clipboard.writeText('start'));
  await page.keyboard.press('Control+v');
  await expect.poll(entryText, { timeout: 30_000, intervals: [300] }).toBe('start');
  let expected = 'start';
  const stamp = Date.now().toString(36);
  for (let i = 1; i <= 20; i++) {
    const text = `[${i}-${stamp}]`;
    await page.evaluate((v) => navigator.clipboard.writeText(v), text);
    await page.keyboard.press('Control+v');
    expected += text;
    await expect.poll(entryText, { message: `Einfügen ${i} bringt genau den neuen Text`, timeout: 6_000, intervals: [100] }).toBe(expected);
  }
  await probe('/entry-close', { method: 'POST' });
  expect(errors).toEqual([]);
  await context.close();
});

test('20 Terminal: ein Bild und eine Datei mit Leerzeichen und Anführungszeichen aus der Zwischenablage landen im Ordner der Shell, der Pfad steht gequotet auf der Zeile, ohne Enter; mit ausgeschaltetem Schalter kommt nichts hoch @full', async ({ browser }) => {
  test.setTimeout(150_000);
  const { context, page, errors } = await signedIn(browser);
  await context.addInitScript(() => localStorage.setItem('pd.termGl', 'false'));
  await approve(page);
  expect((await page.request.put('/api/security/files-write', { headers: API, data: { enabled: false } })).status()).toBe(200);
  await page.goto('/');
  await connect(page, SSH_CONN.name, SSH.password);
  const rows = page.locator('.sv-terminal .xterm-rows');
  await expect(rows).toContainText(/[$#] *$/m, { timeout: 30_000 });
  // die Shell meldet ihren Ordner (OSC 7), dann in einen Ordner mit Leerzeichen und Hochkomma wechseln
  await page.keyboard.type(`PROMPT_COMMAND='printf "\\e]7;file://%s%s\\a" "$HOSTNAME" "$PWD"'`);
  await page.keyboard.press('Enter');
  await page.keyboard.type(`mkdir -p "$HOME/it's here" && cd "$HOME/it's here" && echo ready-$((6*7))`);
  await page.keyboard.press('Enter');
  await expect(rows).toContainText('ready-42');
  const pasteFiles = (files) => page.evaluate((specs) => {
    const dt = new DataTransfer();
    for (const s of specs) dt.items.add(new File([new Uint8Array(s.bytes)], s.name, { type: s.type }));
    const ta = document.querySelector('.sv-terminal .xterm-helper-textarea');
    ta.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
  }, files);
  const png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];

  // Schreiben aus: ein Dialog erklärt es, nichts wird hochgeladen, nichts steht auf der Zeile
  await pasteFiles([{ name: 'image.png', type: 'image/png', bytes: png }]);
  await expect(page.getByText('Schreiben ist ausgeschaltet').first()).toBeVisible({ timeout: 15_000 });
  await page.locator('button:visible', { hasText: 'Schließen' }).last().click();

  await approve(page);
  expect((await page.request.put('/api/security/files-write', { headers: API, data: { enabled: true } })).status()).toBe(200);
  // ein Bild (heißt paste-<Zeit>.png) und eine Datei mit Leerzeichen und Hochkomma
  await pasteFiles([{ name: 'image.png', type: 'image/png', bytes: png }, { name: "mein Bild's $x.txt", type: 'text/plain', bytes: [104, 105] }]);
  // der Pool hat für diese Verbindung noch keine Sitzung und kein gespeichertes Passwort: dieselbe Frage wie in der Dateien-App (die Eingabe ist das Wegwerf-Passwort des Stacks)
  const pw = page.locator('input[type=password]:visible').last();
  if (await pw.waitFor({ state: 'visible', timeout: 8_000 }).then(() => true, () => false)) {      // beim zweiten Versuch des Tests steht die Sitzung schon im Pool
    await pw.fill(SSH.password);
    await page.getByRole('button', { name: 'Verbinden' }).last().click();
  }
  const confirm = page.getByRole('button', { name: 'Hochladen' }).last();
  await expect(confirm).toBeVisible({ timeout: 15_000 });
  await confirm.click();
  await expect(page.getByText(/2 Dateien hochgeladen|hochgeladen/).first()).toBeVisible({ timeout: 30_000 });
  await page.locator('button:visible', { hasText: 'Schließen' }).last().click();
  // die Zeile trägt beide Pfade, gequotet; Enter hat der Mensch noch nicht gedrückt
  await expect(rows).toContainText(/paste-\d{8}-\d{6}\.png/, { timeout: 10_000 });
  await expect(rows).toContainText("'\\''");
  await page.locator('.sv-terminal .xterm-helper-textarea').focus();
  await page.keyboard.press('Control+u');
  await page.keyboard.type('ls -1 "$HOME/it\'s here" | tr "\\n" "|"; echo end-$((1+1))');
  await page.keyboard.press('Enter');
  await expect(rows).toContainText(/paste-\d{8}-\d{6}\.png\|/);
  await expect(rows).toContainText("mein Bild's $x.txt");
  expect(errors.filter((e) => !/409/.test(e))).toEqual([]);        // 409: die erwartete Passwortfrage des Pools (password_required), die Seite fragt danach
  await context.close();
});

test('21 Übergabe: ein zweites Gerät fragt, holt den Desktop hierher, das erste hält an und holt ihn zurück; keins verbindet sich von selbst neu', async ({ browser }) => {
  // Gerät 1: die Anmeldung aus Test 1 (wartet dort noch ein Desktop dieses Geräts, kommt er still zurück)
  const one = await signedIn(browser);
  const sockets1 = watchSockets(one.page);
  await one.page.goto('/');
  await connect(one.page, DESKTOP_CONN.name, RDP.password);
  await waitForPicture(one.page, sockets1);

  // Gerät 2: eigene Anmeldung, also eine andere App-Sitzung
  const { user, password, secret } = loadState();
  const context2 = await browser.newContext({ ...DESKTOP, baseURL: 'http://127.0.0.1:8080' });
  await context2.addInitScript(() => localStorage.setItem('pd.dk.ws.remember', 'false'));
  expect((await context2.request.post('/api/auth/login', { headers: API, data: { user, password, code: await nextCode(secret) } })).status()).toBe(200);
  const page2 = await context2.newPage();
  const errors2 = watchErrors(page2), sockets2 = watchSockets(page2);
  await page2.goto('/');
  await connect(page2, DESKTOP_CONN.name, RDP.password);
  await expect(page2.locator('.term-dialog').filter({ hasText: 'woanders offen' })).toBeVisible();
  await page2.getByRole('button', { name: 'Hierher holen' }).click();
  await waitForPicture(page2, sockets2);

  const overlay1 = one.page.locator('.dk-win .sv-overlay').first();
  await expect(overlay1).toBeVisible();
  await expect(overlay1).toContainText('anderen Gerät');
  await expect(overlay1.locator('.sv-reconnect')).toHaveText('Zurückholen');
  await one.page.waitForTimeout(4000);
  await expect(overlay1, 'Gerät 1 holt nicht von selbst zurück').toBeVisible();
  await expect(page2.locator('.dk-win .sv-overlay').first(), 'Gerät 2 behält den Desktop').toBeHidden();

  // Zurückholen auf Gerät 1: Gerät 2 hält an und bietet seinerseits "Zurückholen"
  await overlay1.locator('.sv-reconnect').click();
  await expect(overlay1).toBeHidden({ timeout: 20_000 });
  const overlay2 = page2.locator('.dk-win .sv-overlay').first();
  await expect(overlay2).toContainText('anderen Gerät');
  await page2.waitForTimeout(4000);
  await expect(overlay1, 'Gerät 1 behält den Desktop').toBeHidden();
  await expect(overlay2).toBeVisible();

  expect([...one.errors, ...errors2].filter((e) => e.startsWith('pageerror'))).toEqual([]);
  await context2.close();
  await one.context.close();
});
