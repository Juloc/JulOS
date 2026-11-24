-- Passkeys (docs/PASSKEYS.md): Anmelden und Freigeben mit WebAuthn, neben Passwort und Authenticator-App.
--   passkeys            ein Eintrag je Passkey einer Person. Gespeichert wird nur, was die Pruefung braucht: Credential-Id und oeffentlicher Schluessel
--                       (der private bleibt im Geraet), Zaehler, Name, AAGUID (Art des Authenticators), Backup-Merker (synchronisierbar, gesichert) und Zeiten.
--                       Keine Attestation, kein User-Handle (er wird aus users.id abgeleitet), nichts Biometrisches.
--   passkey_challenges  offene Herausforderungen. Gespeichert wird nur der SHA-256 der 32 Byte, nie die Herausforderung selbst; sie gilt 5 Minuten und wird beim
--                       Pruefen geloescht (delete ... returning), gilt also hoechstens einmal. user_id und session_id binden Registrieren und Freigabe an die Sitzung, die sie angefordert hat.
--   sessions.strength   womit sich die Sitzung angemeldet hat: 'password' (Passwort mit Code oder Wiederherstellungscode) oder 'passkey'. Daraus entsteht amr im ID-Token der Anmelde-Zentrale.
-- Nichts Vorhandenes wird umgeschrieben: alle bisherigen Sitzungen sind 'password'.
create table passkeys (
  id              uuid primary key default gen_random_uuid(),
  user_id         uuid not null references users (id) on delete cascade,
  credential_id   bytea not null unique check (octet_length(credential_id) between 16 and 1023),
  public_key      bytea not null,
  sign_count      bigint not null default 0 check (sign_count >= 0),
  name            text not null check (char_length(name) between 1 and 60),
  aaguid          uuid,
  backup_eligible boolean not null default false,
  backed_up       boolean not null default false,
  created_at      timestamptz not null default now(),
  last_used_at    timestamptz
);
create index passkeys_user_idx on passkeys (user_id, created_at);

create table passkey_challenges (
  hash        bytea primary key,
  purpose     text not null check (purpose in ('register', 'login', 'step_up')),
  user_id     uuid references users (id) on delete cascade,
  session_id  uuid,
  expires_at  timestamptz not null
);
create index passkey_challenges_expires_idx on passkey_challenges (expires_at);

alter table sessions add column strength text not null default 'password' check (strength in ('password', 'passkey'));
