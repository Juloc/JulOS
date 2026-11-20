# JulOS

**Deine Rechner, deine Programme, deine Dateien: ein persoenlicher Arbeitsbereich, in der Hosentasche oder in jedem Browser.**
Selbst gehostet und Open Source. Was wir jetzt bauen: [docs/SCOPE.md](docs/SCOPE.md). Das groessere Bild: [docs/VISION.md](docs/VISION.md). Geparkte Ideen: [docs/IDEAS.md](docs/IDEAS.md) (alle Englisch).

Das erste Stueck ist ein Fernzugriff, gebaut fuer das **Handy**: deine normale Android-Tastatur (Vorschlaege,
Autokorrektur, Bilder), eine anpassbare Sondertasten-Leiste, eine Fensterleiste in der App statt der Taskleiste,
dynamische Aufloesung, Material-You-Farben und installierbare PWA. Darunter ist es normaler Fernzugriff (RDP oder VNC, von der eigenen Anzeige-Engine als H.264 gestreamt)
auf den eigenen Rechner, nichts liegt in einer Cloud.

[English version](README.md)

```
Handy (PWA)  --HTTPS-->  dein Reverse-Proxy  -->  JulOS-Server (.NET, eigene Anmeldung, RDP-Client + H.264-Kodierer)  --RDP-->  dein Rechner
                                                         |--> SSH-Client (Terminal, Text wird im Browser gezeichnet)  --SSH-->  dein Rechner
                                                         |--> PostgreSQL (Konten, Sitzungen, gespeicherte Verbindungen)
                                                         \<-- Agent in der Sitzung (meldet sich von dort aus, oder Unix-Socket auf demselben Rechner): Fenster, Programme, Foto-Paste
```

Stand: erst Linux (X11, XFCE getestet). Windows ist eingeplant, die Agent-API ist dafuer ausgelegt ([docs/AGENT_API.md](docs/AGENT_API.md)).

## Funktionen

- **Android-Tastatur wie sie ist**: jede Aenderung (Autokorrektur, Vorschlag ersetzt ein Wort) wird als Rueckschritte plus neuer Text gesendet.
  Modi: mit Vorschlaegen, ohne Vorschlaege (Passwortfeld), Rich (nimmt Bilder, Sticker, GIFs aus der Tastatur an).
- **Sondertasten**: zwei einklappbare Zeilen (schnell: Esc, Tab, Strg, Alt, Shift, Pfeile, Enter, Backspace; mehr: Strg+C/V/X/A/Z/F,
  Alt+Tab, Pos1, Ende, Bild auf/ab, ...). Strg/Alt/Shift rasten ein. Bearbeiten: Tasten hinzufuegen, loeschen, zwischen Zeilen
  verschieben, eigene Kombis (`ctrl+shift+t`), Textbausteine.
- **Fensterleiste und Programm-Modus**: Umschalter *Desktop* / *Apps*. Im Programm-Modus ist die Leiste der Sitzung ausgeblendet,
  Fenster erscheinen als Chips (mit Symbol) in der App, "+" startet jedes installierte Programm. Fenster werden maximiert.
  Zoom-Profil je Programm (zum Beispiel schmale Breite fuer Programme mit responsivem Layout).
