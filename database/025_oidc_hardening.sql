-- Anmelde-Zentrale, Nachbesserung nach dem Sicherheits-Review (docs/SIGN-IN.md):
--   oidc_apps.backchannel_private  die Adresse fuer das Abmelden darf im lokalen Netz liegen (10.x, 172.16-31.x, 192.168.x, fc00::/7). Standard: nein. Loopback und Link-Local (Metadaten) sind nie erlaubt.
-- Nichts wird geloescht oder umgeschrieben. Der Eigentuemer steht in instance_settings ('oidc.owner'), nicht hier.
alter table oidc_apps add column backchannel_private boolean not null default false;
