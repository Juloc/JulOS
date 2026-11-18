-- Optional gespeichertes Passwort einer Verbindung, verschluesselt (AES-256-GCM, Schluessel nur in der Server-Einstellung, nie in der Datenbank).
alter table connections add column secret bytea;
