-- Verlauf der Dateien-App fuer Hosts per SFTP (docs/FILES.md): jede schreibende Aktion (hochladen, umbenennen, loeschen, kopieren, verschieben, Ordner anlegen) und jeder Fehler.
-- Wer (user_id), welcher Host (host_name, der Name der Verbindung von damals), welche Aktion, welcher Pfad (gekuerzt), Ergebnis (ok, failed, denied, cancelled), wann.
-- Nie Inhalte und nie Passwoerter: nur Pfade, Zaehler und kurze Codes. connection_id ist absichtlich kein Fremdschluessel: der Verlauf soll auch ueberleben, wenn die Verbindung geloescht wird.
create table files_audit (
  id             bigint generated always as identity primary key,
  at             timestamptz not null,
  user_id        uuid not null references users (id) on delete cascade,
  connection_id  uuid,
  host_name      text not null,
  action         text not null,
  path           text,
  result         text not null,
  detail         text,
  items          integer
);
create index files_audit_user_idx on files_audit (user_id, id desc);
