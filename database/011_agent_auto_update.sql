-- Agent-Aktualisierung: je Agent ein Schalter "automatisch aktualisieren" (Standard aus). Ist er an, bittet der Server den Agenten beim Verbinden,
-- sich auf die Version zu bringen, die im Image mitgeliefert wird (siehe docs/AGENT_API.md).
alter table agents add column auto_update boolean not null default false;
