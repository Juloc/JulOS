# Handy-Test: Größenwechsel, Tastatur, Fensterleiste

Ziel: auf dem echten Handy (Pixel 8 Pro) herausfinden, ob und wann sich der Remote-Bildschirm dauernd in der Größe ändert und die
Taskleiste/Fensterleiste sich falsch verhält. Der Client schreibt dafür nur Messwerte (Zahlen, keine Eingaben) in das Server-Log und zeigt
mit `?debug` ein kleines Messfeld auf dem Bild. Dauert etwa 10 Minuten.

## Wie kommt das Handy an die Test-Instanz?

Die Test-Instanz (`deploy/dev`) lauscht nur auf `127.0.0.1` des PCs. Es wird **nichts** nach außen oder ins Netz geöffnet.

**Empfohlen: USB-Kabel und `adb reverse`.** Das Handy sieht den PC-Port als sein eigenes `localhost`:

1. Am Handy: Einstellungen, Über das Telefon, 7 × auf „Build-Nummer“ tippen, dann Entwickleroptionen, „USB-Debugging“ an. Handy per Kabel an den PC, die Abfrage „USB-Debugging zulassen“ bestätigen.
2. Am PC (die Test-Instanz läuft auf Port 18080; bei einem eigenen Compose-Projekt dessen Port nehmen):

   ```bash
   adb devices
   adb reverse tcp:18080 tcp:18080
   ```

3. Auf dem Handy in Chrome öffnen: `http://localhost:18080/?debug`

Warum so: `localhost` zählt für den Browser als sicherer Kontext. Dadurch gehen WebCodecs (eigene Stream-Engine), Service Worker und
„App installieren“ auch ohne HTTPS. Es gibt keinen offenen Port im Heimnetz, und nach dem Abziehen des Kabels ist alles wieder zu.
Zusatz: in Chrome am PC `chrome://inspect` öffnen, dann sieht man die Konsole des Handys live.

**Nicht empfohlen: ein zeitweiser Port im Heimnetz** (zum Beispiel `192.0.2.10:18080`). Das braucht eine Änderung der Port-Bindung in der
Compose-Datei (nur auf die LAN-Adresse des PCs, nie `0.0.0.0`), eine Firewall-Regel nur für das Handy und gilt nur als `http`: ohne HTTPS
gibt es auf dem Handy **kein WebCodecs** (die Stream-Engine fällt auf Guacamole zurück), keine Installation als App, und das Passwort geht im
Klartext durchs WLAN. Nur wenn USB nicht geht, und dann nur mit einem Wegwerf-Konto und kurz.

Nicht tun: die Test-Instanz über Caddy oder das Internet erreichbar machen.

## Ablauf auf dem Handy

Vorher: einen Wegwerf-Benutzer in der Test-Instanz haben (Einrichtung mit dem Setup-Code aus dem Server-Log, siehe `docs/DEVELOPMENT.md`).
Passwort und Authenticator-Code gibst du selbst ein. Zwei Verbindungen anlegen, die beide auf den Test-Desktop zeigen: eine mit Engine
Guacamole, eine mit Stream.

1. Chrome öffnen, `http://localhost:18080/?debug` aufrufen, anmelden.
2. Die **Guacamole**-Verbindung öffnen. Oben links erscheint ein kleines dunkles Messfeld (Fenstergröße, sichtbarer Bereich, Tastatur,
   Zähler „10 s: …“). Ein **roter Balken** bedeutet: mehr als 12 Größenwechsel in 10 s.
3. Handy 30 Sekunden ruhig halten und beobachten. Die Zahl hinter „10 s:“ muss danach 0 bis 3 sein und das Bild darf nicht flackern.
4. **Tastatur:** drei Mal die Tastatur öffnen und schließen (Tastaturtaste in der Leiste unten). Jeweils danach 3 Sekunden warten.
5. **Drehen:** quer, 5 Sekunden warten, wieder hoch.
6. **Zoom:** Lupe plus und Lupe minus je ein Mal tippen.
7. **Adressleiste:** im normalen Chrome-Tab (nicht als App) zum Tab-Wechsler und zurück gehen oder Chrome kurz in den Hintergrund legen; falls sich die Adressleiste dabei ein- oder ausblendet, 3 Sekunden warten und das Messfeld ansehen.
8. **Programm-Modus:** in der Fensterleiste oben auf „Desktop“ tippen (wird „Apps“), zwei Programme starten und zwischen ihnen wechseln, dann zurück auf „Desktop“.
9. **Sperren:** Handy sperren, 10 Sekunden warten, entsperren.
10. Alles ab Schritt 2 mit der **Stream**-Verbindung wiederholen.
11. Zum Schluss: „App installieren“ in Chrome (geht auch über `localhost`) und die Schritte 2 bis 6 aus der installierten App wiederholen. Dort gibt es kein Messfeld (die App startet ohne `?debug`), erkannte Schleifen stehen aber trotzdem im Server-Log; darum die Uhrzeit notieren.

