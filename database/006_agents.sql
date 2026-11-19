-- Agent auf der Ziel-Maschine, der sich von dort aus beim Server meldet (siehe docs/AGENT_API.md).
-- Je Verbindung hoechstens ein Agent. Das Token (256 Bit Zufall) steht nur als SHA-256-Wert in der Tabelle; der Einrichtungscode
-- (einmalig, kurz gueltig) ebenfalls. last_seen, version und platform sind Anzeigewerte fuer die App.
create table agents (
  connection_id  uuid primary key references connections (id) on delete cascade,
  user_id        uuid not null references users (id) on delete cascade,
  token_hash     text not null unique,
  created_at     timestamptz not null default now(),
  last_seen      timestamptz,
  version        text,
  platform       text
);
create index agents_user_idx on agents (user_id);

create table agent_codes (
  code_hash      text primary key,
  connection_id  uuid not null references connections (id) on delete cascade,
  user_id        uuid not null references users (id) on delete cascade,
  expires_at     timestamptz not null
);
create index agent_codes_connection_idx on agent_codes (connection_id);
