# Changelog

Alle wichtigen Änderungen an JulOS. Das Format folgt [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Versionen
[Semantic Versioning](https://semver.org/lang/de/). Solange die Hauptversion 0 ist, kann sich noch vieles ändern.
Wie eine Version veröffentlicht wird, steht in [docs/RELEASING.md](docs/RELEASING.md).

## [Unveröffentlicht]

### Neu
- Stream-Engine: Vertrauen bei erster Nutzung für das RDP-Zertifikat statt „Zertifikat ignorieren“: der Fingerabdruck wird je Verbindung gemerkt; ändert er sich, kommt vor dem Senden des Passworts eine Warnung mit altem und neuem Fingerabdruck (Abbrechen oder bestätigen). Guacamole verhält sich wie bisher.
- Stream-Engine: auf langsamer Leitung (WLAN, Mobilfunk, WAN) passt sich die Bitrate an die gemessene Liefergeschwindigkeit an und das Fenster der unbestätigten Bilder wird kleiner; Klicks reagieren dort nach etwa 0,3 s statt nach über 1 s.
- Stream-Engine: ruhende Bildteile (Text, Farbe) werden zusätzlich verlustfrei nachgeliefert und liegen pixelgenau über dem Video, farbiger Text ist damit so scharf wie bei Guacamole.

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

[0.1.0]: https://github.com/Juloc/JulOS/releases/tag/v0.1.0
