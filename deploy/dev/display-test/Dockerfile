# SDK mit libopenh264 (Debian), damit die Display-Tests den H.264-Kodierer wirklich benutzen koennen
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends libopenh264-7 && rm -rf /var/lib/apt/lists/*
