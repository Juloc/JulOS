# Programm-Sitzungen: Claude Code und Codex mit einem Tipp

Das native Terminal ([ARCHITECTURE.md](ARCHITECTURE.md), [SESSIONS.md](SESSIONS.md)) kann statt der blanken Shell ein Programm starten.
Gedacht ist es für die Kommandozeilen-Werkzeuge `claude` (Claude Code) und `codex` (OpenAI Codex CLI), die auf einem deiner Rechner laufen:
ein Tipp auf die Kachel „Claude“ oder „Codex“, ohne erst eine Shell zu öffnen, ins Verzeichnis zu wechseln und den Befehl zu tippen,
und am Handy gut zu bedienen. Es ist dasselbe SSH-Terminal wie bisher, nur direkter.

**Anmeldedaten für Claude oder Codex speichert JulOS nie.** Die Werkzeuge melden sich auf dem Zielrechner selbst an (einmal dort
`claude` oder `codex` starten und anmelden). JulOS kennt davon nichts.

## Einrichtung

Auf dem Zielrechner (dem, zu dem die SSH-Verbindung geht):

1. Das Werkzeug installieren und einmal anmelden, so wie es dessen Anleitung beschreibt. Es muss für eine **Login-Shell** im `PATH` stehen
   (siehe „Wie der Start geprüft wird“). Üblich ist `~/.local/bin`, das `~/.profile` auf den meisten Systemen aufnimmt.
2. Optional `tmux` installieren, wenn die Arbeit ein Abreißen der Verbindung überstehen soll (siehe unten).

In JulOS:

- **Eine Kachel aus einer vorhandenen Terminal-Verbindung:** Verbindung bearbeiten (Stift) und „Kachel „Claude“ anlegen“ oder
  „Kachel „Codex“ anlegen“. Es entsteht eine neue Verbindung „Claude auf <Host>“ mit demselben Ziel (Adresse, Port, Benutzer, **gespeichertes
  Passwort** und bekannter Host-Schlüssel werden vom Server übernommen, ohne dass sie je zum Browser gehen), die Original-Verbindung bleibt.
  Dasselbe Ziel darf beliebig viele Kacheln haben: eine Verbindung ist ein Ziel plus ein Programm (und liegt wie jede Verbindung auf einem Host,
  siehe [HOSTS.md](HOSTS.md)).
- **Ein-Tipp-Start:** Auf der Dienste-Seite am Handy gibt es die Knöpfe „Claude Code“ und „Codex“, am Desktop stehen sie im Dock, im
  Launcher, im Menü „Apps“ und in den Schnellaktionen. Gibt es genau eine passende Kachel, öffnet der Tipp sie. Gibt es mehrere, wählst du.
  Gibt es keine, aber eine Terminal-Verbindung, legt der Tipp die Kachel aus dieser an (der Name steht danach in der Liste) und startet sie.
- **Von Hand:** Verbindung anlegen oder bearbeiten, Art „Terminal (SSH)“, dann die Felder unten.

### Felder einer Terminal-Verbindung

| Feld | Bedeutung |
|---|---|
| Startprogramm | „Shell“ (Standard, wie bisher), „Claude Code“ (`claude`), „Codex“ (`codex`) oder „Eigener Befehl“ |
| Befehl | nur bei „Eigener Befehl“: freier Text, eine Zeile, bis 500 Zeichen, zum Beispiel `claude --continue` |
| Arbeitsverzeichnis | optional. `~` oder `~/projekte/app` (im Home-Ordner), `/srv/app` (absolut) oder `projekte/app` (relativ zum Home). Erlaubt sind Buchstaben, Ziffern und `. _ - / + @ % : , =`; ein `~` nur am Anfang; keine Leerzeichen, Anführungszeichen, `$`, Backticks, Semikolons oder Zeilenumbrüche. Bis 200 Zeichen |
| Sitzung am Leben halten (tmux) | startet das Programm in einer tmux-Sitzung auf dem Ziel (siehe unten). Standard aus |

Alle Felder haben Standardwerte: eine Verbindung ohne sie ist eine gewöhnliche Shell wie vor dieser Funktion (Migration 014, Spalten
`program`, `workdir`, `keep_alive`, `custom_command`; ein Auszug aus einer älteren Version lässt sich einspielen). Auch „Shell“ kann ein
Arbeitsverzeichnis und tmux haben.

## Wie der Start geprüft wird

