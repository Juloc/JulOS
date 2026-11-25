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
  `SessionLease` zurück (zum Beenden `Dispose()`), `null` bei erreichter Obergrenze. **Jeder WebSocket-Relay (Stream, native
  SSH) nutzt sie**, damit die Obergrenze für alle Arten zusammen gilt.
- Fehlertext bei Obergrenze: „Es sind schon 8 Sitzungen offen. Schließe eine, um eine neue zu öffnen.“ (Code 797, die Seite zeigt den Text des Servers; die Sitzung schließt sich damit).

## Vertrag `Session` (`sessions.js`)

Jede Sitzungsart ist eine Klasse, die `Session` erweitert (ein `EventTarget`).

```js
class Session extends EventTarget {
  id           // string, sid (siehe oben)
  conn         // { id, name, kind: 'desktop'|'terminal', stored, target, program, workDir } (target = "host:port/user", fuer Schwestersitzungen; program: 'shell'|'claude'|'codex'|'custom', siehe CLI-SESSIONS.md)
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
  es werden keine Größen gemeldet (eine versteckte Bildfläche hat Größe 0 und wird ignoriert), die Fensterleiste fragt nicht ab. Die Ansicht
  schickt dem Server die Nachricht „Pause“: er kodiert nichts, der Browser dekodiert nichts; beim Zurückkommen kommt alles, was sich geändert hat,
  als nächstes Bild.
- **Gemessen** (Test-Instanz, zwei Stream-Sitzungen, beide mit Bewegung auf dem Desktop): Server-CPU mit einer aktiven und einer pausierten
  Sitzung ≈ 16 bis 17 %, genauso viel wie mit einer aktiven allein; mit beiden aktiv ≈ 26 %. Die pausierte Sitzung bekam in 12 s kein Bild
  (0 gegenüber 124 bei der sichtbaren). Hintergrundsitzungen wachsen also nicht mit der Zahl.
- **Fällt eine Sitzung aus** (Netz weg, Ziel weg, Anmeldung abgelehnt), betrifft das nur sie: Überlagerung „Neu verbinden“, Wiederholungen und Zustandspunkt
  sind je Sitzung. Eine Sitzung, die nie verbunden war, schließt sich mit der Fehlermeldung selbst.
- **Zwei RDP-Sitzungen zum selben Ziel und Benutzer:** das Ziel (xrdp, Windows) gibt je Benutzer nur einen Desktop her, die zweite Verbindung übernimmt ihn.
  Die erste zeigt dann „Übernommen“ mit Erklärung (kein Wiederholen, kein „Anmeldung fehlgeschlagen“); „Neu verbinden“ holt den Desktop zurück.
  Dasselbe gilt, wenn die zweite Verbindung von woanders kommt (ein anderes Gerät ohne Übergabe, ein anderes RDP-Programm): beendet das Ziel die Sitzung
  ohne Grund oder mit „eine andere Verbindung hat sie übernommen“, schickt der Server **778** (`ErrorEndedAtTarget`) statt eines stillen Schließens, und der
  Browser verbindet sich nicht von selbst neu (bisher holte er sich den Desktop sofort zurück, und die beiden Geräte warfen sich gegenseitig hinaus).
  Ausnahme wie bisher: kommt das gleich nach dem Anmelden, gilt es als abgelehntes Passwort (Anmeldedialog).
  SSH-Sitzungen zum selben Ziel sind unabhängig voneinander.

## Sitzungserhalt (Stream-Engine)

Bricht nur die Verbindung zwischen Browser und Server ab (Handy wechselt das Netz, Tab im Hintergrund, kurzer Funkloch), bleibt die
**RDP-Sitzung auf dem Server erhalten** und der Browser hängt sich wieder an dieselbe: kein neuer Login am Ziel, Fenster und Zustand auf dem
Desktop bleiben, sofort ein Schlüsselbild. Die Überlagerung zeigt „Verbindung wird wiederhergestellt …“ statt eines Abbruchs.

- **Einstellung:** `JULOS_STREAM_KEEP_SECONDS`, Standard `300`, `0` schaltet es aus (dann endet die Sitzung wie früher mit der Verbindung), höchstens `3600`.
- **Ablauf:** Der Server nennt dem Browser gleich nach dem Verbinden ein zufälliges **Token** (Nachricht 11, 192 Bit). Reißt der WebSocket ab,
  wird die Kodierung pausiert, der Ton aus, Ereignisse werden verworfen; die Sitzung hält ihren Platz in der Obergrenze. Der Browser verbindet
  mit `/api/remote/screen?resume=<Token>` (Anmeldung der App-Sitzung wie immer, kein Ticket, kein Passwort). Ein zweiter Browser mit demselben
  Token löst den ersten ab (der alte Socket ist dann meist schon tot, aber vom Server noch nicht bemerkt).
- **Warum verbindet es neu? (Diagnose):** Das Server-Log nennt bei jedem Abriss die Dauer und den Grund: `Browser weg nach 21 s (Browser schloss selbst (NormalClosure))` kam vom Browser (Seite war im Hintergrund, Neuladen, eigenes Neuverbinden), `(Leitung abgerissen beim Empfangen (WebSocketException))` ohne Schließen vom Netz zwischen Handy und Server, `(abgebrochen, eine neue Verbindung derselben Sitzung kam)` von einem Browser, der sich anhängte, bevor der Server den alten Socket als tot erkannte. Gleichmäßige Abstände sprechen für ein Zeitlimit (Proxy, Mobilfunk), unregelmäßige für das Netz. Mit `?debug` in der Adresse schreibt der Browser dazu `ws-close` (Code, sauber oder abgerissen, wie lange die Verbindung hielt, ob die Seite versteckt war, `navigator.onLine`).
- **Lebenszeichen und stilles Wiederaufnehmen** (`link-health.js`, reine Logik, `tests/frontend/link-health.test.mjs`): Eine Leitung, die still reißt (Router, Mobilfunk-NAT), meldet das
  Betriebssystem erst beim nächsten Schreiben. Darum gibt es ein Lebenszeichen auf Anwendungsebene:
  - **Stream:** der Server nennt es gleich nach dem Verbinden (Nachricht 50 an den Browser); erst dann schickt der Browser Ping (50, Nummer u32), wenn 5 s lang in keiner Richtung etwas floss, und der Server antwortet Pong (51). Kommt nach einem Ping 10 s lang **irgendetwas** nicht, schließt der Browser die Leitung selbst und nimmt die Sitzung wieder auf. Der Server beendet eine Leitung, von der 12 s (Seite im Hintergrund: 90 s, Zeitgeber werden gedrosselt) nichts kam, und hält die Sitzung (Log: `Leitung still: seit 12 s kein Lebenszeichen`). Ein Browser, der nie pingt (alt), wird nie so beendet; ein neuer Browser an einem alten Server sendet nichts, weil Nachricht 50 fehlt.
  - **Terminal:** `Bereit` enthält `link: 1`; Ping ist die alte Nachricht 3 mit angehängter Nummer, Pong ist 4. Gleiche Fristen. Die Shell endet mit der Leitung (außer tmux, dann hängt sich der Browser wieder an).
  - **Zurück aus dem Hintergrund:** statt nach 4 s pauschal neu zu verbinden, geht sofort ein Probe-Ping; kommt binnen 1,2 s etwas, bleibt die Leitung, sonst wird still neu verbunden. Hat der Server das Lebenszeichen nicht (alt), gilt die alte Regel (mehr als 4 s Pause = neu verbinden). Ein Takt, der mehr als 12 s zu spät kommt (Android friert Seiten ein), löst ebenfalls ein Probe-Ping aus; die Zeit zählt über `Date.now()`, nicht über Takte.
  - **Still wiederaufnehmen:** dauert es unter 2,5 s (`QUIET_MS`), bleibt die Ansicht stehen (dieselbe `StreamDisplay`: letztes Bild, Zoom, Lage, Tastaturzustand), es gibt keine Überlagerung und kein Warten auf das erste Bild; nur ein kleines Zeichen „verbinde neu“ in der Statusleiste (auch bei ausgeblendeter Leiste). Nach dem Verbinden fordert der Browser sofort ein Schlüsselbild an. Eingaben in der Lücke werden verworfen (gedrückte Tasten werden losgelassen). Dauert es länger, kommt die bisherige Überlagerung. Das Terminal baut seine Ansicht neu auf (tmux zeichnet sie sofort wieder), auch dort ohne Überlagerung in den ersten 2,5 s.
  - **Sichtbar:** die Statusleiste hat die Zelle „Neuverbindungen“ (Zahl in dieser Sitzung und letzter Grund: Leitung still, vom Server geschlossen, Leitung abgerissen, Seite im Hintergrund), so sieht man, ob es das Netz ist; `?debug` schreibt `ws-close` mit `reason`.
  - **Agenten:** siehe [AGENT_API.md](AGENT_API.md) (Ping alle 5 s, Frist 10 s, gestreutes Neuverbinden, TCP-Keepalive).
- **Was die Sitzung wirklich beendet:** „Schließen“ in der App (Nachricht 17, der Browser sagt es ausdrücklich), der ferne Rechner (Abmelden,
  Ziel weg, von einer anderen Sitzung übernommen), das Ablösen im selben Slot (neuer Login), der Ablauf der Zeit, die **Abmeldung aus der App**
  (wartende Sitzungen prüfen das alle 10 s) und das Herunterfahren des Servers. Ein Neuladen oder Schließen des Tabs beendet sie nicht mehr: die
  Seite kappt nur die Leitung (`RemoteSession.leave`, kein „beenden“), die Sitzung wartet bis zum Ablauf (siehe „Neuladen“ unten).
- **Token abgelehnt** (abgelaufen, Neustart, Sitzung dort beendet, fremder Benutzer): Nachricht 12, der Browser baut ganz normal neu auf (Ticket,
  neuer Login). Unbekannt und fremd sind für den Browser nicht zu unterscheiden. Ist die Ursache eine andere Sitzung zum selben Ziel, zeigt der
  Browser „Übernommen“ und holt den Desktop nicht zurück.
- **Browser:** `resume.js` (reine Logik, `tests/frontend/resume.test.mjs`) entscheidet; ein Token gilt erst, wenn ein Bild gezeichnet wurde (sonst
  wäre ein falsches Passwort eine Endlosschleife). Die Versuche (1 s bis 3 s Abstand) zählen nicht gegen die drei normalen, solange die Sitzung
  wartet (5 s weniger als der Server); das `online`-Ereignis löst sofort einen aus.
- **Sicherheit:** das Token gehört einem Benutzer und gilt nur, solange die Sitzung wartet; es steht in der Adresse des WebSockets (wie das Ticket)
  und nirgends sonst. Der WebSocket braucht weiterhin die App-Sitzung und denselben Ursprung. Ein wartender Desktop läuft weiter: wer ihn
  nicht fünf Minuten offen lassen will, stellt die Zeit kleiner. SSH hat diesen Erhalt nicht; die Arbeit bleibt dort nur mit tmux auf dem Ziel erhalten, und der Browser hängt eine tmux-Sitzung nach einem Abriss nach denselben Fristen von `resume.js` wieder an (Programm-Sitzungen, [CLI-SESSIONS.md](CLI-SESSIONS.md)).

### Neuladen: dieselbe Sitzung zurück (Stream-Engine, [SESSION-KEEP.md](SESSION-KEEP.md), Schritt 1)

Ein Neuladen der Seite war bisher eine **Grenze**: das Token (Nachricht 11) lebt nur im Arbeitsspeicher der Seite, die erhaltene Sitzung blieb verwaist. Jetzt gibt es neben dem Token eine **öffentliche Kennung** (128 Bit, ebenfalls Nachricht 11, hinter dem Token, durch ein Leerzeichen getrennt), die die Seite je Verbindung in `sessionStorage` merkt (je Tab, verschwindet mit dem Tab, kein Geheimnis).

- **Ablauf:** die Seite fragt `GET /api/remote/kept?conn=<Verbindung>`, findet ihre Kennung **ohne Beobachter** und holt sich mit `POST /api/remote/kept/take` ein Einmal-Ticket (20 s, an Person, Sitzung und App-Sitzung gebunden), das der WebSocket einlöst (`?ticket=`). Kein neuer Login am Ziel, kein Passwort, ein Schlüsselbild. Ebenso still kommt eine wartende Sitzung zurück, die **dieses Gerät** (dieselbe App-Sitzung) zuletzt hatte (Tab zu und wieder auf).
- **Übergabe an ein anderes Gerät** (Schritt 3): gibt es zu der Verbindung eine erhaltene Sitzung, die nicht die dieses Tabs ist (ein anderes Gerät zeigt sie gerade, sie wartet seit dem letzten Gerät, ein kopierter Tab), **fragt** die Seite: „Diese Sitzung ist woanders offen … seit 14:02 auf Firefox, Linux“ mit **Hierher holen**, **Neue Sitzung** (beendet die alte zuerst) und **Abbrechen**. Hierher holen schickt `{ id, confirm: true }`; nur damit nimmt der Server eine anderswo angesehene Sitzung. Das alte Gerät bekommt „übernommen“ (777), verbindet sich **nicht** von selbst neu und zeigt **Zurückholen** (dasselbe Nehmen über die Kennung mit Bestätigung, steht im Verlauf; dann hält das andere an). Bei jeder Übernahme stellt der Server ein **neues Token** aus; ein abgelöstes (ein gesperrtes Handy, das die Meldung verpasst hat) bekommt beim nächsten Versuch „übernommen“, nie die Sitzung. Ein einfaches Wiederverbinden behält sein Token. Das Pop-out-Fenster gibt sein neues Token an das Hauptfenster zurück. „Neue Sitzung“ beendet die alte; ein Gerät, das sie gerade zeigt, bekommt **779** („auf einem anderen Gerät beendet“) und baut nicht von selbst neu auf. Vorher baute das zweite Gerät einfach eine neue Sitzung auf, das Ziel gab ihr den Desktop, das erste verband sich neu und nahm ihn zurück: die beiden stritten sich.
- **Beenden:** Abmelden an dem Gerät, das die Sitzung zuletzt hatte, beendet sie sofort (Verlauf `kept.end`); jede andere beendete App-Sitzung („überall abmelden“, Gerät entfernt, Passwort geändert) beendet sie spätestens nach 10 s. „Sitzung schließen“ in der App beendet sie wie immer sofort.
- **Obergrenze:** ein geschlossener Tab lässt die Sitzung warten, jeder neue Tab hat eine neue `sid`. Ist die Obergrenze der Person (8) erreicht, endet die **älteste wartende** Sitzung statt die neue abzulehnen (Verlauf `kept.end`, Grund `limit`); höchstens 4 Sitzungen je Person warten. Angesehene Sitzungen werden nie so beendet. Eine Sitzung, die anderswo angesehen wird, nimmt `POST /api/remote/kept/take` nicht still (nur dieselbe App-Sitzung, etwa ein Neuladen vor dem Bemerken des alten Sockets); der abgelöste Betrachter bekommt „übernommen“ (777). Quick-Connect-Sitzungen haben keine Kennung und stehen in keiner Liste.
- **Beim Parken** lässt der Server am Ziel **alle gedrückten Tasten und Maustasten los** (`ScreenStream.ReleaseInput`), bevor die Kodierung ruht.
- **Grenzen:** die Zeit ist weiter der Wert des Servers für alle (`JULOS_STREAM_KEEP_SECONDS`); ein Tab mit gemerkter Kennung, die ein anderer Tab gerade ansieht (kopierter Tab), fragt nach etwa 2 s; ein Neustart des Servers beendet alles Erhaltene; ein Handy, dessen App beendet wurde, hat keine gemerkte Kennung (Schritt 4).
- **Sicherheit:** wer die App-Sitzung hat (auch ein Dieb des Cookies), kann ein erhaltenes Desktop nehmen; das ist eine Entscheidung des Besitzers ([SECURITY-REVIEW.md](SECURITY-REVIEW.md), F-47).

## Anmeldung in der Sitzung (Stream-Engine)

Lehnt das Ziel die Anmeldung ab oder verlangt es Anmeldedaten, bevor es etwas zeigt, endet die Sitzung nicht mehr mit einer Meldung: der Browser
fragt in der Sitzung nach **Benutzer, Passwort und Domäne** und verbindet noch einmal, das Fenster (Kachel, Tab) bleibt.

- **Fehlercodes** der Nachricht 4: `769` Anmeldung abgelehnt (falsches Passwort, Benutzer unbekannt, am Ziel nicht erlaubt), `770` Anmeldedaten fehlen
  (das Ziel verlangt die Netzwerkanmeldung, und Benutzer oder Passwort waren leer). Der Server unterscheidet beide am `RdpException` (`IsLogonFailure`,
  `IsCredentialsMissing`). Bei xrdp ohne Netzwerkanmeldung gibt es keinen eigenen Code: ein Ziel, das gleich nach dem Verbinden wieder trennt
  (innerhalb von 12 s, solange noch kein Bild da war), gilt wie bisher als abgelehnte Anmeldung (769 im Browser).
- **Ablauf:** der Browser zeigt den Dialog (`RemoteSession.loginDialog` in `app.js`, Entscheidungen in `login.js`, `tests/frontend/login.test.mjs`).
  „Anmelden“ holt ein **neues Ticket** (`POST /api/remote/ticket` mit den neuen Angaben) und öffnet einen neuen WebSocket: derselbe Weg wie beim ersten
  Verbinden, kein neues Verfahren, der alte Slot der Sitzung ist frei (die abgelehnte Verbindung ist am Server schon beendet). Bis zu **drei neue
  Versuche** nach dem ersten Fehlschlag; danach schließt die Sitzung mit „Anmeldung am Ziel abgelehnt“. „Abbrechen“ schließt sie sofort. Hält eine
  Verbindung 12 s, beginnt die Zählung von vorn.
- **Ticket-Felder:** `username` und `domain` (nur für diesen Versuch, ersetzen die der Verbindung, ändern die gespeicherte Verbindung nicht; höchstens
  256 Zeichen, keine Steuerzeichen, sonst 400). Eine leere Domäne ist eine Angabe: die gespeicherte gilt dann nicht. Ohne diese Felder bleibt alles wie
  vorher; auch das Passwort ist weiter Pflicht (400 „Passwort fehlt“), der Dialog verlangt es.
- **Passwort:** bleibt im Speicher der Sitzung (wie das beim ersten Verbinden getippte, für das Wiederverbinden), geht nur im Ticket zum Server, wird nicht
  gespeichert (der Dialog bietet das nicht an; gespeichert wird nur im Verbindungsdialog) und steht nirgends im Log.
- **Nicht dabei:** ein Verbinden ganz ohne Passwort (das Ziel zeigt dann seinen eigenen Anmeldebildschirm) ist weiter nur über die Engine möglich, nicht
  über die Oberfläche.

## Eigenes Fenster (Pop-out)

Auf großen Bildschirmen mit Maus (Desktop-Oberfläche) lässt sich eine Stream-Sitzung in ein **eigenes Browserfenster** lösen: Knopf in der Titelleiste des
Sitzungsfensters oder Menü „<Verbindungsname>“ → „In eigenem Fenster öffnen“. Es gibt **keinen zweiten RDP-Login**: das neue Fenster nimmt dieselbe
Sitzung des Servers mit dem Sitzungs-Token auf (siehe Sitzungserhalt oben, `ScreenRelay`).

- **Ablauf:** Das Hauptfenster (`popout-main.js`, `RemoteSession.popOut` in `app.js`) öffnet `popout.html?c=<Verbindung>&s=<Sitzungskennung>` mit `window.open`
  (direkt aus dem Klick, sonst blockiert der Browser das Fenster; dann kommt eine Meldung), legt Token, Wartezeit, Zoomfaktor, Bildratenstufe und
  Statusleisten-Wahl unter `window.__julosPopouts[sid]` ab und kappt nur seine Leitung (`disconnect(keep)`): der Server hält die Sitzung ohne Browser. Das
  Fenster (`popout.js`) liest den Eintrag aus `window.opener` (gleiche Herkunft) und verbindet mit `?resume=<Token>`. **Das Token steht nie in der Adresse
  des Fensters.** Das Sitzungsfenster im Hauptfenster zeigt „Diese Sitzung läuft in einem eigenen Fenster.“ mit „Wieder anheften“.
- **Wieder anheften:** „Wieder anheften“ im Fenster oder im Hauptfenster, oder das Fenster einfach schließen (auch ein vom Browser beendetes Fenster wird
  erkannt, das Hauptfenster sieht alle 0,6 s nach): das Hauptfenster nimmt die Sitzung mit demselben Token wieder auf, mit sofortigem Schlüsselbild. Die
  Bildratenstufe und die Statusleiste, die im Fenster geschaltet wurden, gelten danach auch dort.
- **Sitzung schließen:** Schließt das Hauptfenster die Sitzung (Fenster zu, „Sitzung beenden“), beendet das Pop-out sie am Server (Nachricht 17) und geht zu.
  Wird das Hauptfenster neu geladen oder geschlossen, während das Pop-out offen ist, läuft die Sitzung im Pop-out weiter; schließt man es dann, endet sie
  (niemand könnte sie wieder aufnehmen).
- **Größe:** Das Fenster bestimmt die Auflösung des fernen Desktops (wie ein Sitzungsfenster, mit demselben Zoomfaktor); Größenänderungen gehen nach 0,35 s
  an den Server (Display Control), ohne neuen Login.
- **Umfang des Fensters:** Bild, Maus, Hardware-Tastatur (physische Tasten als Scancodes, `popout-keys.js`; das Ziel entscheidet mit seiner Belegung, was sie
  schreiben; beim Verlust des Fokus werden gehaltene Tasten losgelassen; Einfügen über die Zwischenablage), Text der Zwischenablage in beide Richtungen, Ton,
  Statusleiste, Bildrate. Touch, Dateiübertragung, Sondertastenleiste, Fensterliste der VM und Mikrofon gibt es nur im Hauptfenster. Zeichen ohne Taste
  (Emoji, Eingabemethoden) lassen sich im Pop-out nicht tippen.
- **Grenzen:** Pro Sitzung ein Pop-out; die Sitzung muss verbunden sein und schon ein Bild gezeigt haben (vorher kann ein Login gescheitert sein). Ein
  Pop-out auf einem anderen Bildschirm behält die Pixeldichte des Fensters, in dem es startet. Auf dem Handy gibt es den Knopf nicht (die Desktop-Oberfläche wird
  dort nicht geladen).
- **Tests:** `tests/frontend/popout.test.mjs` (Tasten, Adresse, Übergabe, blockiertes Fenster, vom Browser beendetes Fenster); die Rückgabe und das
  gleichbleibende Bild prüft der Handtest im Browser (zwei Fenster, ein RDP-Login im Server-Log).

## Grenzen (Stand heute)

- Das Fensterleisten-/Programm-Modus-Backend (Agent) gehört zu genau einer Verbindung: ist für sie ein Agent eingerichtet (er meldet sich vom Ziel aus beim
  Server, siehe AGENT_API.md), zeigt jede Sitzung dieser Verbindung dessen Fenster. Ohne eingerichteten Agenten gilt wie bisher der lokale Socket (derselbe Rechner).
- Die Sitzungen leben im Speicher der Seite: nach einem Neuladen (oder einem beendeten PWA-Prozess) sind sie weg und werden neu geöffnet (die RDP-Sitzung selbst wartet noch bis zum Ablauf, ohne dass die neue Seite sie wieder aufnimmt: das Token steht nur im Speicher).
- Obergrenze pro Benutzer: `JULOS_MAX_SESSIONS_PER_USER` (Standard 8), global 16 je Engine.
