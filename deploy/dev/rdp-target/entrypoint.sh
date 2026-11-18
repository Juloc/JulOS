#!/usr/bin/env bash
# Legt den Benutzer an (wie der Original-Entrypoint) und macht den gemeinsamen Ordner fuer Agent-Socket und Foto-Upload beschreibbar,
# bevor der Original-Entrypoint startet.
set -e
if ! id ubuntu >/dev/null 2>&1; then
  groupadd --gid 1020 ubuntu
  useradd --shell /bin/bash --uid 1020 --gid 1020 --groups sudo --password "$(openssl passwd ubuntu)" --create-home --home-dir /home/ubuntu ubuntu
fi
mkdir -p /home/ubuntu/julos-uploads
chown ubuntu:ubuntu /home/ubuntu /home/ubuntu/julos-uploads
exec /usr/bin/entrypoint "$@"
