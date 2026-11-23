-- Anmelde-Zentrale (OpenID Connect, docs/SIGN-IN.md): JulOS ist die eine Anmelde-Stelle fuer die eigenen Apps.
--   users.oidc_sub       die stabile, nicht erratbare Kennung einer Person ("sub" im Token); nicht der Benutzername, nicht die interne Id
--   oidc_apps            die registrierten Apps: client_id, nur der SHA-256 des Geheimnisses (256 Bit Zufall, nie im Klartext), exakte Rueckkehr-Adressen, die Rollen, die die App kennt
--   oidc_roles           je Person und App eine Rolle; kein Eintrag heisst kein Zugang (der Eigentuemer hat in jeder App admin, ohne Eintrag)
--   oidc_codes           Codes des Authorization-Code-Ablaufs: nur als Hash, einmalig, hoechstens 60 Sekunden, an App, Adresse, PKCE und JulOS-Sitzung gebunden
--   oidc_tokens          die Zugriffs-Token fuer /oidc/userinfo (undurchsichtig, nur als Hash, 5 Minuten)
--   oidc_grants          je JulOS-Sitzung und App, die ein Token bekam: "sid" und wen es betrifft. Endet die Sitzung, geht daran das Logout-Token an die App.
--                        session_id und user_id sind keine Fremdschluessel: der Eintrag muss die Sitzung und das Konto ueberleben, damit die App noch erfaehrt, dass sie endet.
--   oidc_logouts         die Warteschlange der Logout-Token (Back-Channel Logout) mit Wiederholungen und Ergebnis
--   oidc_keys            die Signaturschluessel (ES256): oeffentlicher Teil im Klartext, privater Teil mit dem Server-Schluessel (SecretBox) verschluesselt und nur beim aktiven Schluessel
-- Nichts wird geloescht oder umgeschrieben.
alter table users add column oidc_sub uuid not null default gen_random_uuid();
create unique index users_oidc_sub_key on users (oidc_sub);

create table oidc_apps (
  id                 uuid primary key default gen_random_uuid(),
  name               text not null,
  client_id          text not null unique,
  secret_hash        text not null,
  redirect_uris      text[] not null,
  post_logout_uris   text[] not null default '{}',
  backchannel_uri    text,
  roles              text[] not null,
  enabled            boolean not null default true,
  created_at         timestamptz not null default now(),
  secret_changed_at  timestamptz not null default now()
);
create unique index oidc_apps_name_key on oidc_apps (lower(name));

create table oidc_roles (
  app_id   uuid not null references oidc_apps (id) on delete cascade,
  user_id  uuid not null references users (id) on delete cascade,
  role     text not null,
  primary key (app_id, user_id)
);

create table oidc_codes (
  hash            text primary key,
  app_id          uuid not null references oidc_apps (id) on delete cascade,
  user_id         uuid not null references users (id) on delete cascade,
  session_id      uuid not null,
  redirect_uri    text not null,
  code_challenge  text not null,
  nonce           text,
  scope           text not null,
  auth_time       timestamptz not null,
  expires_at      timestamptz not null,
  used_at         timestamptz
);
create index oidc_codes_expires_idx on oidc_codes (expires_at);

create table oidc_tokens (
  hash        text primary key,
  app_id      uuid not null references oidc_apps (id) on delete cascade,
  user_id     uuid not null references users (id) on delete cascade,
  session_id  uuid not null,
  code_hash   text not null,
  scope       text not null,
  expires_at  timestamptz not null
);
create index oidc_tokens_code_idx on oidc_tokens (code_hash);
create index oidc_tokens_expires_idx on oidc_tokens (expires_at);

create table oidc_grants (
  session_id  uuid not null,
  app_id      uuid not null references oidc_apps (id) on delete cascade,
  user_id     uuid not null,
  sub         uuid not null,
  user_name   text not null,
  sid         text not null,
  issued_at   timestamptz not null default now(),
  primary key (session_id, app_id)
);

create table oidc_logouts (
  id          bigint generated always as identity primary key,
  app_id      uuid not null references oidc_apps (id) on delete cascade,
  sub         uuid not null,
  user_name   text not null,
  sid         text not null,
  reason      text not null,
  created_at  timestamptz not null default now(),
  attempts    integer not null default 0,
  next_at     timestamptz not null default now(),
  done_at     timestamptz,
  result      text
);
create index oidc_logouts_open_idx on oidc_logouts (next_at) where done_at is null;

create table oidc_keys (
  id              uuid primary key default gen_random_uuid(),
  kid             text not null unique,
  alg             text not null,
  public_key      bytea not null,
  private_sealed  bytea,
  created_at      timestamptz not null default now(),
  retired_at      timestamptz
);
-- genau ein aktiver Schluessel (zwei gleichzeitige erste Anfragen erzeugen so nicht zwei)
create unique index oidc_keys_one_active on oidc_keys ((true)) where retired_at is null;
