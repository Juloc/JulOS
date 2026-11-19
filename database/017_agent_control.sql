-- Stufe 'control' je Agent (Host-Agent, docs/HOST-AGENT.md): was der Server diesem Agenten ueberhaupt auftragen darf.
-- 'read' (Standard): nur lesen (Systemwerte, Update-Liste, Dienste, Container). 'updates': zusaetzlich Updates installieren. 'full': zusaetzlich Neustart.
-- In dieser Version gibt es nur lesende Anfragen; die Spalte und die Pruefung stehen schon, damit die Schreibaktionen (Stufe 3) daran anschliessen.
-- Der Agent prueft lokal noch einmal (/etc/julos-agent/permissions): beide muessen erlauben.
alter table agents add column control text not null default 'read' check (control in ('read', 'updates', 'full'));
