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
- Fehlertext bei Obergrenze: „Es sind schon 8 Sitzungen offen. Schließe eine, um eine neue zu öffnen.“ (Code 513).

## Vertrag `Session` (`sessions.js`)

Jede Sitzungsart ist eine Klasse, die `Session` erweitert (ein `EventTarget`).

```js
class Session extends EventTarget {
  id           // string, sid (siehe oben)
  conn         // { id, name, kind: 'desktop'|'terminal', engine, stored }
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
Sessions.activate(id) / Sessions.close(id) / Sessions.closeAll()
Sessions.addEventListener('opened' | 'closed' | 'activated' | 'statechange', e => e.detail.session)
```

Die Hüllen (Handy: Umschalter, Desktop: Fenster) hören auf diese Ereignisse und rufen `mount`/`unmount`/`setVisible` auf. Der Verwalter
selbst kennt kein DOM.
