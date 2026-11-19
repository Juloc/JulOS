# JulOS

**Deine Rechner, deine Programme, deine Dateien: ein persoenlicher Arbeitsbereich, in der Hosentasche oder in jedem Browser.**
Selbst gehostet und Open Source. Was wir jetzt bauen: [docs/SCOPE.md](docs/SCOPE.md). Das groessere Bild: [docs/VISION.md](docs/VISION.md). Geparkte Ideen: [docs/IDEAS.md](docs/IDEAS.md) (alle Englisch).

Das erste Stueck ist ein Fernzugriff, gebaut fuer das **Handy**: deine normale Android-Tastatur (Vorschlaege,
Autokorrektur, Bilder), eine anpassbare Sondertasten-Leiste, eine Fensterleiste in der App statt der Taskleiste,
dynamische Aufloesung, Material-You-Farben und installierbare PWA. Darunter ist es normaler Fernzugriff (RDP ueber Apache Guacamole)
auf den eigenen Rechner, nichts liegt in einer Cloud.

[English version](README.md)

```
Handy (PWA)  --HTTPS-->  dein Reverse-Proxy  -->  JulOS-Server (.NET, eigene Anmeldung)  -->  guacd  --RDP-->  dein Rechner
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
- **Mehrere Sitzungen gleichzeitig** (bis zu 8 je Benutzer, RDP und SSH, auch zwei zum selben Ziel): am Handy eine Leiste mit einem Chip je Sitzung (Zustandspunkt) und ein Sheet mit Karten zum Wechseln und Schliessen; am grossen Bildschirm ist jede Sitzung ein eigenes Fenster, die Tastatur gehoert nur dem Fenster mit Fokus. Sitzungen im Hintergrund laufen weiter, aber ohne teure Arbeit. Siehe [docs/SESSIONS.md](docs/SESSIONS.md).
- **PWA**: installierbar (braucht HTTPS), Standalone/Vollbild, Offline-Huelle. Material-You-Palette aus Grundfarbe oder Bild.
- **Neu verbinden** mit Animation, Auto-Tastatur bei Textfeldern, Layout folgt dem sichtbaren Viewport.

## Schnellstart

Voraussetzungen auf dem Zielrechner: Linux mit X11, **xrdp** (und xorgxrdp), `python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0`.
Auf dem Docker-Host (darf derselbe Rechner sein): Docker mit Compose.

```bash
git clone <dein Fork oder dieses Repo> julos && cd julos
cp .env.example .env            # JULOS_DB_PASSWORD setzen (openssl rand -hex 16); JULOS_UID/JULOS_GID setzen, falls dein Desktop-Benutzer nicht 1000 ist
docker compose up -d --build    # oder: docker compose -f docker-compose.image.yml up -d  (Image aus ghcr.io)
docker compose logs julos       # zeigt den einmaligen Setup-Code
tools/install-linux-agent.sh    # in der grafischen Sitzung des Zielrechners
```

`http://<BIND_IP>:8080` oeffnen (auf `localhost` oder ueber HTTPS; das Sitzungs-Cookie braucht einen sicheren Kontext), Setup-Code eingeben, Benutzername und Passwort waehlen und den
Authenticator-Code bestaetigen. Danach eine Verbindung anlegen (Desktop = RDP, Terminal = SSH) und antippen. Das Passwort des Ziels tippst du bei jeder Verbindung ein, oder es wird auf Wunsch verschluesselt gespeichert (braucht `JULOS_SECRET_KEY` in `.env`: `openssl rand -base64 32`, sichern wie ein Passwort). Datenbank und Schlüssel sichert der Server täglich nach `~/julos-backup` (Einrichtung und Wiederherstellung: [docs/BACKUP.md](docs/BACKUP.md), englisch); den Ordner zusätzlich vom Rechner wegkopieren).
Eine Desktop-Verbindung kann auch die experimentelle Stream-Engine nutzen (eigener RDP-Client, H.264 im Browser; im Dialog der Verbindung unter "Bildübertragung" "Stream" wählen). Standard bleibt Guacamole, und die App weicht darauf aus, wenn Browser oder Server kein H.264 können. Die Stream-Engine spielt auch den Ton des Desktops ab und kann das Mikrofon des Browsers an den Desktop senden (aus, bis du es im Menü einschaltest). Siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
Mit einer Grafikkarte im Container kodiert die Stream-Engine darauf (VAAPI, wahlweise H.265), siehe [docs/ENCODERS.md](docs/ENCODERS.md).
Eine Desktop-Verbindung kann auch die experimentelle Stream-Engine nutzen (eigener RDP-Client, H.264 im Browser; im Dialog der Verbindung unter "Bildübertragung" "Stream" wählen). Standard bleibt Guacamole, und die App weicht darauf aus, wenn Browser oder Server kein H.264 können. Siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
Bei der Stream-Engine werden ruhende Bildteile verlustfrei nachgeliefert (Text und Farben pixelgenau), die Bitrate folgt auf langsamer Leitung der Verbindung, und das RDP-Zertifikat wird bei der ersten Verbindung gemerkt (bei einer Änderung kommt eine Warnung, siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
Eine Terminal-Verbindung ist ein echtes Terminal im Browser (der Server haelt die SSH-Verbindung, xterm.js zeichnet den Text: Auswahl, Kopieren und Einfuegen, Scrollback). Die erste Verbindung merkt sich den SSH-Host-Schluessel des Ziels; aendert er sich, warnt die Seite und verbindet erst nach deiner Bestaetigung. Die Anmeldung geht vorerst per Passwort.
Fuer den Rechner, auf dem der Server selbst laeuft, den Hostnamen `host.docker.internal` verwenden.

`BIND_IP` in `.env` ist die Adresse, auf der der Server lauscht: `127.0.0.1` (Standard, hinter einem Reverse-Proxy) oder die LAN-Adresse.
**Agent auf einem anderen Rechner als der Server:** die Desktop-Verbindung oeffnen, **Agent einrichten** waehlen und die angezeigten Befehle in der grafischen Sitzung des Ziels ausfuehren (sie laden die Agent-Dateien von deinem Server und richten ihn mit einem einmaligen Code ein). Der Agent meldet sich von dort aus per HTTPS beim Server, auf dem Ziel lauscht nichts; **Agent entfernen** widerruft ihn. Siehe [docs/AGENT_API.md](docs/AGENT_API.md).
Auf demselben Rechner bleibt es bei `tools/install-linux-agent.sh` ohne Argumente: die Agent-Dateien liegen in `~/julos-uploads` des Zielbenutzers und der Server laeuft als dieser Benutzer (`JULOS_UID`/`JULOS_GID`). Passwort oder Authenticator verloren? `tools/julos-user.sh` setzt sie auf der Konsole zurueck.

## PWA installieren, HTTPS

Browser installieren PWAs und erlauben die Zwischenablage-API nur ueber **HTTPS**. Davor gehoert ein Reverse-Proxy
(Caddy-Beispiel: [caddy/Caddyfile.example](caddy/Caddyfile.example)). Ueber reines HTTP im Heimnetz geht die Seite, aber Passwoerter laufen unverschluesselt.

## Sicherheit

Eine Fernsteuerung darf nicht allein durch das Desktop-Passwort geschuetzt sein.

- **Eigene Anmeldung:** Passwort (Argon2id) plus Authenticator-Code, zehn Wiederherstellungscodes, langlebige rotierende Sitzungen (in PostgreSQL nur als Hash), Sperre mit wachsender Wartezeit. Jede API und der WebSocket brauchen eine Sitzung, siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **Erster Start:** das erste Konto legst du im Browser an, mit einem einmaligen Setup-Code aus dem Server-Log. Danach ist die Einrichtung geschlossen.
- **Verbindungen** laufen ueber Einmal-Tickets (30 s). Das Passwort des Ziels tippst du pro Verbindung (hoechstens 30 s im Speicher, nie geloggt) oder es wird auf Wunsch AES-256-GCM-verschluesselt gespeichert, mit einem Schluessel nur in den Server-Einstellungen; es geht nie zurueck zum Browser.
- `guacd` und PostgreSQL haben keinen veroeffentlichten Port. Offene Verbindungen enden mit ihrer Sitzung.
- **mTLS** im Proxy (optionale Zusatzschicht): nur dein Handy mit Client-Zertifikat erreicht die Anmeldeseite. Erzeugen mit
  `tools/make-client-cert.sh <name>` (Passphrase tippst du selbst); das Caddy-Beispiel enthaelt den `client_auth`-Block.
- Die Agent-API ist nur ueber den Server mit gueltiger Sitzung erreichbar. Agenten antworten entweder auf einem Unix-Socket (Rechte 0600, derselbe Rechner) oder melden sich mit einem Token je Verbindung aus (auf dem Server nur als Hash, widerrufbar, einmaliger Einrichtungscode).
- xrdp (3389) und den Server-Port nie ohne Proxy davor ins Internet stellen.

Reihenfolge: Proxy-Konfiguration, Port 443 auf den Proxy, DNS; optional ein Client-Zertifikat obendrauf.

## Fotos

`POST /api/upload` legt das Bild in `~/julos-uploads`; `tools/julos-clip.py` (Autostart in der Sitzung) legt es in die GTK-Zwischenablage und
quittiert, danach sendet die Seite Strg+V. Dateien werden nach 30 Minuten geloescht.
Guacamole kann Bild-Zwischenablagen nicht ueber RDP senden, deshalb dieser Weg.

## Aufbau

- `src/JulOS.Frontend` Host: Web-Client (`wwwroot`: PWA), HTTP-API, WebSocket; `src/JulOS.Backend` Konten, Sitzungen, Verbindungen (PostgreSQL); `src/JulOS.Integrations` guacd-Relay und Agent-Client; `src/JulOS.Workers` Hintergrundjobs
- `tests/` (`frontend/` laeuft mit node), `database/` (SQL-Migrationen), `deploy/dev/` die Test-Instanz mit Wegwerf-Zielen fuer SSH und RDP (siehe [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md); Test auf dem echten Handy: [docs/PHONE-TEST.md](docs/PHONE-TEST.md))
- `tools/julos-agent.py` Linux-Agent (Fenster, Programme, Programm-Modus, ausgehende Verbindung zum Server), `tools/julos-clip.py` Foto-Helfer
- `tools/install-linux-agent.sh`, `tools/julos-user.sh`, `tools/make-client-cert.sh`
- `docs/AGENT_API.md` Agent-API, `docs/ARCHITECTURE.md` Stack und Sicherheitskonzept, `caddy/Caddyfile.example`
- `.github/workflows/docker.yml` baut das Image fuer `ghcr.io/<owner>/<repo>` (amd64 + arm64)

## Ausblick

Jetzt: das Remote-Modul (siehe [docs/SCOPE.md](docs/SCOPE.md)). Danach: ein Modul-Kern, dann Docker-, Caddy- und DDNS-Module, Dateien, Windows-Agent, mehrere Hosts. Ziel ist eine Oberflaeche fuer alles, was du betreibst.

## Lizenz

MIT, siehe [LICENSE](LICENSE). Basiert auf Apache Guacamole (guacd, guacamole-common-js).
