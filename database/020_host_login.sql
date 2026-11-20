-- Ein Login je Host (docs/HOSTS.md, "Login"): Benutzername und (verschluesseltes) Passwort einmal am Host, fuer Terminal, Dateien (SFTP) und Desktop-Verbindungen dieses Hosts.
-- Das Passwort ist wie das einer Verbindung mit AES-256-GCM verschluesselt (SecretBox, Schluessel nicht in der Datenbank); die Host-Kennung steckt als zugehoerige Daten darin.
--   login_user     Benutzer des Host-Logins (null: der Host hat keinen Login)
--   login_secret   das verschluesselte Passwort (null: nur ein Benutzer, das Passwort wird gefragt)
--   login_targets  die Adressen (klein geschrieben), fuer die das Passwort gilt: die der Verbindungen zur Zeit, als es gesetzt wurde. Eine Verbindung mit anderer Adresse bekommt
--                  das Passwort nicht (sonst koennte eine neue Verbindung zu einem fremden Ziel es abholen).
--   login_checked  die Uebernahme der bisherigen Zugangsdaten der Verbindungen (HostLoginMigration beim Start) ist fuer diesen Host erledigt
-- Eine Verbindung mit own_login = true nimmt immer ihren eigenen Benutzer und ihr eigenes Passwort ("Eigenen Benutzer verwenden"), sonst gilt der Host-Login, wenn es einen gibt.
-- Nichts wird geloescht oder umgeschrieben: die Passwoerter der Verbindungen bleiben, wie sie sind.
alter table hosts add column login_user text;
alter table hosts add column login_secret bytea;
alter table hosts add column login_targets text[] not null default '{}';
alter table hosts add column login_checked boolean not null default false;
alter table connections add column own_login boolean not null default false;
