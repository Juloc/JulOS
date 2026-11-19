-- Bekannter Fingerabdruck des TLS-Zertifikats eines RDP-Ziels (Vertrauen bei erster Nutzung): "SHA256:<Hex mit Doppelpunkten>".
-- Nur der Fingerabdruck des oeffentlichen Zertifikats, nichts Geheimes. Aendert sich Adresse, Port oder Art der Verbindung, wird er geloescht.
alter table connections add column cert_fingerprint text;
