-- Ordner fuer Verbindungen, Favoriten und "Zuletzt benutzt" auf dem Server (statt nur im Geraet), Einstellungen je Benutzer (siehe docs/FOLDERS.md).
-- Rueckwaertskompatibel: bestehende Verbindungen bleiben ohne Ordner (folder_id null), keine Favoriten, nie benutzt. Nichts geht verloren.

create table folders (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references users (id) on delete cascade,
  name        text not null,
  icon        text not null default 'folder',
  sort        integer not null default 0,
  created_at  timestamptz not null default now()
);
create index folders_user_idx on folders (user_id, sort, created_at);

-- Ein geloeschter Ordner loest nur die Zuordnung: seine Verbindungen bleiben erhalten (ohne Ordner).
alter table connections add column folder_id uuid references folders (id) on delete set null;
alter table connections add column favorite boolean not null default false;
alter table connections add column last_used_at timestamptz;
alter table connections add column use_count integer not null default 0;
create index connections_folder_idx on connections (folder_id);