Beim Verbinden öffnet der Server neben der Shell einen kurzen zweiten Kanal (ohne Terminal, **Frist 5 Sekunden**) und führt einen
festen Prüfbefehl aus: `command -v claude` (oder `codex`), ob das Arbeitsverzeichnis da ist (`cd`) und, wenn tmux gewünscht ist,
`command -v tmux`. Findet die einfache Suche das Programm nicht, sucht der Befehl noch einmal in einer Login-Shell (`$SHELL -lc`), weil
dort oft ein erweiterter `PATH` gilt. Nur was die Verbindung braucht, wird geprüft. Danach schreibt der Server eine Startzeile in die Shell.
Es gibt vier Rückfälle, jeder mit sichtbarem Hinweis (oben im Terminal und als gelbe Zeile im Verlauf), nie stillschweigend:

| Fund | Verhalten | Hinweis (Beispiel) |
|---|---|---|
| Programm fehlt | gewöhnliche Shell (mit Arbeitsverzeichnis und tmux, wenn gewünscht, unter dem Namen einer Shell-Sitzung) | „claude ist auf diesem Rechner nicht installiert oder nicht im PATH. Es läuft eine normale Shell.“ |
| Arbeitsverzeichnis fehlt | gewöhnliche Shell im Home-Ordner, **das Programm startet nicht** (ein Programm im falschen Ordner wäre gefährlich) | „Das Arbeitsverzeichnis … gibt es auf diesem Rechner nicht.“ |
| tmux fehlt | normale Sitzung ohne tmux | „tmux ist auf diesem Rechner nicht installiert: die Sitzung bleibt nicht erhalten, wenn die Verbindung abreißt.“ |
| Prüfung fehlgeschlagen oder nach 5 s ohne Antwort | gewöhnliche Shell | „Das Programm ließ sich auf dem Rechner nicht prüfen.“ |

Der „Eigene Befehl“ wird nicht auf Vorhandensein geprüft (er kann alles Mögliche sein), nur Verzeichnis und tmux.

Die Startzeile beginnt mit einem Leerzeichen (viele Shells nehmen solche Zeilen nicht in den Verlauf) und ersetzt die Shell durch das
Programm (`exec`): beendest du das Programm (zum Beispiel mit Strg+D), endet die Sitzung, und JulOS zeigt „Beendet“ mit „Neu verbinden“.
Sie ist für Bash, Zsh, Fish und andere übliche Shells des Ziels geschrieben. Du siehst sie kurz im Terminal, bevor das Programm den
Bildschirm übernimmt.

## tmux: Arbeit, die ein Abreißen überlebt

Mit „Sitzung am Leben halten“ startet der Server

```
tmux new-session -A -s julos-<programm>-<name>-<kennung> '<programm>' \; set-option mouse on \; set-option status off
```

- **`-A`**: gibt es die Sitzung schon, hängt sich der Client wieder an, statt eine neue zu starten. Das Programm läuft also auf dem Ziel weiter,
  auch wenn das Handy den Empfang verliert, gesperrt wird oder der Browser zugeht; beim Neuverbinden ist alles noch da.
- Der **Name** besteht nur aus `[a-z0-9-]`: Programm, Name der Verbindung (Kleinbuchstaben, Sonderzeichen zu Bindestrichen, höchstens 24 Zeichen) und
  die ersten sechs Zeichen der Kennung der Verbindung (zwei Verbindungen mit gleichem Namen teilen sich nie eine Sitzung). Gleiche Verbindung,
  gleicher Name: so findet der Neustart seine Sitzung wieder.
- **Maus an**: Wischen im Terminal scrollt den Verlauf von tmux. **Statuszeile aus**: am Handy ist jede Zeile kostbar. Beides gilt nur für
  Sitzungen mit dem Namen `julos-…`; deine anderen tmux-Sitzungen bleiben unberührt.
- **Neuverbinden im Browser:** läuft die Sitzung in tmux (der Server meldet es beim Verbinden), hängt der Browser sie nach einem Abriss von
  selbst wieder an, mit wachsenden Pausen bis zu zehn Minuten (dieselben Fristen wie beim Stream, `resume.js`), auch sofort nach dem
  Zurückkommen aus dem Hintergrund und wenn das Netz wieder da ist. Ohne tmux gibt es kein stilles Neuverbinden: die Shell auf dem Ziel endet mit
  der Verbindung, ein neues Anhängen ergäbe eine leere Shell; die Überlagerung bietet „Neu verbinden“ an. Der Entwurf in der Eingabeleiste bleibt in beiden Fällen.
- Fehlt tmux auf dem Ziel, gilt der Rückfall oben (normale Sitzung, Hinweis).

