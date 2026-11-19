-- Bekannter Host-Schluessel des Ziels einer Terminal-Verbindung (Vertrauen bei erster Nutzung): "<Algorithmus> SHA256:<Fingerabdruck>".
-- Nur der Fingerabdruck des oeffentlichen Schluessels, nichts Geheimes. Aendert sich Adresse, Port oder Art der Verbindung, wird er geloescht.
alter table connections add column host_key text;
