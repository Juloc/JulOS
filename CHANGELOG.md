# Changelog

Alle wichtigen Änderungen an JulOS. Das Format folgt [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Versionen
[Semantic Versioning](https://semver.org/lang/de/). Solange die Hauptversion 0 ist, kann sich noch vieles ändern.
Wie eine Version veröffentlicht wird, steht in [docs/RELEASING.md](docs/RELEASING.md).

## [Unveröffentlicht]

### Behoben
- Mikrofon: Die Kopfzeile `Permissions-Policy` sperrte das Mikrofon für die Seite (`microphone=()`); sie erlaubt es jetzt für die eigene Seite (`microphone=(self)`), alle anderen Rechte bleiben gesperrt.
- Stream-Engine: Der Ton wird dem RDP-Server jetzt als gewünscht gemeldet. Zuvor ging die Option „kein Ton“ an den Server, was auf Windows-Zielen den Ton verhindern kann (xrdp ignorierte sie).

### Entfernt
- Guacamole entfernt, die Strom-Engine ist der einzige Weg für Desktop-Verbindungen (eigener RDP-Client auf dem Server, H.264 im Browser mit WebCodecs). Weg sind der Relay und das Protokoll für guacd, der JavaScript-Client, die Wahl „Bildübertragung“ im Verbindungsdialog und in den Einstellungen sowie der automatische Rückfall; kann der Browser kein H.264 oder kommt keine Verbindung zustande, zeigt die Sitzung eine klare Meldung mit „Neu verbinden“. Wer einen Desktop aufmacht, ohne dass der Server den Kodierer (libopenh264) laden kann, bekommt die Meldung, dass die Bildübertragung nicht verfügbar ist.
- Für Betreiber: Der `guacd`-Container und alle `JULOS_GUACD*`-Einstellungen werden nicht mehr gebraucht und können aus der Compose-Datei (und aus `.env`) entfernt werden; wer die mitgelieferten Compose-Dateien nutzt, bekommt sie schon ohne guacd. Gespeicherte Verbindungen bleiben unverändert: alte Einträge mit der Engine „guac“ nutzen jetzt ebenfalls den Strom, die Spalte `engine` der Datenbank bleibt als Rest bestehen, wird nicht mehr gelesen und es gibt keine Migration. Das Laufwerk (`JULOS_DRIVE_ROOT`) steht jetzt jeder Desktop-Verbindung offen, die es einschaltet; die Felder `engine` in der Verbindungs-API und im Ticket-Aufruf entfallen (alte Clients dürfen sie weiter mitschicken, sie werden ignoriert).
- Die Hilfswege für Guacamole in der App (Foto über den Helfer, „Bild aus der VM holen“) sind weg, Bilder und Dateien gehen über den Strom; die Agent-Schnittstellen `/api/upload`, `/api/upload-file` und `/api/clipboard/pull` bleiben bestehen.
## [Unreleased]

### Neu
- Tägliches Backup: der Server schreibt die Datenbank (komprimiert) und eine Kopie des Schlüssels für gespeicherte Passwörter als getrennte Dateien in einen Backup-Ordner (`/backup`, im Compose-Beispiel `~/julos-backup`), mit Aufbewahrung (Standard 14 Tage), Uhrzeit und Schalter per Umgebungsvariable (`JULOS_BACKUP`, `JULOS_BACKUP_TIME`, `JULOS_BACKUP_KEEP_DAYS`, `JULOS_BACKUP_KEY`). Ohne eingebundenen Ordner wird nichts geschrieben, sondern laut gewarnt. Kein zusätzlicher Container.
- Wiederherstellung auf der Konsole: `dotnet JulOS.dll backup restore DATEI --key SCHLÜSSELDATEI` (alles oder nichts, prüft am Ende, dass sich gespeicherte Passwörter entschlüsseln lassen); `backup create` sichert sofort. „Über JulOS“ zeigt das letzte Backup mit Zeit und Größe. Anleitung: [docs/BACKUP.md](docs/BACKUP.md).
## [Unreleased]

### Geändert
- Entwicklung: `deploy/dev/e2e.sh` startet einen eigenen Test-Stack und prüft in einem echten Browser (Playwright in Docker, Chromium) sechs Abläufe: Ersteinrichtung und Anmeldung, Verbindungen anlegen, RDP-Stream mit Bild, Klick und Tastatur im Ziel, SSH-Terminal, Handy- und Desktop-Oberfläche ohne Konsolenfehler. Beschreibung in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md), optionaler Schritt vor dem Tag in [docs/RELEASING.md](docs/RELEASING.md).
## [Unreleased]

### Neu
- Module Caddy und DDNS, erste Stufe, nur lesend und standardmäßig aus (`JULOS_CADDY`, `JULOS_DDNS`, siehe [docs/MODULES.md](docs/MODULES.md)): Caddy zeigt „Domain → Ziel“ aus einem nur lesend eingebundenen Caddyfile und/oder der Admin-API (ein fester Abruf), mit einer Erreichbarkeitsprüfung nur der gelisteten Ziele; DDNS vergleicht die öffentliche Adresse (Router oder Lookup-Dienst) mit der Namensauflösung der eingestellten Namen („Synchron“ oder „Nicht synchron“, mit Zeit der Prüfung), ohne Token und ohne zu schreiben. Fenster im Desktop (Dock, Launcher, Menü) und Karten in der Handy-Shell erscheinen nur, wenn der Server das Modul eingeschaltet hat.
## [Unreleased]

### Neu
- Stream-Engine, wahlweise und standardmäßig aus (`JULOS_RDP_GFX=on`): das H.264, das ein RDP-Server durch die Grafik-Pipeline (RDPGFX) schickt, geht ohne neue Kodierung an den Browser (weniger CPU, eine Kodierstufe weniger). Liefert der Server kein passendes H.264, bricht etwas ab oder braucht der Browser ein Schlüsselbild, verbindet dieselbe Sitzung ohne Pipeline neu und kodiert wie bisher selbst. Das xrdp von Debian 13 liefert so kein H.264, Windows-Server (komprimierte Nachrichten) sind nicht unterstützt und nicht geprüft; Einzelheiten in [docs/RDPGFX.md](docs/RDPGFX.md). Gegen einen Server, der H.264 sendet, noch nicht erprobt.
## [Unreleased]

### Neu
- Stream-Engine: Sitzungserhalt. Bricht die Verbindung zum Browser ab (Handy wechselt das Netz, Tab im Hintergrund), bleibt die RDP-Sitzung auf dem Server `JULOS_STREAM_KEEP_SECONDS` Sekunden (Standard 300, 0 = aus) erhalten; der Browser nimmt sie mit einem Sitzungs-Token wieder auf, ohne neuen Login am Ziel und mit sofortigem Schlüsselbild, und zeigt „Verbindung wird wiederhergestellt …“ statt eines Abbruchs. Schließen in der App, Abmelden, Ablösen und Ablauf beenden die Sitzung wirklich.
- Stream-Engine: Laufwerksumleitung (RDPDR). Ein Ordner des Servers (Einstellung `JULOS_DRIVE_ROOT`, aus, wenn leer) kann je Verbindung als Laufwerk in den Desktop gereicht werden; im Verbindungsdialog zwei Schalter, beide aus: „freigeben“ und „Schreiben erlauben“ (ohne den zweiten nur Lesen). Streng abgesichert: fester Wurzelpfad nur aus der Server-Einstellung, nur Windows-Pfade unterhalb der Wurzel, kein `..`, keine versteckten Namen, keine Symlinks, keine Unicode-Tricks, `openat2` mit `RESOLVE_BENEATH`. Siehe [docs/DRIVES.md](docs/DRIVES.md). Auf einem echten Windows-Ziel noch nicht erprobt.

## [0.4.0] - 2026-11-19

### Neu
- Stream-Engine: Vertrauen bei erster Nutzung für das RDP-Zertifikat statt „Zertifikat ignorieren“: der Fingerabdruck wird je Verbindung gemerkt; ändert er sich, kommt vor dem Senden des Passworts eine Warnung mit altem und neuem Fingerabdruck (Abbrechen oder bestätigen). Guacamole verhält sich wie bisher.
- Stream-Engine: auf langsamer Leitung (WLAN, Mobilfunk, WAN) passt sich die Bitrate an die gemessene Liefergeschwindigkeit an und das Fenster der unbestätigten Bilder wird kleiner; Klicks reagieren dort nach etwa 0,3 s statt nach über 1 s.
- Stream-Engine: ruhende Bildteile (Text, Farbe) werden zusätzlich verlustfrei nachgeliefert und liegen pixelgenau über dem Video, farbiger Text ist damit so scharf wie bei Guacamole.

## [0.3.0] - 2026-11-19

### Neu
- Ton aus der VM im Browser und Mikrofon zur VM (Bildstrom-Engine): Ton nur von der gerade aktiven Sitzung, Mikrofon nur auf Knopfdruck mit Browser-Berechtigung und deutlicher Anzeige, höchstens eine Sitzung nimmt auf.
- Der Agent meldet sich von der Ziel-Maschine aus beim Server (WebSocket über HTTPS, ausgehend): Fensterleiste, Programmstart sowie Foto-, Datei- und Zwischenablage-Übertragung gehen jetzt auch, wenn der Server auf einem anderen Rechner läuft (zum Beispiel auf Cubi).
- „Agent einrichten“ und „Agent entfernen“ im Verbindungsdialog: einmaliger Code (10 Minuten) und fertige Befehle für das Ziel; Status je Verbindung (verbunden, getrennt, Version). Der Agent bleibt eine einzige Python-Datei ohne Zusatzpakete; der Unix-Socket-Weg für denselben Rechner funktioniert weiter.
- Der Bildstrom kann auf einer Grafikkarte kodiert werden (VAAPI, zum Beispiel Intel QuickSync), wenn der Container `/dev/dri` sieht; sonst und bei jedem Fehler kodiert wie bisher die CPU (H.264). Dazu ein optionaler H.265-Strom, wenn Browser und Grafikkarte ihn können (`JULOS_ENCODER`, `JULOS_CODEC`); Prüfung mit `dotnet JulOS.dll diag encoders`, Anleitung in [docs/ENCODERS.md](docs/ENCODERS.md). Auf einer echten Grafikkarte noch nicht erprobt.
- Sicherheits-Review mit Bericht in [docs/SECURITY-REVIEW.md](docs/SECURITY-REVIEW.md).

### Geändert
- Die Aufrufe der App an Agent, Foto-Upload, Datei-Upload und „Bild aus der VM holen“ nennen die Verbindung (`?c=`); ohne Angabe gilt wie bisher der lokale Socket.

### Sicherheit
- Anmeldesperre auch für IPv6 pro Netz, Obergrenze für gleichzeitige Anmeldeversuche, begrenzte Größe von API-Anfragen, Grenzen für Uploads und die Dateisuche, Kontingent für Dateien, sichere Downloads (keine Named Pipes), Warnung beim Start ohne Schlüssel für gespeicherte Passwörter.

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

[0.4.0]: https://github.com/Juloc/JulOS/releases/tag/v0.4.0
[0.3.0]: https://github.com/Juloc/JulOS/releases/tag/v0.3.0
[0.2.0]: https://github.com/Juloc/JulOS/releases/tag/v0.2.0
[0.1.0]: https://github.com/Juloc/JulOS/releases/tag/v0.1.0
