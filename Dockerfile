# JulOS-Server (.NET). Der Node-Vorgaenger liegt noch in gateway/ und wird in Schritt 6 entfernt.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props JulOS.slnx ./
COPY src/JulOS.Backend/JulOS.Backend.csproj src/JulOS.Backend/
COPY src/JulOS.Workers/JulOS.Workers.csproj src/JulOS.Workers/
COPY src/JulOS.Frontend/JulOS.Frontend.csproj src/JulOS.Frontend/
RUN dotnet restore src/JulOS.Frontend/JulOS.Frontend.csproj
COPY src src
COPY database database
RUN dotnet publish src/JulOS.Frontend/JulOS.Frontend.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "JulOS.dll"]
