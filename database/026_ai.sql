-- KI-Zugang (docs/AI-GATEWAY.md): Anbieter, Apps mit ihren Schluesseln und der Verbrauch je Tag. Neue Tabellen, nichts Vorhandenes wird veraendert.
--   ai_providers  ein Eintrag je Anbieter (codex, openai, anthropic). sealed_key: der API-Schluessel, verschluesselt mit dem Geheimnis des Servers (SecretBox), nie im Klartext.
--   ai_apps       die Apps der Familie, die KI ueber JulOS benutzen. key_hash: SHA-256 ihres Schluessels (der Schluessel selbst wird nur einmal angezeigt und nie gespeichert),
--                 key_hint: die letzten vier Zeichen zum Wiedererkennen. allowed: erlaubte Anbieter oder Modelle ("openai" oder "openai/gpt-4.1"). Budgets in Tokens, leer = ohne Grenze.
--   ai_usage      nur Zaehler je Tag, App, Anbieter und Modell: Anfragen, Ergebnisse, Tokens rein und raus, Dauer. Nie ein Inhalt einer Anfrage oder Antwort.
-- Die Schalter (KI an oder aus, Standardmodell) stehen in instance_settings (ai.enabled, ai.defaultModel).
create table ai_providers (
  id          text primary key check (id in ('codex', 'openai', 'anthropic')),
  enabled     boolean not null default false,
  sealed_key  bytea,
  updated_at  timestamptz not null default now()
);

create table ai_apps (
  id            uuid primary key default gen_random_uuid(),
  name          text not null,
  key_hash      bytea not null unique,
  key_hint      text not null,
  active        boolean not null default true,
  allowed       text[] not null,
  daily_tokens  bigint check (daily_tokens is null or daily_tokens > 0),
  monthly_tokens bigint check (monthly_tokens is null or monthly_tokens > 0),
  rate_per_minute integer not null default 30 check (rate_per_minute between 1 and 600),
  allow_files   boolean not null default false,
  created_at    timestamptz not null default now(),
  last_used_at  timestamptz
);
create unique index ai_apps_name on ai_apps (lower(name));

create table ai_usage (
  day         date not null,
  app_id      uuid not null references ai_apps (id) on delete cascade,
  provider    text not null,
  model       text not null,
  requests    integer not null default 0,
  ok          integer not null default 0,
  failed      integer not null default 0,
  aborted     integer not null default 0,
  tokens_in   bigint not null default 0,
  tokens_out  bigint not null default 0,
  millis      bigint not null default 0,
  primary key (day, app_id, provider, model)
);
create index ai_usage_app on ai_usage (app_id, day);
