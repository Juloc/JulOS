# Wegwerf-Ziel fuer VNC-Tests: ein TigerVNC-Server (Xvnc) mit einem kleinen Desktop (openbox, xterm), Zwischenablage-Abgleich und xdotool/xclip zum
# Pruefen von Eingaben. Nur fuer die Tests (deploy/dev/test-vnc.sh) und den Handtest; kein Zugang von aussen, das Passwort kommt aus der Umgebung.
FROM debian:trixie-slim
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends tigervnc-standalone-server tigervnc-tools tigervnc-xorg-extension openbox xterm xdotool xclip x11-utils x11-xserver-utils openssl procps busybox \
 && rm -rf /var/lib/apt/lists/*
COPY deploy/dev/vnc-target/entrypoint.sh /usr/local/bin/vnc-entrypoint
RUN useradd -m -s /bin/bash tester
USER tester
EXPOSE 5900
ENTRYPOINT ["/usr/local/bin/vnc-entrypoint"]
