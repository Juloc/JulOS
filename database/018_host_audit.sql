-- Verlauf der Aktionen auf Hosts (Host-Agent, Stufe 3, docs/HOST-AGENT.md): jede Anfrage, jede Ablehnung und jedes Ergebnis. Wer (user_id), welcher Host, welche Aktion,
-- welches Ziel, was geschah (event + detail), wann. Kein Geheimnis: weder Token noch Bestaetigungswerte noch Inhalte von Logs oder Ausgaben, nur kurze Codes.
-- host_id ist absichtlich kein Fremdschluessel: der Verlauf soll auch ueberleben, wenn der Host geloescht wird (host_name merkt den Namen von damals).
create table host_audit (
  id         bigint generated always as identity primary key,
  at         timestamptz not null,
  user_id    uuid not null references users (id) on delete cascade,
  host_id    uuid not null,
  host_name  text not null,
  action     text not null,
  target     text,
  event      text not null,
  detail     text,
  job        text
);
create index host_audit_host_idx on host_audit (user_id, host_id, id desc);
