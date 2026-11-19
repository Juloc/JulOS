# Statusleiste und Bildrate

Jede Stream-Sitzung (RDP über die eigene Engine) kann eine kleine **Statusleiste** zeigen und die **Bildrate** begrenzen. Beides gilt je Sitzung,
nichts davon belastet den Normalfall.

## Statusleiste

Sie zeigt sechs Werte, einmal je Sekunde aktualisiert:

| Wert | Quelle |
|---|---|
| Bilder/s | gezeichnete Bilder in der letzten Sekunde (Zähler im Dekodierer-Ausgang) |
| Reaktion | Zeit von Klick, Taste oder Mausrad bis zum nächsten Bild, Median der letzten neun Eingaben; „–“, wenn seit 20 s keine Eingabe ein Bild ausgelöst hat |
| Daten | empfangene Bytes je Sekunde (alle Nachrichten der Sitzung, auch Nachbesserung und Ton) |
| Auflösung | Größe des Desktops am Ziel |
| Kodierer | was der Server meldet (Nachricht 30), zum Beispiel „OpenH264 2.4.1 (H.264)“ oder ein GPU-Kodierer |
| Bildrate | die gewählte Stufe (siehe unten) |

- **Desktop:** Menü „Ansicht“ → „Statusleiste“. Sie liegt als Fußzeile am unteren Rand des Sitzungsfensters, über dem Bild (die Fläche des Desktops
  wird dadurch nicht kleiner, es gibt keine neue Auflösung).
- **Handy:** Sitzungsmenü → „Leistung anzeigen“; ein kleines Overlay oben links im Bild.
- Die Wahl gilt für alle Sitzungen und wird im Browser gemerkt (`pd.perf`). Im Pop-out-Fenster gibt es denselben Schalter.

**Kosten:** zwei Zähler und ein Zeitstempel je Bild bzw. Nachricht im Client (`perf.js`, `stream.js`), nichts auf dem Server. Gerechnet wird nur,
solange die Leiste sichtbar ist und die Sitzung im Vordergrund liegt (`statusbar.js`, ein Zeitgeber von einer Sekunde, sonst keiner).

**Genauigkeit der Reaktionszeit:** Sie misst, wann nach der Eingabe das erste Bild mit höherer Folgenummer gezeichnet wird. Ein Bild, das der Server schon
vor der Eingabe kodiert hatte und das erst danach ankommt, zählt mit; das gibt in seltenen Fällen etwas zu kurze Werte. Eine Eingabe, auf die kein
Bild folgt (Klick auf eine ruhende Fläche), gibt keinen Wert: nach 1,5 s verfällt sie. Ein Ping ohne Bildänderung wird bewusst nicht gesendet (keine
zusätzliche Last, keine neue Nachricht für etwas, das der Benutzer ohnehin an seiner Eingabe spürt).

## Bildrate wählen

Je Sitzung vier Stufen; **Automatisch** ist der Standard und verhält sich wie bisher (kein festes Intervall, die Leitungsmessung `LinkPacer` und der
Kodierer bestimmen Bitrate und Tempo):

| Stufe | kleinster Abstand zwischen Bildern | Bitrate-Obergrenze |
|---|---|---|
| Automatisch | keiner | konfigurierte Höchstrate (`MaxKilobitsPerSecond` des Ziels) |
| 60 Bilder/s | 16 ms | wie Automatisch |
| 30 Bilder/s | 33 ms | wie Automatisch |
| Sparsam | 66 ms (15 Bilder/s) | 35 % der Höchstrate, mindestens 600 kbit/s |

- **Wo:** Desktop: Menü „Ansicht“ → „Bildrate“; Handy: Sitzungsmenü → „Bildrate“ (tippen schaltet weiter). Die Wahl wird je Verbindung gemerkt
  (`pd.q.<Verbindung>`) und nach jedem Verbinden und Wiederaufnehmen erneut gesendet.
- **Protokoll:** Browser → Server, Nachricht **31** `stufe(1)` (0 automatisch, 1 = 60, 2 = 30, 3 sparsam), siehe `ScreenProtocol.cs`. Unbekannte Stufen und zu
  kurze Nachrichten werden ignoriert. Der Server behält die Stufe bis zur nächsten Nachricht (auch wenn der Browser mit Sitzungserhalt wechselt).
- **Wirkung im Server** (`ScreenStream.SetQuality`, `StreamQualityPolicy`): die Kodierschleife wartet nach einem Bild mindestens den Abstand der Stufe
  (zusätzlich zum Rat des Kodierers, der größere gilt); bei „Sparsam“ geht die Bitrate-Obergrenze entsprechend herunter. Die Stufe ist eine
  **Obergrenze**: die Leitungsmessung darf die Bitrate weiter darunter senken, nie darüber. Schlüsselbilder (erstes Bild, Größenwechsel, Anforderung des
  Browsers) und die Wiederholungen, die ein ruhendes Bild scharf machen, halten den Abstand nicht ein: ein neuer Bildinhalt wartet nie auf die Stufe.
- **Grenzen:** „60“ und „30“ begrenzen nur nach oben; was das Ziel liefert (xrdp meldet in der Praxis rund 25 Aktualisierungen je Sekunde), bleibt die
  obere Schranke. Werden die Bilder des Servers durchgereicht (RDPGFX, `JULOS_RDP_GFX`, siehe [RDPGFX.md](RDPGFX.md)), gibt es nichts zu begrenzen, die Stufe
  hat dann keine Wirkung.
- **Tests:** `tests/JulOS.Display.Tests/Stream/StreamQualityTests.cs` (Protokoll, Zahlen der Stufen, Begrenzung der Bitrate im Stream),
  `tests/frontend/perf.test.mjs` (Zähler, Reaktionszeit, Stufen).
