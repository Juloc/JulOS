-- Verlauf der DNS-Aenderungen durch das Modul DDNS (docs/MODULES.md): wann welcher Name von welcher Adresse auf welche gesetzt wurde, ausgeloest per Knopf
-- oder automatisch. Nur Adressen und Namen aus der Einstellung, keine Zugangsdaten. Die Tabelle gehoert dem Server, nicht einem Benutzer; es bleiben die letzten 200 Zeilen.
create table ddns_history (
  id          bigserial primary key,
  at          timestamptz not null default now(),
  host        text not null,
  record_type text not null,
  old_ip      text,
  new_ip      text not null,
  trigger     text not null,
  ok          boolean not null,
  message     text not null default ''
);
create index ddns_history_at_idx on ddns_history (at desc);
