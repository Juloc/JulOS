# Checkliste: Claude Code und Codex auf deinen Geräten

Eine Liste, ein Abschnitt je Gerät: ob es sich lohnt, die Befehle, die du dort selbst tippst, und was danach in JulOS einzutragen ist.
Hintergründe, Quellen und die Sicherheitsregeln stehen in [AI-CLI-SETUP.md](AI-CLI-SETUP.md) (Englisch), das Skript in
[`tools/julos-setup-ai-cli.sh`](../tools/julos-setup-ai-cli.sh), die Bedienung der Programm-Sitzungen in [CLI-SESSIONS.md](CLI-SESSIONS.md).

Regeln für alles hier:

- **Du tippst alle Passwörter selbst.** Das Skript liest, speichert und setzt keine (sudo fragt von selbst im Terminal).
- **Anmeldung bei Claude und Codex bleibt auf dem Gerät.** JulOS speichert davon nichts; nur das SSH-Passwort des Kontos wie bei jeder Terminal-Verbindung.
- **Kein „dangerously skip permissions“**, kein „danger-full-access“, keine Schlüssel in `docker-compose.yml` oder `.env` (Begründung: AI-CLI-SETUP.md, Abschnitt 5).
- **Erst Trockenlauf, dann `--yes`.** Ohne `--yes` ändert das Skript nichts.
- Die Adressen deiner Geräte stehen bewusst **nicht** in diesem Repository (Regel 3 in `CLAUDE.md`). Unten stehen Platzhalter wie `<docker-vm>`; setze die
  echte Adresse selbst ein.

Das Skript bekommst du, bis JulOS es selbst ausliefert (Plan, AI-CLI-SETUP.md Abschnitt 10), von einem Rechner, der den JulOS-Ordner hat (zum Beispiel
Gerät 1), mit `scp` (du gibst das Passwort des Zielkontos ein):

```sh
scp <JulOS-Ordner>/tools/julos-setup-ai-cli.sh <benutzer>@<ziel-adresse>:
```

## Übersicht

| # | Gerät | Empfohlen | Kurz |
|---|---|---|---|
| 1 | Entwicklungs-VM (Debian 13, Desktop mit Claude Desktop und Codex) | **ja** | läuft schon mit Codex; `claude` für die Kommandozeile fehlt noch; zuerst hier, es ist der Testfall |
| 2 | Docker-VM (Reverse-Proxy, Mediendienste, JulOS) | **ja, mit eigenem Konto `ai`** | dort liegen die Projekte und Container; aber nie im Konto mit Docker-Rechten |
| 3 | Proxmox-Host | **nein auf dem Host**, ja in einem eigenen LXC | der Hypervisor hat Zugriff auf alle VMs und Platten |
| 4 | Home Assistant OS | **nein** | kein normales Linux, die Installation überlebt keine Updates; besser mit einer Kopie der Konfiguration arbeiten |
| 5 | NAS (vermutlich Synology) | **nein** | eingeschränktes System, Daten der ganzen Familie; besser Freigaben auf einem anderen Gerät einbinden |

Empfohlene Reihenfolge: **1, dann 2, dann (optional) der LXC aus 3.** 4 und 5 überspringen.

---

## Gerät 1: Entwicklungs-VM (Debian 13, x86_64, hier laufen Claude Desktop und Codex)

**Empfohlen: ja.** Hier arbeitest du ohnehin mit beiden Werkzeugen; mit JulOS kommst du auch vom Handy dran, und die tmux-Sitzung bleibt bei Netzabriss
erhalten. Stand beim Prüfen (nur gelesen): `codex` 0.166.0 liegt in `~/.local/bin` (neuere Version vorhanden), `claude` ist **nicht** im `PATH` (Claude Desktop bringt
ein eigenes Binary mit, das keine Kommandozeile für JulOS ist), `tmux`, `git`, `gh`, `ripgrep`, `bubblewrap` und `socat` sind da, SSH-Server läuft, Locale UTF-8, Zeitzone Europe/Berlin.
Die Ordner `~/.claude` und `~/.codex` sind mit 775 zu offen (das Skript setzt 700). Die Maschine ist oft voll ausgelastet (Arbeitsspeicher ~10 GB): nicht
mehrere Sitzungen gleichzeitig.

