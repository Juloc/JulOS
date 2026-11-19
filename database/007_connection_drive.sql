-- Laufwerksumleitung (nur Stream-Engine): ob ein Ordner des Servers als Laufwerk in die Sitzung gereicht wird und ob dort geschrieben werden darf.
-- Beides ist je Verbindung ausgeschaltet; die Wurzel des Ordners steht nie in der Datenbank (Server-Einstellung JULOS_DRIVE_ROOT).
-- Schreiben gibt es nur zusammen mit dem Laufwerk.
alter table connections add column drive_share boolean not null default false;
alter table connections add column drive_write boolean not null default false check (not drive_write or drive_share);
