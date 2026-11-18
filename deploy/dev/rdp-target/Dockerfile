# Test-Desktop: xrdp + XFCE auf Debian 13, genau wie die Linux-VM (xrdp 0.10 mit Grafik-Pipeline "GFX"), plus der echte JulOS-Agent
# und der Zwischenablage-Helfer aus tools/. Nur fuer die Test-Instanz. Kontext ist die Repository-Wurzel (siehe deploy/dev/compose.yaml).
FROM debian:trixie-slim
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends xrdp xorgxrdp xfce4 xfce4-terminal dbus-x11 xauth openssl sudo procps \
    python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0 gir1.2-gdkpixbuf-2.0 \
 && rm -rf /var/lib/apt/lists/*
COPY tools/julos-agent.py tools/julos-clip.py /opt/julos/
# beim Anmelden der Sitzung starten, wie es tools/install-linux-agent.sh fuer einen echten Rechner einrichtet
RUN for n in agent clip; do printf '[Desktop Entry]\nType=Application\nName=JulOS %s\nExec=/usr/bin/python3 /opt/julos/julos-%s.py\nNoDisplay=true\n' "$n" "$n" > /etc/xdg/autostart/julos-$n.desktop; done
COPY deploy/dev/rdp-target/entrypoint.sh /usr/local/bin/dev-entrypoint
EXPOSE 3389
ENTRYPOINT ["/usr/local/bin/dev-entrypoint"]
