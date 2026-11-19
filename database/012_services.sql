-- Dienste (Modul "Dienste", docs/MODULES.md): ein eigener Dienst im Heimnetz mit Adresse zum Oeffnen, optionaler Gesundheitsadresse
-- und optionaler JSON-Kennzahlen-Adresse. Der API-Schluessel steht nur verschluesselt in secret (SecretBox, die Dienst-Id ist "zugehoerige Daten")
-- und wird nie ausgeliefert. Neue Tabelle, nichts Bestehendes aendert sich.
create table services (
  id               uuid primary key default gen_random_uuid(),
  user_id          uuid not null references users (id) on delete cascade,
  name             text not null,
  icon             text not null default 'globe',
  open_url         text not null default '',
  health_url       text not null default '',
  metrics_url      text not null default '',
  metrics          jsonb not null default '[]'::jsonb,
  interval_seconds integer not null default 60,
  header_name      text not null default '',
  secret           bytea,
  template         text not null default '',
  sort             integer not null default 0,
  created_at       timestamptz not null default now()
);
create index services_user_idx on services (user_id, sort, created_at);
