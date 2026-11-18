-- Welche Bildschirm-Engine eine Verbindung benutzt: 'guac' (Guacamole, Standard) oder 'stream' (eigener RDP-Client mit H.264).
alter table connections add column engine text not null default 'guac' check (engine in ('guac', 'stream'));
