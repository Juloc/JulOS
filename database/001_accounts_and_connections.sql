-- Konten, Wiederherstellungscodes, Sitzungen und gespeicherte Verbindungen.
-- Es wird nie ein Passwort im Klartext gespeichert; Sitzungen stehen nur als SHA-256-Wert in der Tabelle.

create table users (
  id              uuid primary key default gen_random_uuid(),
  name            text not null,
  password_hash   text not null,
  totp_secret     text not null,
  totp_confirmed  boolean not null default false,
  totp_last_step  bigint not null default 0,
  created_at      timestamptz not null default now()
);
create unique index users_name_key on users (lower(name));

create table recovery_codes (
  user_id    uuid not null references users (id) on delete cascade,
  code_hash  text not null,
  primary key (user_id, code_hash)
);

create table sessions (
  hash        text primary key,
  user_id     uuid not null references users (id) on delete cascade,
  created_at  timestamptz not null,
  last_seen   timestamptz not null,
  rotated_at  timestamptz not null,
  prev_hash   text,
  prev_until  timestamptz,
  user_agent  text,
  ip          text
);
create index sessions_user_idx on sessions (user_id);
create index sessions_prev_idx on sessions (prev_hash) where prev_hash is not null;

create table connections (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references users (id) on delete cascade,
  name        text not null,
  kind        text not null check (kind in ('desktop', 'terminal', 'screen')),
  host        text not null,
  port        integer not null check (port between 1 and 65535),
  username    text not null default '',
  sort        integer not null default 0,
  created_at  timestamptz not null default now()
);
create index connections_user_idx on connections (user_id, sort);
