using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Display.Rdp.Drive;
using JulOS.Integrations.Ssh;

namespace JulOS.Frontend.Connections;

/// <summary>Die Liste der gespeicherten Verbindungen (Name, Art, Adresse, Port, Benutzername). Ein Passwort wird nur auf Wunsch verschluesselt abgelegt und nie wieder ausgegeben.</summary>
internal static class ConnectionEndpoints
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static ConnectionDto ToDto(ConnectionRecord c) => new(c.Id, c.Name, c.Kind, c.Host, c.Port, c.Username, c.HasPassword, c.HostId, c.DriveShare, c.DriveWrite, c.Domain, c.Notes, c.KeyboardLayout, c.Program, c.WorkDir, c.KeepAlive, c.CustomCommand);

    private static string? CheckPassword(string? password, SecretBox box)
    {
        if (string.IsNullOrEmpty(password)) return null;
        if (!box.Enabled) return "Passwörter speichern ist nicht eingerichtet (JULOS_SECRET_KEY fehlt).";
        return password.Length > 256 ? "Das Passwort ist zu lang." : null;
    }

    /// <summary>Programm, Arbeitsverzeichnis und tmux aus der Anfrage; nur Terminals haben sie (alles andere bekommt die Standardwerte). current = bisherige Werte beim Aendern.</summary>
    private static (SshLaunch? Value, string? Error) ProgramOf(string kind, ConnectionRequest body, SshLaunch? current) =>
        kind == "terminal" ? SshLaunchRules.Validate(body.Program, body.WorkDir, body.KeepAlive, body.CustomCommand, current) : (SshLaunch.Shell, null);

    private static ConnectionProgram Stored(SshLaunch l) => new(l.Program, l.WorkDir, l.KeepAlive, l.Custom);

    public static void MapConnections(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/connections", async (HttpContext ctx, ConnectionStore store, SecretBox box, DriveRoot drives) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return new ConnectionsResponse([.. (await store.ListAsync(id.UserId, ctx.RequestAborted)).Select(ToDto)], box.Enabled, drives.Available);
        });

        app.MapPost("/api/connections", async (ConnectionRequest body, HttpContext ctx, ConnectionStore store, HostStore hosts, HostPolicy policy, SecretBox box, DriveRoot drives) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (v, error) = ConnectionRules.Validate(body.Name, body.Kind, body.Host, body.Port, body.Username, driveShare: body.DriveShare, driveWrite: body.DriveWrite, domain: body.Domain, notes: body.Notes, layout: body.Layout);
            if (v is null) return Error(400, error!);
            var (launch, launchError) = ProgramOf(v.Kind, body, null);
            if (launch is null) return Error(400, launchError!);
            if (v.DriveShare && !drives.Available) return Error(400, "Die Laufwerksfreigabe ist am Server nicht eingerichtet (JULOS_DRIVE_ROOT).");
            if (!policy.Allows(v.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
            var passwordError = CheckPassword(body.Password, box);
            if (passwordError is not null) return Error(400, passwordError);
            if (await store.CountAsync(id.UserId, ctx.RequestAborted) >= ConnectionRules.MaxPerUser) return Error(400, "Zu viele Verbindungen.");
            // Der Host muss dem Benutzer gehoeren; ohne Angabe kommt die Verbindung auf seinen ersten Host
            if (body.HostId is { } wanted && await hosts.GetAsync(id.UserId, wanted, ctx.RequestAborted) is null) return Error(404, "Host nicht gefunden.");
            var c = await store.CreateAsync(id.UserId, v.Name, v.Kind, v.Host, v.Port, v.Username, body.HostId, ctx.RequestAborted, v.DriveShare, v.DriveWrite, v.Domain, v.Notes, v.Layout, Stored(launch));
            if (!string.IsNullOrEmpty(body.Password))
            {
                await store.SetSecretAsync(id.UserId, c.Id, box.Encrypt(body.Password, c.Id), ctx.RequestAborted);
                c = c with { HasPassword = true };
            }
            return Results.Json(ToDto(c), AppJson.Default.ConnectionDto);
        });

        app.MapPut("/api/connections/{connection:guid}", async (Guid connection, ConnectionRequest body, HttpContext ctx, ConnectionStore store, HostStore hosts, HostPolicy policy, SecretBox box, DriveRoot drives) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var before = await store.GetAsync(id.UserId, connection, ctx.RequestAborted);
            if (before is null) return Error(404, "Verbindung nicht gefunden.");
            var (v, error) = ConnectionRules.Validate(body.Name, body.Kind, body.Host, body.Port, body.Username, body.DriveShare, body.DriveWrite, before.DriveShare, before.DriveWrite,
                body.Domain, body.Notes, body.Layout, before.Domain, before.Notes, before.KeyboardLayout);
            if (v is null) return Error(400, error!);
            var (launch, launchError) = ProgramOf(v.Kind, body, new SshLaunch(before.Program, before.WorkDir, before.KeepAlive, before.CustomCommand));
            if (launch is null) return Error(400, launchError!);
            if (v.DriveShare && !before.DriveShare && !drives.Available) return Error(400, "Die Laufwerksfreigabe ist am Server nicht eingerichtet (JULOS_DRIVE_ROOT).");
            if (!policy.Allows(v.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
            var passwordError = CheckPassword(body.Password, box);
            if (passwordError is not null) return Error(400, passwordError);
            if (body.HostId is { } moveTo && await hosts.GetAsync(id.UserId, moveTo, ctx.RequestAborted) is null) return Error(404, "Host nicht gefunden.");
            var c = await store.UpdateAsync(id.UserId, connection, v.Name, v.Kind, v.Host, v.Port, v.Username, body.HostId, ctx.RequestAborted, v.DriveShare, v.DriveWrite, v.Domain, v.Notes, v.Layout, Stored(launch));
            if (c is null) return Error(404, "Verbindung nicht gefunden.");

            // Ein gespeichertes Passwort gehoert zu genau diesem Ziel: wer Adresse, Port, Art oder Benutzer aendert, schickt es sonst an ein anderes Ziel
            var targetChanged = before.Kind != v.Kind || before.Host != v.Host || before.Port != v.Port || before.Username != v.Username;
            if (!string.IsNullOrEmpty(body.Password))
            {
                await store.SetSecretAsync(id.UserId, connection, box.Encrypt(body.Password, connection), ctx.RequestAborted);
                c = c with { HasPassword = true };
            }
            else if (before.HasPassword && (body.ForgetPassword == true || targetChanged))
            {
                await store.SetSecretAsync(id.UserId, connection, null, ctx.RequestAborted);
                c = c with { HasPassword = false };
            }
            // Ein anderes Ziel hat einen anderen Host-Schluessel: der gemerkte gehoert zum alten Ziel
            if (before.HostKey is not null && (before.Kind != v.Kind || before.Host != v.Host || before.Port != v.Port))
            {
                await store.SetHostKeyAsync(id.UserId, connection, null, ctx.RequestAborted);
            }
            // Dasselbe fuer das Zertifikat des RDP-Ziels
            if (before.CertFingerprint is not null && (before.Kind != v.Kind || before.Host != v.Host || before.Port != v.Port))
            {
                await store.SetCertFingerprintAsync(id.UserId, connection, null, ctx.RequestAborted);
            }
            return Results.Json(ToDto(c), AppJson.Default.ConnectionDto);
        });

        // Kachel "Claude auf <Host>" oder "Codex auf <Host>" aus einer vorhandenen Terminal-Verbindung: gleiches Ziel (Host, Port, Benutzer, gespeichertes Passwort,
        // bekannter Host-Schluessel), anderes Programm. Eine Verbindung ist ein Ziel plus ein Programm; dasselbe Ziel darf mehrere Kacheln haben (docs/CLI-SESSIONS.md).
        app.MapPost("/api/connections/{connection:guid}/program", async (Guid connection, ProgramTileRequest body, HttpContext ctx, ConnectionStore store, HostStore hosts, HostPolicy policy, SecretBox box) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var source = await store.GetAsync(id.UserId, connection, ctx.RequestAborted);
            if (source is null) return Error(404, "Verbindung nicht gefunden.");
            if (source.Kind != "terminal") return Error(400, "Nur eine Terminal-Verbindung lässt sich als Claude- oder Codex-Kachel anlegen.");
            var program = (body.Program ?? "").Trim().ToLowerInvariant();
            if (!SshLaunchRules.IsTemplate(program)) return Error(400, "Das Programm muss Claude Code oder Codex sein.");
            var (launch, launchError) = SshLaunchRules.Validate(program, body.WorkDir ?? source.WorkDir, body.KeepAlive ?? source.KeepAlive, "");
            if (launch is null) return Error(400, launchError!);
            if (!policy.Allows(source.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
            if (await store.CountAsync(id.UserId, ctx.RequestAborted) >= ConnectionRules.MaxPerUser) return Error(400, "Zu viele Verbindungen.");

            var host = await hosts.GetAsync(id.UserId, source.HostId, ctx.RequestAborted);
            var title = body.Name?.Trim();
            if (string.IsNullOrEmpty(title)) title = (program == "claude" ? "Claude auf " : "Codex auf ") + (host?.Name ?? source.Name);
            if (title.Length > 60) title = title[..60].TrimEnd();
            if (title.Any(char.IsControl)) return Error(400, "Der Name muss 1 bis 60 Zeichen haben.");
            var c = await store.CreateAsync(id.UserId, title, "terminal", source.Host, source.Port, source.Username, source.HostId, ctx.RequestAborted, program: Stored(launch));
            if (source.HasPassword)
            {
                // Das Passwort gehoert zu einer Verbindung (die Kennung steckt in der Verschluesselung): auf dem Server umschluesseln, es geht nie zum Browser
                var sealedValue = await store.GetSecretAsync(id.UserId, connection, ctx.RequestAborted);
                var plain = sealedValue is null ? null : box.Decrypt(sealedValue, connection);
                if (plain is not null)
                {
                    await store.SetSecretAsync(id.UserId, c.Id, box.Encrypt(plain, c.Id), ctx.RequestAborted);
                    c = c with { HasPassword = true };
                }
            }
            if (source.HostKey is not null)
            {
                await store.SetHostKeyAsync(id.UserId, c.Id, source.HostKey, ctx.RequestAborted);        // dasselbe Ziel: der Schluessel ist schon bekannt
            }
            return Results.Json(ToDto(c), AppJson.Default.ConnectionDto);
        });

        app.MapDelete("/api/connections/{connection:guid}", async (Guid connection, HttpContext ctx, ConnectionStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!await store.DeleteAsync(id.UserId, connection, ctx.RequestAborted)) return Error(404, "Verbindung nicht gefunden.");
            return Results.NoContent();
        });
    }
}
