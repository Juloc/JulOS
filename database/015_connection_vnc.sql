-- Neue Art von Verbindung: 'vnc' (Desktop ueber VNC, siehe docs/VNC.md). Die Pruefung der Art kam mit der ersten Tabelle und hat den Standardnamen.
alter table connections drop constraint connections_kind_check;
alter table connections add constraint connections_kind_check check (kind in ('desktop', 'terminal', 'screen', 'vnc'));