Beenden: Programm beenden (Strg+D beziehungsweise `/exit`) beendet auch die tmux-Sitzung. Ein reines Schließen der Sitzung in JulOS
trennt nur: die tmux-Sitzung läuft weiter und wartet. Aufräumen auf dem Ziel mit `tmux ls` und `tmux kill-session -t <name>`.

## Bedienung am Handy und am Desktop

Bei Programmen ist unten im Terminal die **Eingabeleiste** offen (bei der blanken Shell eingeklappt, wie bisher; der Pfeil rechts unten holt sie):

- **Textfeld** (mehrzeilig, wächst bis sechs Zeilen): Diktieren, lange Prompts und Einfügen ohne direktes Tippen ins Terminal. **Enter** sendet,
  **Umschalt+Enter** (oder der Knopf „Neue Zeile“) macht eine neue Zeile. Der Text geht als Einfügen (mit Klammer-Modus, wenn das Programm ihn
  kann, sonst zeilenweise), kurz danach folgt Enter. Ein leeres Feld sendet nur Enter (zum Bestätigen einer Rückfrage). Während einer Eingabemethode
  (Diktat, Kompositionstext) sendet Enter nicht, sondern bestätigt die Zeichen. Ist die Verbindung weg, bleibt der Text im Feld.
- **Schnelltasten** (wischbare Zeile): Esc, ⇧Tab, Tab, Strg+C, Strg+D, Pfeile, Enter, Bild auf/ab und je nach Programm Strg+O, `/` und `@`.
  Die Voreinstellungen stehen in `terminal-programs.js`; Claude Code bekommt ⇧Tab (Betriebsart) und Strg+O, beide Programme `/` (Befehle) und `@`
  (Dateien). Die allgemeine Tastenleiste der Sitzung (Handy) bleibt daneben, mit Strg+D für Terminals in den Voreinstellungen
  (je Taste „Gilt für“ Terminal, siehe [FOLDERS.md](FOLDERS.md)).
- **Kopieren und Einfügen:** „Auswahl kopieren“ nimmt die Auswahl im Terminal (lange tippen wählt ein Wort, ziehen erweitert; am PC Maus) in die
  Zwischenablage. „Einfügen“ liest die Zwischenablage und legt den Text ins Textfeld, zum Durchsehen vor dem Senden. Beides braucht die
  **Berechtigung des Browsers** für die Zwischenablage (auf dem Handy fragt er beim ersten Mal; nur auf sicheren Seiten, also HTTPS oder
  localhost). Ohne sie bleibt Kopieren über den alten Weg (verstecktes Feld) möglich, und beim Einfügen steht der Hinweis, wie es sonst geht
  (Tastatur lange drücken; am PC Strg+Umschalt+V). Das Kontextmenü des Terminals (lange Tippen auf eine Auswahl, Rechtsklick) fügt direkt ins
  Terminal ein. Siehe [CLIPBOARD.md](CLIPBOARD.md) für den Ablauf bei Desktop-Sitzungen; das Terminal nutzt seine eigene Kopierfunktion.
- **Schrift:** „A−“ und „A+“, Zwei-Finger-Spreizen im Terminal und die Zoom-Tasten der Leiste; die Größe wird gemerkt.
- **Scrollen im Verlauf:** Wischen im Terminal (mit tmux scrollt die Maus von tmux).
- Die Leiste klappt mit dem Pfeil ein und lässt am Rand nur einen kleinen Griff stehen; ob sie offen ist, merkt sich die Sitzung bis zum Schließen.

## Sicherheit

- **Der Server baut die Startzeile aus festen Vorlagen und geprüften Feldern.** Programmnamen kommen aus einer festen Liste (`claude`, `codex`),
  das Arbeitsverzeichnis besteht den Zeichensatz oben, der tmux-Name nur aus `[a-z0-9-]`. Jedes Feld, das in die Zeile kommt, wird mit
  Einfach-Anführungszeichen gequotet (`'` wird zu `'\''`), `~` wird als `"$HOME"` aufgelöst. Der Bau prüft **noch einmal**, auch wenn ein Datensatz
  die Prüfung umgangen hätte, und wirft dann statt eine Zeile zu bauen. Getestet mit Semikolon, Backticks, `$()`, Zeilenumbrüchen, Anführungszeichen,
  Leerzeichen und Unicode, auch durch eine echte `sh` (`tests/JulOS.Backend.Tests/SshLaunchTests.cs`).
