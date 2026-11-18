#!/usr/bin/env bash
# Legt den Test-Benutzer an (ubuntu / ubuntu, nur in diesem Wegwerf-Container), macht den gemeinsamen Ordner fuer Agent-Socket und
# Foto-Upload beschreibbar und startet xrdp wie auf der VM (sesman und xrdp).
set -e
if ! id ubuntu >/dev/null 2>&1; then
  groupadd --gid 1020 ubuntu
  useradd --shell /bin/bash --uid 1020 --gid 1020 --groups sudo --create-home --home-dir /home/ubuntu ubuntu
  echo 'ubuntu:ubuntu' | chpasswd
  echo 'xfce4-session' > /home/ubuntu/.xsession
  chown ubuntu:ubuntu /home/ubuntu/.xsession
fi
mkdir -p /home/ubuntu/julos-uploads
chown ubuntu:ubuntu /home/ubuntu /home/ubuntu/julos-uploads
rm -f /var/run/xrdp/*.pid /var/run/xrdp-sesman.pid
/usr/sbin/xrdp-sesman
exec /usr/sbin/xrdp --nodaemon
