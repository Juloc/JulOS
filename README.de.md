# JulOS

**Deine Rechner, deine Programme, deine Dateien: ein persoenlicher Arbeitsbereich, in der Hosentasche oder in jedem Browser.**
Selbst gehostet und Open Source. Was wir jetzt bauen: [docs/SCOPE.md](docs/SCOPE.md). Das groessere Bild: [docs/VISION.md](docs/VISION.md). Geparkte Ideen: [docs/IDEAS.md](docs/IDEAS.md) (alle Englisch).

Das erste Stueck ist ein Fernzugriff, gebaut fuer das **Handy**: deine normale Android-Tastatur (Vorschlaege,
Autokorrektur, Bilder), eine anpassbare Sondertasten-Leiste, eine Fensterleiste in der App statt der Taskleiste,
dynamische Aufloesung, Material-You-Farben und installierbare PWA. Darunter ist es normaler Fernzugriff (RDP ueber Apache Guacamole)
auf den eigenen Rechner, nichts liegt in einer Cloud.

[English version](README.md)

```
Handy (PWA)  --HTTPS-->  dein Reverse-Proxy  -->  Gateway (Node + guacamole-lite)  -->  guacd  --RDP-->  xrdp (dein Linux-Desktop)
                                                         \--> Agent (Unix-Socket) in der Sitzung: Fenster, Programme, Foto-Paste
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
- **PWA**: installierbar (braucht HTTPS), Standalone/Vollbild, Offline-Huelle. Material-You-Palette aus Grundfarbe oder Bild.
- **Neu verbinden** mit Animation, Auto-Tastatur bei Textfeldern, Layout folgt dem sichtbaren Viewport.

## Schnellstart

Voraussetzungen auf dem Zielrechner: Linux mit X11, **xrdp** (und xorgxrdp), `python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0`.
Auf dem Docker-Host (darf derselbe Rechner sein): Docker mit Compose.

```bash
git clone <dein Fork oder dieses Repo> julos && cd julos
cp .env.example .env            # TOKEN_KEY (32 Zeichen: openssl rand -hex 16) und RDP_USER setzen
docker compose up -d --build    # oder: docker compose -f docker-compose.image.yml up -d  (Image aus ghcr.io)
tools/install-linux-agent.sh    # in der grafischen Sitzung des Zielrechners
```

`http://<BIND_IP>:8080` oeffnen und mit dem Passwort des Linux-Benutzers anmelden (tippst du selbst, wird nie gespeichert).

`BIND_IP` in `.env` ist die Adresse, auf der das Gateway lauscht: `127.0.0.1` (Standard, hinter einem Reverse-Proxy) oder die LAN-Adresse.
Ist der Docker-Host nicht der Zielrechner, `RDP_HOST` setzen. Die Agent-Dateien liegen in `~/julos-uploads` des Zielbenutzers,
unterstuetzt ist deshalb zunaechst derselbe Rechner.

## PWA installieren, HTTPS

Browser installieren PWAs und erlauben die Zwischenablage-API nur ueber **HTTPS**. Davor gehoert ein Reverse-Proxy
(Caddy-Beispiel: [caddy/Caddyfile.example](caddy/Caddyfile.example)). Ueber reines HTTP im Heimnetz geht die Seite, aber Passwoerter laufen unverschluesselt.

## Sicherheit

Eine Fernsteuerung darf nicht allein durch das Desktop-Passwort geschuetzt sein.

- Das Ziel (Host, Port, Benutzer) steht fest auf dem Server; der Browser liefert nur das Passwort, weitergereicht in einem AES-verschluesselten Token, 30 s gueltig.
- `guacd` hat keinen veroeffentlichten Port. Anmeldeversuche sind begrenzt.
- **Proxy-Modus**: mit `PROXY_SECRET` antwortet das Gateway (HTTP und WebSocket) nur auf Anfragen mit dem Header `X-Proxy-Secret`,
  den dein Reverse-Proxy setzt. Einschalten mit `tools/enable-proxy-mode.sh`.
- **mTLS** im Proxy: nur dein Handy mit Client-Zertifikat erreicht die Anmeldeseite. Erzeugen mit
  `tools/make-client-cert.sh <name>` (Passphrase tippst du selbst); das Caddy-Beispiel enthaelt den `client_auth`-Block.
- Die Agent-API ist nur ueber das Gateway mit gueltiger Sitzung erreichbar, per Unix-Socket (Rechte 0600).
- xrdp (3389) und den Gateway-Port nie direkt ins Internet stellen.

Reihenfolge: Zertifikat, Proxy-Konfiguration, `tools/enable-proxy-mode.sh`, Port 443 auf den Proxy, DNS.
Rueckgaengig: `PROXY_SECRET` in `.env` leeren und `docker compose up -d`.

## Fotos

`POST /api/upload` legt das Bild in `~/julos-uploads`; `tools/julos-clip.py` (Autostart in der Sitzung) legt es in die GTK-Zwischenablage und
quittiert, danach sendet die Seite Strg+V. Dateien werden nach 30 Minuten geloescht.
Guacamole kann Bild-Zwischenablagen nicht ueber RDP senden, deshalb dieser Weg.

## Aufbau

- `gateway/server.js` Token-Server, WebSocket-Tunnel, Upload, Agent-Proxy
- `gateway/public/` Web-Client (`index.html`, `app.js`, `style.css`, `sw.js`, `manifest.webmanifest`)
- `tools/julos-agent.py` Linux-Agent (Fenster, Programme, Programm-Modus), `tools/julos-clip.py` Foto-Helfer
- `tools/install-linux-agent.sh`, `tools/make-client-cert.sh`, `tools/enable-proxy-mode.sh`
- `docs/AGENT_API.md` Agent-API, `caddy/Caddyfile.example`
- `.github/workflows/docker.yml` baut das Image fuer `ghcr.io/<owner>/<repo>` (amd64 + arm64)

## Ausblick

Jetzt: das Remote-Modul (siehe [docs/SCOPE.md](docs/SCOPE.md)). Danach: ein Modul-Kern, dann Docker-, Caddy- und DDNS-Module, Dateien, Windows-Agent, mehrere Hosts. Ziel ist eine Oberflaeche fuer alles, was du betreibst.

## Lizenz

MIT, siehe [LICENSE](LICENSE). Basiert auf Apache Guacamole (guacd, guacamole-common-js) und guacamole-lite.
