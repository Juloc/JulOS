# Mehrere Sitzungen gleichzeitig

JulOS kann mehrere Fernsteuer-Sitzungen (RDP, SSH) gleichzeitig offen halten, auch mehrere zum selben Ziel. Diese Seite ist der
Vertrag zwischen der Oberfläche (Handy- und Desktop-Hülle), dem Sitzungsverwalter und den Sitzungsarten (Fernsteuerung in `app.js`,
das native Terminal in `terminal-session.js`).

## Begriffe

- **Sitzung** = eine geöffnete Ansicht auf ein Ziel. Sie hat eine **Sitzungskennung** (`sid`), die der Browser beim Öffnen erzeugt
  (zufällig, `[A-Za-z0-9_-]{8,40}`). Ein Neuladen oder Wiederverbinden *derselben* Sitzung nimmt dieselbe `sid`.
- **Slot** auf dem Server = Benutzer + Verbindung + `sid`. Ein neuer WebSocket mit demselben Slot löst nur den alten *dieses* Slots ab
  (der alte bekommt „übernommen“, Status 777); alle anderen Sitzungen bleiben unberührt.
- **Obergrenzen:** pro Benutzer `JULOS_MAX_SESSIONS_PER_USER` (Standard 8) und global je Engine 16. Eine Sitzung, die einen vorhandenen
  Slot ersetzt, zählt nicht doppelt.

## Server

- `POST /api/remote/ticket` nimmt zusätzlich `"sid"` (optional; fehlt sie, gilt wie bisher ein Slot je Verbindung).
- `SessionRegistry` (`JulOS.Integrations/SessionRegistry.cs`) verwaltet die Slots: `TryOpen(userId, slot, onReplaced)` gibt eine
  `SessionLease` zurück (zum Beenden `Dispose()`), `null` bei erreichter Obergrenze. **Jeder WebSocket-Relay (Guacamole, Stream, native
  SSH) nutzt sie**, damit die Obergrenze für alle Arten zusammen gilt.
- Fehlertext bei Obergrenze: „Es sind schon 8 Sitzungen offen. Schließe eine, um eine neue zu öffnen.“ (Code 797, die Seite zeigt den Text des Servers; die Sitzung schließt sich damit).

## Vertrag `Session` (`sessions.js`)

Jede Sitzungsart ist eine Klasse, die `Session` erweitert (ein `EventTarget`).

```js
class Session extends EventTarget {
  id           // string, sid (siehe oben)
  conn         // { id, name, kind: 'desktop'|'terminal', engine, stored, target } (target = "host:port/user", fuer Schwestersitzungen)
  kind         // 'desktop' | 'terminal'
  title        // Anzeigename ("Server" oder "Server 2" bei der zweiten Sitzung zum selben Ziel)
  state        // 'connecting' | 'connected' | 'reconnecting' | 'disconnected' | 'error' | 'taken'
  stateText    // kurzer deutscher Text für Karten, z. B. 'Verbunden', 'Verbinde …', 'Getrennt'
  element      // Wurzelelement, gehört der Sitzung (enthält alles, auch eigene Leisten und Überlagerungen)

  mount(container)    // element in container einhängen; es füllt den Container (position/size liegen beim Container)
  unmount()           // element entfernen; die Sitzung läuft weiter
  connect(password)   // starten oder neu starten (Promise; Fehler erscheinen als state, nicht als Ausnahme)
  disconnect()        // beenden, das Objekt bleibt (kann wieder connect())
  resize()            // Container hat eine neue Größe (idempotent, die Sitzung drosselt selbst)
  focus() / blur()    // Tastatur geht an diese Sitzung / nicht mehr
  setVisible(bool)    // false = im Hintergrund: teure Arbeit pausieren (Zeichnen, Dekodieren, Abfragen); true = wieder sichtbar
  dispose()           // disconnect() + DOM und Zeitgeber aufräumen
}
```

Ereignisse (`CustomEvent`, Daten in `detail`):

| Ereignis | detail | Bedeutung |
|---|---|---|
| `statechange` | `{ state, text }` | Zustand oder Zustandstext hat sich geändert (Punkt auf der Karte) |
| `titlechange` | `{ title }` | Titel hat sich geändert |
| `closerequest` | `{ message, reason }` | Die Sitzung will geschlossen werden (zum Beispiel „Verbindung beenden“, Anmeldung abgelehnt, Sitzung der App abgelaufen: `reason: 'auth'`). Der Verwalter schließt sie und zeigt `message`. |

Regeln:

1. Eine Sitzung fasst nie eine andere an und liest keinen globalen Zustand der Seite für „die“ Sitzung.
2. Tastatur: nur die aktive (`focus()`) Sitzung bekommt Tasten. Hört eine Sitzung selbst auf Tastaturereignisse, dann nur auf ihrem `element`.
3. Hintergrund (`setVisible(false)`) heißt: weiter verbunden, aber nichts Teures. Beim Zurückkommen (`setVisible(true)`) muss das Bild
   sofort stimmen.
4. Einstellungen je Verbindung (Zoom, Größe) liegen in `localStorage` unter dem Schlüssel der **Verbindung** (`conn.id`), nicht der Sitzung.
5. Verbindungsverlust, Wiederverbinden und die Überlagerung „Neu verbinden“ sind Sache der einzelnen Sitzung.

## Verwalter `Sessions` (`sessions.js`)

