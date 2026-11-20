-- Sicherheit (docs/SECURITY-REVIEW.md, F-01, F-02, F-03, F-04, F-10; docs/ARCHITECTURE.md, "Einstellungen der Instanz"):
--   instance_settings  Einstellungen der ganzen Instanz (nicht je Benutzer), ein JSON-Wert je Schluessel, vom Server geprueft (InstanceSettings).
--                      Eine Umgebungsvariable ist nur der Rueckfall, solange hier nichts steht. Keine Geheimnisse ausser dem umhuellten Schluessel (backup.recovery).
--   security_audit     Verlauf der Aenderungen an diesen Einstellungen und der Freigaben (Step-up): wer, was, wann. Nie ein Code, nie ein Schluessel.
--   sessions.step_up_until / step_up_once
--                      Bis wann die Sitzung fuer Aktionen freigegeben ist (frischer Code aus der Authenticator-App); once: die Freigabe gilt fuer genau eine Aktion ("immer fragen").
-- Nichts wird geloescht oder umgeschrieben; users.totp_secret wird beim Start verschluesselt (TotpMigration), nicht hier.
create table instance_settings (
  key         text primary key,
  value       jsonb not null,
  updated_at  timestamptz not null default now()
);

create table security_audit (
  id         bigserial primary key,
  at         timestamptz not null default now(),
  user_name  text not null,
  event      text not null,
  detail     text
);

alter table sessions add column step_up_until timestamptz;
alter table sessions add column step_up_once boolean not null default false;
