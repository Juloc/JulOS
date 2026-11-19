# JulOS-Server (.NET), siehe docs/ARCHITECTURE.md
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props JulOS.slnx ./
COPY src/JulOS.Backend/JulOS.Backend.csproj src/JulOS.Backend/
COPY src/JulOS.Workers/JulOS.Workers.csproj src/JulOS.Workers/
COPY src/JulOS.Integrations/JulOS.Integrations.csproj src/JulOS.Integrations/
COPY src/JulOS.Frontend/JulOS.Frontend.csproj src/JulOS.Frontend/
RUN dotnet restore src/JulOS.Frontend/JulOS.Frontend.csproj
COPY src src
COPY database database
# Commit und Bauzeit kommen vom Aufrufer (Workflow); sie stehen nur in "Ueber JulOS". Die Version steht in Directory.Build.props.
ARG COMMIT=
ARG BUILD_DATE=
RUN dotnet publish src/JulOS.Frontend/JulOS.Frontend.csproj -c Release -o /app --no-restore -p:SourceRevisionId=${COMMIT}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# H.264 fuer den eigenen Bildschirm-Stream: libopenh264 2.4.1 (Ubuntu-Paket libopenh264-7, BSD, etwa 1 MB). Genau diese Version ist die, gegen die
# der Kodierer geprueft ist; das Alpine-Paket ist eine andere (2.6.0) und wird deshalb nicht benutzt.
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends libopenh264-7 && rm -rf /var/lib/apt/lists/*
# Hardware-Kodierung auf einer Grafikkarte (VAAPI, siehe docs/ENCODERS.md): libva (MIT) und der Treiber fuer Intel-GPUs (intel-media-va-driver, MIT/BSD,
# Ubuntu universe, etwa 13 MB). Ohne GPU im Container (kein /dev/dri) liegen sie nur da und der Server kodiert auf der CPU. Fuer eine andere GPU:
# --build-arg VAAPI_PACKAGES="libva2 libva-drm2 mesa-va-drivers" (AMD) oder "... i965-va-driver" (aeltere Intel); leer lassen schaltet es ab.
ARG VAAPI_PACKAGES="libva2 libva-drm2 intel-media-va-driver"
RUN if [ -n "$VAAPI_PACKAGES" ]; then apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends $VAAPI_PACKAGES && rm -rf /var/lib/apt/lists/*; fi
WORKDIR /app
COPY --from=build /app .
ARG BUILD_DATE=
ENV ASPNETCORE_HTTP_PORTS=8080 JULOS_DATA=/data JULOS_BUILD_DATE=${BUILD_DATE}
# Datenordner (Schluessel fuer gespeicherte Passwoerter): gehoert dem Benutzer des Servers, damit ein neues Volume ihn uebernimmt
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "JulOS.dll"]
