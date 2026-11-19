-- Hosts: ein Rechner oder Server mit eigenen Verbindungen und (hoechstens) einem eigenen Agenten (siehe docs/HOSTS.md).
-- Eine Verbindung gehoert zu genau einem Host, der Agent hing bisher an einer Verbindung und haengt jetzt am Host.
-- Bestehende Daten gehen nicht verloren: jeder Benutzer mit Verbindungen bekommt einen Standard-Host, dem alle seine Verbindungen
-- gehoeren. Hatte er mehrere Agenten (je Verbindung einen), behaelt der zuletzt gesehene den Standard-Host; jeder weitere bekommt einen
-- eigenen Host mit dem Namen seiner Verbindung, und diese Verbindung zieht dorthin um.

create table hosts (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references users (id) on delete cascade,
  name        text not null,
  sort        integer not null default 0,
  created_at  timestamptz not null default now()
);
create index hosts_user_idx on hosts (user_id, sort, created_at);

alter table connections add column host_id uuid references hosts (id);

insert into hosts (user_id, name) select distinct user_id, 'Mein Rechner' from connections;
update connections c set host_id = h.id from hosts h where h.user_id = c.user_id;
alter table connections alter column host_id set not null;
create index connections_host_idx on connections (host_id);

-- Jede Verbindung hat einen Host: ohne Angabe bekommt sie den ersten des Benutzers (gibt es keinen, wird "Mein Rechner" angelegt).
-- Das gilt auch fuer Zeilen, die ohne host_id kommen, zum Beispiel aus dem Auszug einer aelteren Version (docs/BACKUP.md).
create function connections_default_host() returns trigger as $$
begin
  if new.host_id is null then
    select id into new.host_id from hosts where user_id = new.user_id order by sort, created_at, id limit 1;
    if new.host_id is null then
      insert into hosts (user_id, name) values (new.user_id, 'Mein Rechner') returning id into new.host_id;
    end if;
  end if;
  return new;
end;
$$ language plpgsql;
create trigger connections_default_host before insert on connections for each row execute function connections_default_host();

-- Agenten: Schluessel ist jetzt der Host. hostname und os meldet der Agent selbst (reine Anzeigewerte).
alter table agents add column host_id uuid references hosts (id) on delete cascade;
alter table agents add column hostname text;
alter table agents add column os text;

do $$
declare
  r record;
  h uuid;
begin
  for r in
    select a.connection_id, c.user_id, c.name, c.host_id as default_host,
           row_number() over (partition by c.user_id order by a.last_seen desc nulls last, a.created_at) as rn
    from agents a join connections c on c.id = a.connection_id
  loop
    if r.rn = 1 then
      h := r.default_host;
    else
      insert into hosts (user_id, name, sort) values (r.user_id, r.name, r.rn::integer - 1) returning id into h;
      update connections set host_id = h where id = r.connection_id;
    end if;
    update agents set host_id = h where connection_id = r.connection_id;
  end loop;
end $$;

alter table agents drop column connection_id;
alter table agents alter column host_id set not null;
alter table agents add primary key (host_id);

-- Offene Einrichtungscodes gelten nur zehn Minuten: sie werden nicht uebernommen, ein neuer Code ist schnell erzeugt.
delete from agent_codes;
alter table agent_codes drop column connection_id;
alter table agent_codes add column host_id uuid not null references hosts (id) on delete cascade;
create index agent_codes_host_idx on agent_codes (host_id);
