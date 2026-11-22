-- Arbeitsplatz je Geraet (docs/MULTI-SCREEN.md): welche Bildschirme (Browserfenster) und Fenster es gibt und wo sie liegen, damit beim naechsten Oeffnen
-- auf demselben Geraet alles wieder da ist und "auf dem Handy weiterarbeiten" die offenen Verbindungen eines anderen Geraets findet.
--   device_id  vom Browser erzeugt und lokal gehalten (zufaellig); kein Geheimnis, es gibt keinen Zugriff: jede Anfrage braucht die Sitzung, der Schluessel enthaelt den Benutzer.
--   doc        das Dokument (Bildschirme, Fenster, Fokus, Dock), vom Server geprueft und neu geschrieben (WorkspaceRules). Keine Zugangsdaten, nur Kennungen der Verbindungen.
--   version    zaehlt bei jedem Speichern hoch: ein zweites Browserfenster desselben Geraets mit altem Stand wird erkannt (nur speichern, wenn die Version stimmt).
-- Neue Tabelle, nichts Vorhandenes wird veraendert. Mit dem Benutzer verschwindet sein Arbeitsplatz (on delete cascade); das Backup nimmt jede Tabelle des Schemas mit.
create table workspaces (
  user_id     uuid not null references users (id) on delete cascade,
  device_id   text not null,
  name        text not null default '',
  doc         jsonb not null,
  version     integer not null default 1,
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now(),
  primary key (user_id, device_id)
);