- **Der „Eigene Befehl“ ist freier Text des Besitzers der Verbindung** und läuft mit seinen Rechten auf seinem Ziel (ohne tmux als `exec sh -c '<Befehl>'`,
  mit tmux als ein Argument für tmux). Er wird **nur aus der gespeicherten Verbindung** genommen, nie aus einer Anfrage: das Ticket für den WebSocket
  kennt kein Programm, und Quick Connect startet immer die Shell. Er steht **nicht im Log** (der Server loggt nur das Programm, zum Beispiel
  „custom“, nie die Startzeile oder die Prüfung). Er darf nur eine Zeile ohne Steuerzeichen sein. Wer die Verbindung bearbeiten kann, kann damit
  alles auf dem Ziel ausführen, was dieser Benutzer dort darf; das ist, was ein SSH-Terminal ohnehin ist.
- Ein Arbeitsverzeichnis, das es nicht gibt, startet das Programm nicht (siehe oben). Das Programm läuft immer als der Benutzer der Verbindung
  auf dem Ziel, nie mit mehr Rechten.
- Die Anmeldung am Ziel, die Host-Schlüssel-Prüfung beim ersten Mal und die Warnung bei geänderten Schlüsseln sind unverändert
  ([ARCHITECTURE.md](ARCHITECTURE.md)). Claude und Codex bekommen von JulOS keine Zugangsdaten.
- Die Prüfung läuft in einem zweiten Kanal derselben SSH-Verbindung (`MaxSessions` 2 statt 1 im Client). Beschränkt ein Ziel die Zahl der Kanäle
  auf einen, schlägt die Prüfung fehl: dann gilt der Rückfall „Prüfung fehlgeschlagen“ mit Hinweis.

## Grenzen

- Die Startzeile wird in die interaktive Shell des Ziels getippt. Eine Shell, die vor dem Prompt etwas abfragt (zum Beispiel eine
  Rückfrage in `~/.bashrc`), liest sie als Antwort. Das ist selten; dann „Eigener Befehl“ mit `tmux` von Hand oder die Rückfrage entfernen.
- Der Prüfbefehl sucht in einer Login-Shell, nicht in einer interaktiven. Steht der `PATH`-Eintrag nur in `~/.bashrc` (Zeile nach dem
  üblichen „nicht interaktiv: aufhören“), findet die Prüfung das Programm nicht, obwohl du es von Hand starten kannst. Abhilfe: den Eintrag in
  `~/.profile` setzen oder „Eigener Befehl“ mit dem vollen Pfad (`~/.local/bin/claude`).
- Wie die Programme intern funktionieren (Betriebsarten, Tastenbelegung) ändert sich mit ihren Versionen; die Schnelltasten sind Voreinstellungen
  ohne Gewähr. Wer andere Tasten will, nutzt das Textfeld oder die Sondertasten-Leiste der Sitzung.
- Ohne tmux endet die Arbeit mit der Verbindung. Das Wiederanhängen gilt nur für tmux-Sitzungen, nicht für gewöhnliche Shells.
- Es gibt weiterhin höchstens acht Sitzungen je Benutzer (alle Arten zusammen), siehe [SESSIONS.md](SESSIONS.md).

## Für Entwickler

| Teil | Datei |
|---|---|
| Prüfung der Felder, Quoting, Prüfbefehl, Startplan | `src/JulOS.Integrations/Ssh/SshLaunch.cs` |
| Exec-Kanal (5 s) | `ISshSession.ExecAsync`, `SshNetSession` |
| Ablauf nach dem Verbinden (Prüfen, Startzeile, Meldung „Bereit“ mit `program`, `keep`, `notice`) | `SshRelay.StartProgramAsync`, `SshProtocol.Ready` |
| Speicherung, API (`program`, `workDir`, `keepAlive`, `customCommand`; `POST /api/connections/{id}/program` legt die Kachel an) | `database/014_connection_program.sql`, `ConnectionStore`, `ConnectionEndpoints` |
| Kacheln, Schnelltasten, Hinweistexte (ohne DOM) | `wwwroot/terminal-programs.js` |
| Eingabeleiste | `wwwroot/terminal-input.js`, `terminal.css` |
| Neuverbinden mit tmux | `wwwroot/terminal-session.js` |

Tests: `tests/JulOS.Backend.Tests/SshLaunchTests.cs` (Prüfung, Quoting, echte `sh` mit Attrappen), `CliSessionTests.cs` (Relay, API, Migration,
Kachel, echtes SSH-Ziel), `tests/frontend/terminal-programs.test.mjs` und `terminal-input.test.mjs`, im Browser `deploy/dev/e2e/tests/e2e.spec.mjs`
(Tests 8 und 9: der Wegwerf-SSH-Container bekommt eine Attrappe `claude` in den `PATH`, `codex` fehlt dort bewusst).