Konto: hier das vorhandene Entwicklerkonto (es ist ohnehin das, mit dem Desktop-Claude und Codex laufen). Standard-Berechtigungsmodus beibehalten.

Auf dem Gerät, im Terminal:

```sh
cd <JulOS-Ordner>
sh tools/julos-setup-ai-cli.sh --check-network      # Trockenlauf: was ist da, was fehlt, sind die Server erreichbar
sh tools/julos-setup-ai-cli.sh --yes --claude       # installiert nur Claude Code (prüft Summe und GPG-Signatur), setzt PATH und tmux-Block
codex update                                        # Codex auf den neuesten Stand (optional)
claude auth login                                   # Anmeldung: URL am Handy öffnen, Code ins Terminal einfügen (AI-CLI-SETUP.md Abschnitt 3)
codex login status                                  # zeigt, ob Codex angemeldet ist
claude auth status
```

Danach in JulOS (Verbindung auf diese VM, Art **Terminal (SSH)**; gibt es schon eine, Stift „Verbindung bearbeiten“ und „Kachel „Claude“ anlegen“):

- Verbindung: Art Terminal (SSH), Adresse `<dev-vm>`, Port 22, Benutzer = dein Entwicklerkonto, Passwort wie bei jeder SSH-Verbindung.
- Startprogramm **Claude Code** (zweite Kachel **Codex**), Arbeitsverzeichnis `~/projects/<projekt>` (nicht `~`), „Sitzung am Leben halten (tmux)“ **an**.
- Beim ersten Verbinden den Host-Schlüssel bestätigen.

---

## Gerät 2: Docker-VM (Debian, Reverse-Proxy, Mediendienste, JulOS)

**Empfohlen: ja, aber nur in einem eigenen Konto `ai`.** Hier liegen deine Compose-Projekte; Claude und Codex sind dort nützlich für Konfiguration und
Fehlersuche. Aber: wer in der Gruppe `docker` ist, ist praktisch root. Deshalb:

- Das Konto `ai` **nicht** in die Gruppe `docker`, **nicht** in `sudo`.
- Die Compose-Ordner, `.env`-Dateien und Schlüssel mit den Rechten deines Admin-Kontos lassen (Modus 600/700), damit `ai` sie nicht lesen kann.
- Arbeiten am laufenden Stack machst du selbst oder liest nur mit (Modus `plan`); `ai` arbeitet in Klonen unter `~/work`.

Voraussetzung: dein Admin-Konto auf dieser VM hat `sudo`. Befehle auf dieser VM (per SSH oder an der Konsole):

```sh
sh julos-setup-ai-cli.sh --user ai                  # Trockenlauf: zeigt Pakete, Konto und Schritte
sh julos-setup-ai-cli.sh --user ai --yes            # installiert fehlende Pakete (sudo fragt), legt das Konto ai an, installiert beide Werkzeuge für ai
sudo passwd ai                                      # Passwort für ai setzen (du tippst es)
sudo -u ai -H sh -c 'mkdir -p ~/work && cd ~/work && git clone <repo-adresse>'    # ein Projekt als Klon
sudo -u ai ls <compose-ordner>/.env                 # Kontrolle: muss "Permission denied" melden; sonst: chmod 600 <compose-ordner>/.env
```

Anmeldung als `ai` (SSH oder JulOS-Shell-Sitzung als `ai`, am besten schon vom Handy aus, damit der Browser dort ist):

```sh
claude auth login        # URL am Handy öffnen, Code mit „Einfügen“ ins Terminal geben
codex login --device-auth   # URL am Handy öffnen, kurzen Code eingeben
claude auth status
codex login status
```

Danach in JulOS:

- Verbindung: Art **Terminal (SSH)**, Adresse = die **LAN-Adresse dieser VM** (nicht `127.0.0.1`: JulOS läuft hier in einem Container und käme sonst nur an sich selbst),
  Port 22, Benutzer `ai`, das eben gesetzte Passwort. Host-Schlüssel beim ersten Verbinden bestätigen.
- Kachel **Claude** und Kachel **Codex**: Startprogramm wählen, Arbeitsverzeichnis `~/work/<projekt>`, tmux **an**.
- Wenn der SSH-Server dieser VM Passwortanmeldung für `ai` nicht zulässt, trage in `sshd_config` nichts selbst um (das ist eine Sicherheits-Einstellung):
  nimm erst die Passwort-Anmeldung, ein Schlüssel-Login ist in JulOS noch nicht vorgesehen (siehe `IDEAS.md`).

---

## Gerät 3: Proxmox-Host (Hypervisor)

**Empfohlen: nein auf dem Host, ja in einem eigenen Container (LXC) oder einer kleinen VM.** Der Host kann jede VM und jede Platte lesen und löschen; ein Agent
mit Shell dort wäre der denkbar größte Hebel bei einem Fehler oder einer Prompt-Injection. Ein LXC „ai-box“ ohne eingebundene Host-Ordner kostet wenig und
isoliert die Arbeit. Dort läuft dann dasselbe wie auf Gerät 2, auch mit dem Konto `ai`.

Vorschlag: Debian-13-Vorlage, **unprivilegiert**, 2 Kerne, **4096 MB** RAM (Claude Code nennt 4 GB als Minimum), 512 MB Swap, 12 GB Platte, DHCP, keine
Bind-Mounts. In der Weboberfläche: „CT-Vorlagen“ (Debian 13 laden), dann „CT erstellen“, Häkchen „Unprivilegierter Container“. Das Root-Passwort des Containers
setzt du dort selbst. Als Befehl am Host (Namen der Vorlage vorher mit `pveam available` prüfen; **nicht getestet**):

```sh
pct create <id> local:vztmpl/<debian-13-vorlage> --hostname ai-box --unprivileged 1 --cores 2 --memory 4096 --swap 512 \
  --rootfs local-lvm:12 --net0 name=eth0,bridge=vmbr0,ip=dhcp --features nesting=1 --start 1
```

(`nesting=1` kann nötig sein, damit der Sandbox-Helfer `bubblewrap` Namensräume anlegen kann; ohne ihn arbeiten die Werkzeuge mit einer Warnung ohne Sandbox weiter.
Nicht getestet.)

In der Konsole des Containers (als root, am Handy die Proxmox-Konsole oder vom PC aus):

```sh
apt-get update && apt-get install -y curl sudo openssh-server
scp <benutzer>@<dev-vm>:<JulOS-Ordner>/tools/julos-setup-ai-cli.sh .     # zieht das Skript von Gerät 1; du gibst das Passwort ein
sh julos-setup-ai-cli.sh --user ai                  # Trockenlauf
sh julos-setup-ai-cli.sh --user ai --yes
passwd ai                                           # Passwort für ai
```

Anmeldung bei Claude und Codex als `ai`, und die Einträge in JulOS: wie bei Gerät 2 (Adresse = die des Containers, Benutzer `ai`, Arbeitsverzeichnis `~/work/<projekt>`, tmux an).
Der Container sollte nur ausgehend ins Internet und zu deinen Git-Servern kommen, nicht in dein Verwaltungsnetz (Firewall des Containers oder VLAN, wenn du eins hast).

---

## Gerät 4: Home Assistant OS

**Empfohlen: nein.** Home Assistant OS ist kein normales Linux; auf der Weboberfläche und im SSH-Add-on läuft alles in Containern, die bei Updates neu
entstehen, und eine Installation dort (Alpine mit musl, kein `sudo`, kein dauerhafter Ort für `~/.local`) hielte nicht und wäre nur schwer abzusichern.
Eine Änderung an der Konfiguration durch einen Agenten kann außerdem Automatisierungen oder die ganze Installation unbrauchbar machen.