```js
Sessions.registerFactory(kind, (conn, opts) => new MySession(conn, opts))   // pro Art; 'desktop' in app.js, 'terminal' in terminal-session.js
Sessions.open(conn, { password, extra })   // legt eine Sitzung an (extra: true = bewusst eine zweite zum selben Ziel); sonst wird eine vorhandene aktiviert
Sessions.list() / Sessions.get(id) / Sessions.find(connId) / Sessions.active
Sessions.activate(id, { reveal }) / Sessions.close(id, message, reason) / Sessions.closeAll()
Sessions.requestSwitcher() / Sessions.requestHome()          // aus einer Sitzung heraus: Umschalter zeigen / zur Startseite
Sessions.addEventListener('opened' | 'closed' | 'activated' | 'statechange' | 'switcher' | 'home', e => e.detail.session)
```

- `activate` meldet nur einen Wechsel; mit `reveal: true` (die Sitzung soll gezeigt werden, auch wenn sie schon die aktive ist, zum Beispiel nach
  „Zur Startseite“) immer. Beim Schließen der aktiven Sitzung wird die zuletzt benutzte aktiv (ohne `reveal`).
- Die Hüllen (Handy: Umschalter, Desktop: Fenster) hören auf diese Ereignisse und rufen `mount`/`unmount`/`setVisible` auf. Der Verwalter
  selbst kennt kein DOM. Die Verdrahtung steht in `shell.js` (Ereignisse -> `desktop.js` oder `mobile.js`).

## Oberfläche

- **Handy:** Ab zwei Sitzungen steht oben in der Fernsteuerung eine Leiste (Startseite, ein Chip je Sitzung mit Zustandspunkt, „Alle Sitzungen“).
  Ein Tipp auf einen Chip wechselt. Das Sheet „Sitzungen“ (Leiste oder Menü ⋯) zeigt Karten mit Zustand, Wechsel mit einem Tipp, ein Kreuz schließt.
  Auf der Startseite steht „Offene Sitzungen“, Kacheln zeigen die Zahl offener Sitzungen. Eine Verbindung, die schon offen ist, wird nur nach vorn
  geholt; „Weitere Sitzung“ gibt es im Menü der Kachel (gedrückt halten) und im Menü ⋯ einer Sitzung. Die Zurück-Geste in der Fernsteuerung geht zur
  Startseite, die Sitzungen bleiben offen.
- **Desktop (breiter Bildschirm mit Maus):** jede Sitzung ist ein eigenes Fenster (Zustandspunkt in der Titelleiste), neue Fenster versetzt, im Menü
  *Desktop* „Sitzungen nebeneinander“ und „überlappend“, in der Menüleiste ein Menü der offenen Sitzungen. Die Tastatur gehört dem Fenster mit Fokus
  (Klick ins Fenster oder in seine Titelleiste). Ein minimiertes Fenster ist im Hintergrund.

## Hintergrund und Ausfälle

- **Im Hintergrund** (andere Sitzung vorn, Fenster minimiert, Seite versteckt, Startseite offen): `setVisible(false)`. Die Ansicht ist ausgeblendet,
  es werden keine Größen gemeldet (eine versteckte Bildfläche hat Größe 0 und wird ignoriert), die Fensterleiste fragt nicht ab. Die Stream-Engine
  schickt dem Server die Nachricht „Pause“: er kodiert nichts, der Browser dekodiert nichts; beim Zurückkommen kommt alles, was sich geändert hat,
  als nächstes Bild. Guacamole bleibt verbunden (es gibt keine Pause im Protokoll), die Ansicht ist nur ausgeblendet.
- **Gemessen** (Test-Instanz, zwei Stream-Sitzungen, beide mit Bewegung auf dem Desktop): Server-CPU mit einer aktiven und einer pausierten
  Sitzung ≈ 16 bis 17 %, genauso viel wie mit einer aktiven allein; mit beiden aktiv ≈ 26 %. Die pausierte Sitzung bekam in 12 s kein Bild
  (0 gegenüber 124 bei der sichtbaren). Hintergrundsitzungen wachsen also nicht mit der Zahl.
- **Fällt eine Sitzung aus** (Netz weg, Ziel weg, Anmeldung abgelehnt), betrifft das nur sie: Überlagerung „Neu verbinden“, Wiederholungen und Zustandspunkt
  sind je Sitzung. Eine Sitzung, die nie verbunden war, schließt sich mit der Fehlermeldung selbst.
- **Zwei RDP-Sitzungen zum selben Ziel und Benutzer:** das Ziel (xrdp, Windows) gibt je Benutzer nur einen Desktop her, die zweite Verbindung übernimmt ihn.
  Die erste zeigt dann „Übernommen“ mit Erklärung (kein Wiederholen, kein „Anmeldung fehlgeschlagen“); „Neu verbinden“ holt den Desktop zurück.
  SSH-Sitzungen zum selben Ziel sind unabhängig voneinander.

## Grenzen (Stand heute)

- Das Fensterleisten-/Programm-Modus-Backend (Agent) gehört zu genau einer Verbindung: ist für sie ein Agent eingerichtet (er meldet sich vom Ziel aus beim
  Server, siehe AGENT_API.md), zeigt jede Sitzung dieser Verbindung dessen Fenster. Ohne eingerichteten Agenten gilt wie bisher der lokale Socket (derselbe Rechner).
- Die Sitzungen leben im Speicher der Seite: nach einem Neuladen (oder einem beendeten PWA-Prozess) sind sie weg und werden neu geöffnet.
- Obergrenze pro Benutzer: `JULOS_MAX_SESSIONS_PER_USER` (Standard 8), global 16 je Engine.
