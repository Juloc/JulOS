# Codex im eigenen Container (docs/CODEX-SIDECAR.md): das Codex-Programm und das Tor (tools/codex-gate), sonst nichts. Ein eigener Benutzer, ein eigenes Volume
# fuer die Anmeldung, kein Zugriff auf die Daten von JulOS. Gebaut mit dem Kontext des Repositories: docker build -f deploy/codex/Dockerfile .
# Version und Pruefsummen sind die in src/JulOS.Integrations/Ai/CodexInstaller.cs (CodexRelease); ein Test (CodexSidecarTests) vergleicht beide Stellen.
FROM debian:13-slim

ARG CODEX_VERSION=0.171.0
ARG CODEX_SHA256_AMD64=01baa2e47809f8597be5b2f8abd4e4dfe3fb5bcb6e504c2fbaaad97056c15e8d
ARG CODEX_SHA256_ARM64=bcf50b836385e51bd2976ab2bbe92b1b08a1cbe37712eb8dbe378aa78d24871c
ARG TARGETARCH

# curl nur zum Laden: es wird danach wieder entfernt. Das Archiv wird gegen die feste Pruefsumme geprueft, bevor irgendetwas entpackt wird (sonst bricht der Bau ab);
# die Pruefsumme der entpackten Datei kommt in /usr/local/share/codex.sha256, damit der Selbsttest sie wiederfindet.
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends python3-minimal ca-certificates curl tini \
 && case "$TARGETARCH" in \
      amd64) triple=x86_64-unknown-linux-musl; sha="$CODEX_SHA256_AMD64";; \
      arm64) triple=aarch64-unknown-linux-musl; sha="$CODEX_SHA256_ARM64";; \
      *) echo "Bauart $TARGETARCH wird nicht unterstuetzt" >&2; exit 1;; \
    esac \
 && curl -fsSL -o /tmp/codex.tar.gz "https://github.com/openai/codex/releases/download/rust-v${CODEX_VERSION}/codex-${triple}.tar.gz" \
 && echo "${sha}  /tmp/codex.tar.gz" | sha256sum -c - \
 && mkdir /tmp/x && tar -xzf /tmp/codex.tar.gz -C /tmp/x "codex-${triple}" \
 && install -m 0755 "/tmp/x/codex-${triple}" /usr/local/bin/codex \
 && sha256sum /usr/local/bin/codex | cut -d' ' -f1 > /usr/local/share/codex.sha256 \
 && rm -rf /tmp/codex.tar.gz /tmp/x \
 && apt-get purge -y --auto-remove curl && rm -rf /var/lib/apt/lists/*

# Benutzer codex (10001) und die Gruppe des Sockets (10002): JulOS tritt ihr bei (group_add), sonst niemand. Home gehoert nur codex (0700).
RUN groupadd -g 10002 codexsock && useradd -u 10001 -g 10002 -M -d /home/codex -s /usr/sbin/nologin codex \
 && install -d -o codex -g codexsock -m 0700 /home/codex \
 && install -d -o codex -g codexsock -m 0750 /run/codex

COPY tools/codex-gate/codex_gate.py /usr/local/bin/codex-gate
RUN chmod 0755 /usr/local/bin/codex-gate

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
USER codex
WORKDIR /home/codex
ENTRYPOINT ["tini", "--", "python3", "/usr/local/bin/codex-gate"]
