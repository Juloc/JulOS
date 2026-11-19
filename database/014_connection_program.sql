-- Programm-Sitzungen (docs/CLI-SESSIONS.md): eine Terminal-Verbindung kann statt der Shell ein Programm starten (Claude Code, Codex oder einen eigenen Befehl),
-- in einem Arbeitsverzeichnis und auf Wunsch in einer tmux-Sitzung, die ein Neuverbinden uebersteht.
-- Rueckwaertskompatibel: alle vier Spalten haben Standardwerte (program 'shell' = Verhalten wie bisher), und ein Auszug aus einer aelteren Version
-- laesst sich einspielen (docs/BACKUP.md). Was erlaubt ist, prueft der Server (Integrations/Ssh/SshLaunch.cs), nicht die Datenbank.
alter table connections add column program text not null default 'shell';
alter table connections add column workdir text not null default '';
alter table connections add column keep_alive boolean not null default false;
alter table connections add column custom_command text not null default '';
