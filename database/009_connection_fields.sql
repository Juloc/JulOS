-- Weitere Felder je Verbindung: Domaene (nur Desktop/RDP), Notizen und Tastaturlayout des Ziels (nur Desktop/RDP, Standard Deutsch wie bisher fest eingestellt).
-- Rueckwaertskompatibel: alle drei haben Standardwerte, bestehende Verbindungen aendern ihr Verhalten nicht, und ein Auszug aus einer aelteren Version
-- laesst sich einspielen (docs/BACKUP.md). Die erlaubten Layouts prueft der Server (Logic/KeyboardLayouts.cs), nicht die Datenbank.
alter table connections add column domain text not null default '';
alter table connections add column notes text not null default '';
alter table connections add column keyboard_layout text not null default 'de';
