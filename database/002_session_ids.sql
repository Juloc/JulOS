-- Stabile Kennung einer Sitzung: bleibt gleich, auch wenn das Cookie erneuert wird (der Hash aendert sich dabei).
-- Offene Remote-Verbindungen pruefen damit, ob ihre Sitzung noch gilt.
alter table sessions add column id uuid not null default gen_random_uuid();
create unique index sessions_id_idx on sessions (id);