Besser: **auf Gerät 2 oder im LXC aus Gerät 3 an einer Kopie arbeiten** und nur geprüfte Änderungen zurückspielen. Beispiel (nicht getestet; Platzhalter ersetzen,
`secrets.yaml` bleibt draußen), als `ai` auf der Arbeitsmaschine:

```sh
mkdir -p ~/work/ha-config && cd ~/work/ha-config
git init -q   # Verlauf, damit du jede Änderung siehst und zurücknehmen kannst
rsync -a --delete --exclude secrets.yaml --exclude '.storage/' --exclude '*.db' <ha-benutzer>@<home-assistant>:/config/ ./
```

Der Agent ändert dort, du prüfst mit `git diff` und kopierst Dateien selbst zurück (oder startest die Konfigurationsprüfung von Home Assistant, bevor du neu lädst). In JulOS
richtest du für Home Assistant höchstens eine normale **Shell**-Verbindung (Art Terminal, Startprogramm „Shell“) zum SSH-Add-on ein, ohne Claude oder Codex.

---

## Gerät 5: NAS (vermutlich Synology)

**Empfohlen: nein.** Ein NAS-Betriebssystem ist ein geschlossenes System mit eigenem Paketweg: Claude Code und Codex sind dafür weder gedacht noch getestet (ältere
C-Bibliothek, kein normaler Paketmanager, Updates des Systems löschen Eigenes), und ein Fehler träfe die Daten der ganzen Familie. Es gibt dort **keine** sinnvolle Kommandozeilen-Einrichtung in
diesem Projekt. Wenn die Werkzeuge Dateien vom NAS brauchen:

- Freigabe **schreibgeschützt** auf Gerät 2 oder im LXC einbinden (NFS oder SMB), und zwar mit einem NAS-Konto, das nur diese Freigabe lesen darf; oder
- die Dateien dorthin kopieren und an der Kopie arbeiten.

Eine Shell-Verbindung (Art Terminal, Startprogramm „Shell“) zum NAS in JulOS ist unabhängig davon möglich, wenn du dort SSH aktiviert hast (nur bei Bedarf einschalten).

---

## Wenn etwas nicht klappt

| Meldung in JulOS beim Start der Kachel | Ursache | Abhilfe |
|---|---|---|
| „claude ist auf diesem Rechner nicht installiert oder nicht im PATH“ | Werkzeug fehlt, oder der `PATH` steht nur in `~/.bashrc` | Skript noch einmal mit `--yes` laufen lassen (setzt den Block in `~/.profile`); Kontrolle: `sh -lc 'command -v claude'` |
| „Das Arbeitsverzeichnis … gibt es auf diesem Rechner nicht“ | Ordner fehlt oder falsch geschrieben | `mkdir -p ~/work/<projekt>`; die Kachel startet bewusst keine Programme im falschen Ordner |
| „tmux ist auf diesem Rechner nicht installiert“ | tmux fehlt | `sh julos-setup-ai-cli.sh --yes` oder Paket `tmux` installieren |
| „Das Programm ließ sich auf dem Rechner nicht prüfen“ | Prüfung dauerte länger als 5 s oder der SSH-Server erlaubt nur einen Kanal | Erneut versuchen; in `sshd_config` `MaxSessions` mindestens 2 (siehe CLI-SESSIONS.md) |
| Das Programm fragt nach Anmeldung | noch nicht angemeldet, oder die Anmeldung ist abgelaufen | `claude auth login` oder `codex login --device-auth` in einer Shell-Sitzung |

Nicht verifiziert: die `pct`-Zeile und die Schritte im Proxmox-Abschnitt, der `rsync`-Weg für Home Assistant, das Verhalten auf Synology; das Skript wurde nur auf
dieser Entwicklungs-VM (Trockenlauf) und in Wegwerf-Containern (Debian 13, Ubuntu 24.04, Alpine) getestet, nicht auf Gerät 2.
