-- Push-Meldungen ohne KI (docs/PUSH.md): Web Push ueber die PWA und feste Regeln fuer die Hosts.
--   push_vapid          das Schluesselpaar des Servers (VAPID, RFC 8292), genau eine Zeile, beim ersten Einschalten erzeugt. Der private Teil ist mit dem Server-Schluessel
--                       (SecretBox, AES-256-GCM) verschluesselt, der Schluessel liegt nicht in der Datenbank. Der oeffentliche Teil geht an den Browser.
--   push_subscriptions  ein Abo je Geraet und Browser (Endpunkt des Push-Dienstes und die Schluessel des Browsers), gehoert einem Benutzer. Ein toter Endpunkt (404/410) wird entfernt.
--                       origin: die Adresse, unter der die App beim Anlegen geoeffnet war (wird als Absender "sub" der VAPID-Kennung gebraucht).
--   alert_settings      Schalter und Schwellen der Regeln je Benutzer (JSON, vom Server geprueft).
--   alert_state         der letzte Zustand je Host (JSON): daran erkennt die Auswertung, was neu ist. Nichts davon geht an den Browser.
--   alert_log           die letzten Meldungen (was, wann, welcher Host); push: pending (noch nicht zugestellt), sent, none (kein Geraet), dropped (zu alt geworden).
--                       host_id ist kein Fremdschluessel: die Meldung bleibt lesbar, auch wenn der Host geloescht wird.
create table push_vapid (
  id              boolean primary key default true check (id),
  public_key      bytea not null,
  private_sealed  bytea not null,
  created_at      timestamptz not null default now()
);

create table push_subscriptions (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references users (id) on delete cascade,
  endpoint    text not null unique,
  p256dh      bytea not null,
  auth        bytea not null,
  label       text not null,
  origin      text not null,
  created_at  timestamptz not null default now(),
  last_ok     timestamptz
);
create index push_subscriptions_user_idx on push_subscriptions (user_id);

create table alert_settings (
  user_id     uuid primary key references users (id) on delete cascade,
  settings    jsonb not null,
  updated_at  timestamptz not null default now()
);

create table alert_state (
  user_id  uuid not null references users (id) on delete cascade,
  host_id  uuid not null references hosts (id) on delete cascade,
  state    jsonb not null,
  primary key (user_id, host_id)
);

create table alert_log (
  id         bigint generated always as identity primary key,
  user_id    uuid not null references users (id) on delete cascade,
  at         timestamptz not null default now(),
  host_id    uuid,
  host_name  text not null,
  rule       text not null,
  kind       text not null,
  detail     text,
  push       text not null default 'pending'
);
create index alert_log_user_idx on alert_log (user_id, id desc);