- **Foto einfuegen**: Foto waehlen, wird verkleinert, hochgeladen, in die Remote-Zwischenablage gelegt und mit Strg+V eingefuegt.
- **Passwortmanager**: Anmeldeformular und Passwort-Dialog sind fuer Autofill erkennbar (Bitwarden usw.).
- **Touch**: Tippen = Klick, Halten = Rechtsklick, Ziehen, Zwei-Finger-Scrollen, Pinch-Zoom (Aufloesung passt sich an), Touchpad-Modus.
- **Deutsch und Englisch:** die ganze Oberfläche im Browser gibt es in beiden Sprachen (Auswahl „Deutsch / English / System“ in den Einstellungen, je Benutzer auf dem Server gespeichert; Standard ist Deutsch). Siehe [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#languages).
- **Mehrere Sitzungen gleichzeitig** (bis zu 8 je Benutzer, RDP und SSH, auch zwei zum selben Ziel): am Handy eine Leiste mit einem Chip je Sitzung (Zustandspunkt) und ein Sheet mit Karten zum Wechseln und Schliessen; am grossen Bildschirm ist jede Sitzung ein eigenes Fenster, die Tastatur gehoert nur dem Fenster mit Fokus. Sitzungen im Hintergrund laufen weiter, aber ohne teure Arbeit. Siehe [docs/SESSIONS.md](docs/SESSIONS.md).
- **Statusleiste, Bildrate und Pop-out** (Desktop und Handy): je Stream-Sitzung eine optionale Statusleiste (Bilder pro Sekunde, Reaktionszeit, Datenrate, Auflösung, Kodierer), eine Bildrate je Sitzung (automatisch, 60, 30, sparsam) und am großen Bildschirm lässt sich eine Sitzung ohne zweiten Login in ein eigenes Browserfenster lösen. Siehe [docs/STATUSBAR.md](docs/STATUSBAR.md) und [docs/SESSIONS.md](docs/SESSIONS.md).
- **PWA**: installierbar (braucht HTTPS), Standalone/Vollbild, Offline-Huelle. Material-You-Palette aus Grundfarbe oder Bild.
- **Neu verbinden** mit Animation, Auto-Tastatur bei Textfeldern, Layout folgt dem sichtbaren Viewport.
- **Dateien auf jedem Host** (Desktop-Fenster und Handy-Seite "Dateien"): der gemeinsame Ordner des Servers und jeder Host mit SSH-Verbindung, per SFTP mit den Zugangsdaten, die du an der Verbindung gespeichert hast (kein Root, kein Agent). Die App arbeitet wie ein Explorer: Einfachklick waehlt, Doppelklick oeffnet, Auswahl mit Umschalt, Strg und Rahmen, Buchstabensprung, Rechtsklick-Menue, Ziehen, Ausschneiden, Kopieren und Einfuegen (auch zwischen Hosts), ein Baum der Hosts mit angehefteten Ordnern, kompakte Liste mit Endungen, ausgegraute versteckte Dateien und sortierbaren Spalten, dazu ein einfacher Texteditor. Herunterladen, hochladen (auswaehlen oder hineinziehen), neuer Ordner oder neue Textdatei, umbenennen, kopieren, verschieben (auch von Host zu Host ueber den Server) und loeschen; jede Aenderung wird in der App bestaetigt und in einem Verlauf festgehalten. Lesen geht sofort; **Schreiben ist aus, bis du `JULOS_FILES_WRITE=on` setzt**. Der Host-Schluessel muss bestaetigt werden, Links werden nie verfolgt, nichts liegt im Speicher, Downloads erscheinen nie als Seite. Siehe [docs/FILES.md](docs/FILES.md) (englisch).

## Schnellstart

Voraussetzungen auf dem Zielrechner: Linux mit X11, **xrdp** (und xorgxrdp), `python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0`.
Auf dem Docker-Host (darf derselbe Rechner sein): Docker mit Compose.

```bash
git clone <dein Fork oder dieses Repo> julos && cd julos
cp .env.example .env            # JULOS_DB_PASSWORD setzen (openssl rand -hex 16); JULOS_UID/JULOS_GID setzen, falls dein Desktop-Benutzer nicht 1000 ist
docker compose up -d --build    # oder: docker compose -f docker-compose.image.yml up -d  (Image aus ghcr.io)
docker compose logs julos       # zeigt den einmaligen Setup-Code
tools/install-linux-agent.sh    # derselbe Rechner wie der Server (leitet an install-agent.sh --desktop weiter)
```

`http://<BIND_IP>:8080` oeffnen (auf `localhost` oder ueber HTTPS; das Sitzungs-Cookie braucht einen sicheren Kontext), Setup-Code eingeben, Benutzername und Passwort waehlen und den
Authenticator-Code bestaetigen. Danach eine Verbindung anlegen (Desktop = RDP, Desktop (VNC) = VNC, Terminal = SSH) und antippen. Ein Host kann **einen Login** behalten (Benutzer und Passwort, Seite des Hosts, Abschnitt Login), den Terminal, Dateien und Desktop-Verbindungen dieses Hosts gemeinsam nutzen; eine Verbindung kann ihren eigenen verlangen. Sonst tippst du das Passwort des Ziels bei jeder Verbindung ein, oder es wird auf Wunsch verschluesselt gespeichert (braucht `JULOS_SECRET_KEY` in `.env`: `openssl rand -base64 32`, sichern wie ein Passwort). Datenbank und Schlüssel sichert der Server täglich nach `~/julos-backup` (Einrichtung und Wiederherstellung: [docs/BACKUP.md](docs/BACKUP.md), englisch); den Ordner zusätzlich vom Rechner wegkopieren).
Eine Desktop-Verbindung streamt die eigene Anzeige-Engine von JulOS (eigener RDP-Client auf dem Server, H.264 im Browser mit WebCodecs dekodiert; der Browser braucht H.264 und einen sicheren Kontext, sonst sagt die App es und bietet "Neu verbinden" an). Sie spielt auch den Ton des Desktops ab und kann das Mikrofon des Browsers an den Desktop senden (aus, bis du es im Menü einschaltest). Siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
Eine **VNC**-Verbindung (RFB 3.3 bis 3.8; ohne Anmeldung, VNC-Passwort oder VeNCrypt mit Zertifikat; Raw, CopyRect, RRE, Hextile, Zlib und ZRLE; Text in der Zwischenablage; der Desktop folgt dem Fenster, wo der Server es kann) läuft durch dieselbe Engine, Bildkodierung und Sitzungen; beim VNC-Passwort zählen nur die ersten 8 Zeichen und reines VNC ist nicht verschlüsselt, also nur im vertrauten Netz oder mit VeNCrypt benutzen. Umfang und Grenzen: [docs/VNC.md](docs/VNC.md) (englisch).
Mit einer Grafikkarte im Container kodiert die Stream-Engine darauf (VAAPI, wahlweise H.265), siehe [docs/ENCODERS.md](docs/ENCODERS.md).
Wahlweise (`JULOS_RDP_GFX=on`, standardmäßig aus) reicht die Stream-Engine das H.264 weiter, das ein RDP-Server durch seine Grafik-Pipeline schickt, statt neu zu kodieren, und fällt auf den eigenen Kodierer zurück, wenn der Server das nicht kann; das gängige xrdp von Debian kann es nicht, siehe [docs/RDPGFX.md](docs/RDPGFX.md).
Reißt bei der Stream-Engine nur die Verbindung zum Browser ab (Handy wechselt das Netz, Tab im Hintergrund), bleibt die RDP-Sitzung `JULOS_STREAM_KEEP_SECONDS` Sekunden auf dem Server (Standard 300, 0 = aus) und der Browser hängt sich ohne neuen Login wieder an ([docs/SESSIONS.md](docs/SESSIONS.md)). Sie kann auch einen Ordner des Servers als Laufwerk in den Desktop reichen, nur Lesen, solange kein zweiter Schalter Schreiben erlaubt; standardmäßig aus, in der Server-Einstellung (`JULOS_DRIVE_ROOT`) und je Verbindung ([docs/DRIVES.md](docs/DRIVES.md)).
Bei der Stream-Engine werden ruhende Bildteile verlustfrei nachgeliefert (Text und Farben pixelgenau), die Bitrate folgt auf langsamer Leitung der Verbindung, und das RDP-Zertifikat wird bei der ersten Verbindung gemerkt (bei einer Änderung kommt eine Warnung, siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
Eine Terminal-Verbindung ist ein echtes Terminal im Browser (der Server haelt die SSH-Verbindung, xterm.js zeichnet den Text: Auswahl, Kopieren und Einfuegen, Scrollback). Die erste Verbindung merkt sich den SSH-Host-Schluessel des Ziels; aendert er sich, warnt die Seite und verbindet erst nach deiner Bestaetigung. Die Anmeldung geht vorerst per Passwort.
Eine Terminal-Verbindung kann statt der Shell ein Programm starten: **Claude Code** oder **Codex** (die Kommandozeilen-Werkzeuge `claude` und `codex` auf deinem Rechner, die sich dort selbst anmelden; JulOS speichert dafür keine Zugangsdaten) oder einen eigenen Befehl, in einem Arbeitsverzeichnis und auf Wunsch in tmux, damit die Arbeit ein Abreißen der Verbindung übersteht. Ein Tipp auf die Kachel „Claude“ oder „Codex“ startet es; unten im Terminal machen eine Eingabeleiste (mehrzeiliges Textfeld, Enter sendet) und eine Zeile Schnelltasten (Esc, Tab, Umschalt+Tab, Strg+C, Strg+D, Pfeile, Bild auf/ab) es am Handy bedienbar. Der Server prüft, ob das Programm auf dem Ziel da ist (`command -v`, 5 s), und startet sonst eine normale Shell mit sichtbarem Hinweis. Siehe [docs/CLI-SESSIONS.md](docs/CLI-SESSIONS.md) (deutsch).
Fuer den Rechner, auf dem der Server selbst laeuft, den Hostnamen `host.docker.internal` verwenden.

`BIND_IP` in `.env` ist die Adresse, auf der der Server lauscht: `127.0.0.1` (Standard, hinter einem Reverse-Proxy) oder die LAN-Adresse.
**Mehrere Rechner (Hosts):** ein Host ist ein Rechner oder Server mit eigenen Verbindungen und eigenem Agenten; Desktop-Oberflaeche und Handy-Shell listen sie mit gruenem oder grauem Statuspunkt (siehe [docs/HOSTS.md](docs/HOSTS.md)).
**Updates und Aktionen auf Hosts laufen im Hintergrund:** nach der Bestätigung (Updates installieren, Pakete reparieren, Neustart, Image holen) erscheint ein kleiner Eintrag mit Fortschrittsbalken in der Menüleiste (Handy: unter der Kopfzeile); ein Klick zeigt die Live-Ausgabe, eine Fehlerzusammenfassung in Klartext und das volle Protokoll. Proxmox wird als Ganzes aktualisiert (`dist-upgrade`), halb eingerichtete Pakete bekommen die Taste **Reparieren**, und das Wort `disks` lässt den Agenten die SMART-Werte der Platten über den Root-Helfer lesen. Siehe [docs/HOST-AGENT.md](docs/HOST-AGENT.md#background-tasks).
**Agent-Betrieb und Updates:** der Host-Dialog zeigt `Agent 0.x, Update verfügbar`, wenn der Server einen neueren Agenten mitliefert; **Jetzt aktualisieren** lässt den verbundenen Agenten die neue Datei selbst von deinem Server holen (SHA-256 vom Server, nur gleiche Herkunft, atomarer Ersatz, Neustart), ein Schalter je Agent (Standard aus) macht es beim Verbinden automatisch. Auf dem Ziel: `julos-agent.py status | stop | uninstall [--yes]`; das Log liegt in `~/.local/state/julos/agent.log` (mit Rotation); `install-linux-agent.sh --systemd` startet ihn als Benutzerdienst. Agenten vor 0.4.0 müssen einmal neu eingerichtet werden, bevor sie sich aktualisieren können. Einzelheiten in [docs/AGENT_API.md](docs/AGENT_API.md).
**Ein Agent pro Rechner, ein Installer:** der Agent (`tools/julos-agent.py`, 0.7.0) kann auf einem Rechner alles: mit Desktop läuft er in deiner Sitzung (Fensterleiste, Programme, Zwischenablage) **und** beantwortet die Host-Anfragen (Systemwerte, Updates, Platten, Dienste, Docker, bestätigte Aktionen); auf einem Server ohne Desktop läuft er als eigener Systembenutzer. `curl -fsSL <Server>/agent-files/install-agent.sh -o julos-install-agent.sh && sudo sh julos-install-agent.sh --server <Server> --code CODE [--allow updates,logs] [--dry-run]` erkennt selbst, was der Rechner ist („Desktop detected“), installiert geprüfte Dateien nur von deinem Server, lässt die Rechte-Datei leer, solange du keine Wörter mit `--allow` nennst, und aktualisiert eine vorhandene Installation ohne neuen Code. `install-host-agent.sh` und `install-linux-agent.sh` funktionieren weiter (sie leiten weiter). Siehe [docs/HOST-AGENT.md](docs/HOST-AGENT.md).
**Agent auf einem anderen Rechner als der Server:** den Host oeffnen (Host-Dialog oder Ansicht Hosts), **Agent einrichten** waehlen und die angezeigten Befehle in der grafischen Sitzung des Ziels ausfuehren (sie laden die Agent-Dateien von deinem Server und richten ihn mit einem einmaligen Code ein). Der Agent meldet sich von dort aus per HTTPS beim Server, auf dem Ziel lauscht nichts; **Agent entfernen** widerruft ihn. Siehe [docs/AGENT_API.md](docs/AGENT_API.md).
Auf demselben Rechner bleibt es bei `tools/install-linux-agent.sh` ohne Argumente: die Agent-Dateien liegen in `~/julos-uploads` des Zielbenutzers und der Server laeuft als dieser Benutzer (`JULOS_UID`/`JULOS_GID`). Passwort oder Authenticator verloren? `tools/julos-user.sh` setzt sie auf der Konsole zurueck.

## PWA installieren, HTTPS

Browser installieren PWAs und erlauben die Zwischenablage-API nur ueber **HTTPS**. Davor gehoert ein Reverse-Proxy
(Caddy-Beispiel: [caddy/Caddyfile.example](caddy/Caddyfile.example)). Ueber reines HTTP im Heimnetz geht die Seite, aber Passwoerter laufen unverschluesselt.

## Sicherheit

Eine Fernsteuerung darf nicht allein durch das Desktop-Passwort geschuetzt sein.

- **Eigene Anmeldung:** Passwort (Argon2id) plus Authenticator-Code, zehn Wiederherstellungscodes, langlebige rotierende Sitzungen (in PostgreSQL nur als Hash), Sperre mit wachsender Wartezeit. Jede API und der WebSocket brauchen eine Sitzung, siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **Erster Start:** das erste Konto legst du im Browser an, mit einem einmaligen Setup-Code aus dem Server-Log. Danach ist die Einrichtung geschlossen. Das Authenticator-Geheimnis siehst du auch als QR-Code; Passwort und Authenticator aenderst du spaeter in der App (Handy: Profil, Desktop: Einstellungen), jeweils mit dem aktuellen Passwort. Ein kleiner Dialog (Ping, DNS-Abfrage, Port-Test) prueft vom Server aus, ob die Ziele deiner gespeicherten Verbindungen antworten.
- **Verbindungen** laufen ueber Einmal-Tickets (30 s). Das Passwort des Ziels tippst du pro Verbindung (hoechstens 30 s im Speicher, nie geloggt) oder es wird auf Wunsch AES-256-GCM-verschluesselt gespeichert, mit einem Schluessel nur in den Server-Einstellungen; es geht nie zurueck zum Browser.
- PostgreSQL hat keinen veroeffentlichten Port. Offene Verbindungen enden mit ihrer Sitzung.
- **mTLS** im Proxy (optionale Zusatzschicht): nur dein Handy mit Client-Zertifikat erreicht die Anmeldeseite. Erzeugen mit
  `tools/make-client-cert.sh <name>` (Passphrase tippst du selbst); das Caddy-Beispiel enthaelt den `client_auth`-Block.
- Die Agent-API ist nur ueber den Server mit gueltiger Sitzung erreichbar. Agenten antworten entweder auf einem Unix-Socket (Rechte 0600, derselbe Rechner) oder melden sich mit einem Token je Host aus (auf dem Server nur als Hash, widerrufbar, einmaliger Einrichtungscode).
- xrdp (3389) und den Server-Port nie ohne Proxy davor ins Internet stellen.
- **Checkliste fürs Internet** (Einzelheiten und Aufwand in [docs/SECURITY-REVIEW.md](docs/SECURITY-REVIEW.md), englisch): `JULOS_TRUSTED_PROXIES` auf die Adresse des Proxys setzen und Port 8080 nur für den Proxy erreichbar machen, `JULOS_ALLOWED_HOSTS` setzen, Sicherung (und ihren Schlüssel) nur an einen Ort legen, den nur du lesen kannst, bei Agenten von außen [caddy/Caddyfile.internet.example](caddy/Caddyfile.internet.example) nehmen, Images mindestens monatlich neu bauen oder ziehen (siehe [docs/RELEASING.md](docs/RELEASING.md)).

Reihenfolge: Proxy-Konfiguration, Port 443 auf den Proxy, DNS; optional ein Client-Zertifikat obendrauf.

## Fotos

`POST /api/upload` legt das Bild in `~/julos-uploads`; `tools/julos-clip.py` (Autostart in der Sitzung) legt es in die GTK-Zwischenablage und
quittiert, danach sendet die Seite Strg+V. Dateien werden nach 30 Minuten geloescht.
Die App selbst braucht diesen Weg nicht mehr: die Stream-Engine sendet Bilder und Dateien selbst ueber RDP (siehe [docs/CLIPBOARD.md](docs/CLIPBOARD.md), englisch). Upload-API und Helfer bleiben fuer die Agent-API.

## Aufbau

- `src/JulOS.Frontend` Host: Web-Client (`wwwroot`: PWA), HTTP-API, WebSocket; `src/JulOS.Backend` Konten, Sitzungen, Verbindungen (PostgreSQL); `src/JulOS.Integrations` Anzeige-Engine (RDP- und VNC-Client, H.264), SSH-Client und Agent-Client; `src/JulOS.Workers` Hintergrundjobs
- `tests/` (`frontend/` laeuft mit node), `database/` (SQL-Migrationen), `deploy/dev/` die Test-Instanz mit Wegwerf-Zielen fuer SSH und RDP (siehe [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md); Test auf dem echten Handy: [docs/PHONE-TEST.md](docs/PHONE-TEST.md))
- `tools/julos-agent.py` Linux-Agent (Fenster, Programme, Programm-Modus, ausgehende Verbindung zum Server), `tools/julos-clip.py` Foto-Helfer
- `tools/install-agent.sh` (und die dünnen `tools/install-host-agent.sh`, `tools/install-linux-agent.sh`), `tools/julos-user.sh`, `tools/make-client-cert.sh`
- `docs/AGENT_API.md` Agent-API, `docs/ARCHITECTURE.md` Stack und Sicherheitskonzept, `docs/MODULES.md` die optionalen Module Dienste (Widgets), Caddy, DDNS und die Karte Heimnetz (standardmaessig aus), `caddy/Caddyfile.example`
- `.github/workflows/docker.yml` baut das Image fuer `ghcr.io/<owner>/<repo>` (amd64 + arm64; die SDK-Stufe laeuft nativ, ohne Emulation), `.github/workflows/tests.yml` fuehrt die Server-Tests (mit PostgreSQL), die Browser-Logik-Tests und eine NuGet-Schwachstellenmeldung aus

## Ausblick

Jetzt: das Remote-Modul (siehe [docs/SCOPE.md](docs/SCOPE.md)). Danach: ein Modul-Kern, dann das Docker-Modul und Schreibzugriff fuer Caddy und DDNS (die lesende erste Stufe gibt es schon, standardmaessig aus), Dateien, Windows-Agent, mehrere Hosts. Ziel ist eine Oberflaeche fuer alles, was du betreibst.

## Lizenz

MIT, siehe [LICENSE](LICENSE). Bausteine von Dritten und ihre Lizenzen: `src/JulOS.Frontend/wwwroot/licenses.txt` und `src/JulOS.Integrations/Display/NOTICE.md`.