## Was zurückschicken

- Ein Screenshot des Messfelds (Lauter- und Ausschalttaste zusammen) **direkt nach** jedem Schritt, bei dem etwas auffiel, und bei jedem roten Balken.
- Pro Auffälligkeit: Schritt-Nummer, ungefähre Uhrzeit und ein Satz, was man gesehen hat („Bild sprang“, „Leiste verschwand“, „Bild blieb schwarz“).
- Ob der Fehler mit Guacamole, mit Stream oder mit beiden auftritt, und ob im Browser-Tab oder in der installierten App.

Die Messwerte holt der PC selbst aus dem Server-Log, du musst nichts kopieren:

```bash
docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env logs julos | grep -E '"evt":"(lay|loop|sendSize|remote-size|kbvisible|size-storm)"'
```

## Was die Messwerte heißen

| Eintrag (`evt`) | Inhalt |
| --- | --- |
| `lay` | Zustand nach einer Änderung: `iw`/`ih` Fenster, `vw`/`vh`/`vt` sichtbarer Bereich und Versatz, `sat`/`sar`/`sab`/`sal` Sicherheitsabstände, `sw`/`sh` Bildfläche, `tb` Höhe der Fensterleiste, `dk` Höhe der Leiste unten, `kb` Tastatur offen, `n10` Wechsel in den letzten 10 s |
| `sendSize` | Größe, die an die VM geschickt wurde (`why`: `fit` normal, `nudge` Anstupser beim Start) |
| `remote-size` | Größe, die die VM zurückgemeldet hat |
| `kbvisible` | Tastatur erkannt (auf/zu) |
| `loop` | mehr als 12 Wechsel in 10 s (Art und Anzahl: `send`, `remote`, `stage`, `conn`); wird auch ohne `?debug` geschrieben |
| `size-storm` | die Notbremse hat eine Größe zurückgehalten |

Was der Client dagegen tut (damit die Messwerte richtig gelesen werden): Größen unter 8 × 16 Geräte-Pixel Unterschied werden nicht gesendet,
höchstens eine Änderung pro Sekunde, nichts, während die Tastatur ein- oder ausfährt (rund 0,6 s Ruhe), höchstens 6 Änderungen in 20 s,
und ein Programm-Wechsel in der Fensterleiste ändert die Anzeigegröße erst nach zwei gleichen Abfragen (4 s) und höchstens alle 8 s.

Aufräumen: `adb reverse --remove tcp:18080` und Kabel abziehen.

## Ton und Mikrofon (Stream-Engine)

Noch nicht auf einem echten Handy geprüft; so prüfst du es (Verbindung mit Engine "Stream", Seite über `localhost` per `adb reverse`, weil Ton im Browser und das Mikrofon eine sichere Seite brauchen):

1. Verbinden, Menü (⋮) öffnen: "Ton vom Rechner" steht auf "an" (oder "bitte antippen", dann einmal irgendwo tippen), "Lautstärke" schaltet 100 / 60 / 30 / 0 %.
2. Im Desktop einen Ton abspielen (Terminal: `speaker-test -t sine -f 440 -c 2` oder ein Video). Er soll ohne Knacken und ohne wachsende Verzögerung kommen; mit `?debug` zeigt `__pd.S.client.audio.stats` Puffer in ms (Ziel um 60), Aussetzer und verworfene Samples.
3. Mikrofon im Menü einschalten: der Browser fragt nach der Freigabe, oben erscheint die rote Anzeige "Mikrofon an" (antippen beendet es). Im Desktop eine Aufnahme starten (`parecord`, ein Anruf, Sprachaufnahme): xrdp öffnet den Kanal erst dann, "wartet auf Programm" wechselt zu "nimmt auf".
4. Abgelehnte Freigabe, kein Mikrofon, belegtes Mikrofon: eine deutsche Meldung, kein Absturz. Bildschirm sperren oder Tab verdecken: der Ton pausiert (der Server schickt dann nichts) und läuft beim Zurückkehren weiter.

