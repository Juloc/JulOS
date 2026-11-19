# Changelog

Alle wichtigen Änderungen an JulOS. Das Format folgt [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Versionen
[Semantic Versioning](https://semver.org/lang/de/). Solange die Hauptversion 0 ist, kann sich noch vieles ändern.
Wie eine Version veröffentlicht wird, steht in [docs/RELEASING.md](docs/RELEASING.md).

## [Unreleased]

### Neu
- Der Agent meldet sich von der Ziel-Maschine aus beim Server (WebSocket über HTTPS, ausgehend): Fensterleiste, Programmstart sowie Foto-, Datei- und Zwischenablage-Übertragung gehen jetzt auch, wenn der Server auf einem anderen Rechner läuft.
- „Agent einrichten“ und „Agent entfernen“ im Verbindungsdialog: einmaliger Code (10 Minuten) und fertige Befehle für das Ziel; Status je Verbindung (verbunden, getrennt, Version).
- Der Agent bleibt eine einzige Python-Datei ohne Zusatzpakete; der Unix-Socket-Weg für denselben Rechner funktioniert weiter.

### Geändert
- Die Aufrufe der App an Agent, Foto-Upload, Datei-Upload und „Bild aus der VM holen“ nennen die Verbindung (`?c=`); ohne Angabe gilt wie bisher der lokale Socket.

## [0.2.0] - 2026-11-19

### Neu
- Mehrere Sitzungen gleichzeitig: beliebig viele RDP- und SSH-Verbindungen nebeneinander (Desktop: ein Fenster je Sitzung, Handy: Sitzungswechsler), pro Nutzer standardmäßig bis zu 8.
- Natives SSH-Terminal im Browser (xterm.js, kein Bildstrom): Auswahl, Kopieren und Einfügen, Scrollen, Größenänderung, Tastenleiste auch am Handy.
- Hostschlüssel werden beim ersten Verbinden gemerkt und danach geprüft; ein geänderter Schlüssel braucht eine ausdrückliche Bestätigung.
- Je Sitzung eigenes Wiederverbinden bei Verbindungsabbruch; Hintergrund-Sitzungen pausieren den Bildstrom.

### Geändert
- Wird dasselbe RDP-Ziel ein zweites Mal geöffnet, zeigt die ältere Sitzung „übernommen“ statt eines falschen Anmeldefehlers.
- Die Fernsteuerung in `app.js` ist in Sitzungsobjekte aufgeteilt (`sessions.js`).

## [0.1.0] - 2026-11-19

Erste Version unter eigener Zählung (davor gab es Vorabversionen `1.0.0-beta.1` bis `1.0.0-beta.4`).

### Neu
- Selbst gehosteter Arbeitsplatz im Browser: Desktop (RDP) und Terminal (SSH) gespeicherter Verbindungen, Anmeldung mit Passwort und Authenticator.
- Eigene Bildschirm-Engine (RDP-Client und H.264 im Browser) neben Guacamole, pro Verbindung wählbar, mit automatischem Rückfall auf Guacamole.
- Zwischenablage mit Text, Bildern und Dateien in beide Richtungen.
- Handy-Oberfläche im Material-3-Stil (Home, Dienste, Profil) und Desktop-Oberfläche mit Fenstern, oberer Menüleiste und Dock (drei Modi).
- Tasten gehen im Desktop-Layout immer an das aktive Fernfenster; Kombinationen, Zoom und Fenster-Modus liegen in den Menüs der oberen Leiste.
- Systemübersicht (CPU, Arbeitsspeicher, Speicher, Netz) und Dateien (nur lesen) für den Server.
- „Über JulOS“ mit Version, Build und Lizenzen (Desktop: Hilfe, Handy: Profil).

### Behoben
- Der Bildstrom konnte nach einigen hundert Bildern stehen bleiben; Bestätigungen laufen jetzt über Folgenummern, dazu ein Wachhund.
- Am PC wird das Bild 1:1 gezeichnet statt hochskaliert (schärfer).

[0.2.0]: https://github.com/Juloc/JulOS/releases/tag/v0.2.0
[0.1.0]: https://github.com/Juloc/JulOS/releases/tag/v0.1.0
