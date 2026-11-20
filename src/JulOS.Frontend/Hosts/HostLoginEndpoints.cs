using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Frontend.Files;

namespace JulOS.Frontend.Hosts;

/// <summary>
/// Der Login eines Hosts (docs/HOSTS.md, "Login"): ein Benutzer und ein Passwort fuer Terminal, Dateien und Desktop-Verbindungen dieses Hosts. Alles braucht die Sitzung (ApiGuard, Kopf und Origin bei
/// Aenderungen) und gilt nur fuer Hosts des angemeldeten Benutzers (ein fremder Host ist 404). Das Passwort geht nie wieder hinaus; die Antworten sagen nur, ob eins da ist.
/// </summary>
internal static class HostLoginEndpoints
{
    public const int MaxUser = 64, MaxPassword = 256;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    /// <summary>Der Benutzername: 1 bis 64 Zeichen, keine Steuerzeichen (Leerraum am Rand entfaellt).</summary>
    internal static (string? User, string? Error) ValidateUser(string? user)
    {
        user = user?.Trim() ?? "";
        if (user.Length is < 1 or > MaxUser || user.Any(char.IsControl)) return (null, "Der Benutzername muss 1 bis 64 Zeichen haben.");
        return (user, null);
    }

    /// <summary>Ein Benutzername aus den Verbindungen des Hosts als Vorschlag: der haeufigste, bei Gleichstand der einer Terminal-Verbindung zuerst; null, wenn keine Verbindung einen hat.</summary>
    internal static string? Suggest(IEnumerable<ConnectionRecord> connections) =>
        connections.Where(c => c.Username != "").GroupBy(c => c.Username, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Any(c => c.Kind == "terminal")).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();

    private static async Task<HostLoginDto> DescribeAsync(Guid userId, Guid hostId, HostLoginInfo login, ConnectionStore connections, SecretBox box, int pinned, CancellationToken ct)
    {
        var mine = (await connections.ListAsync(userId, ct)).Where(c => c.HostId == hostId).ToList();
        var choices = await connections.ChoicesAsync(userId, mine, ct);
        var rows = mine.Select(c => new HostLoginConnectionDto(c.Id, c.Name, c.Kind, $"{c.Host}:{c.Port}", choices[c.Id].Username, choices[c.Id].Source == JulOS.Backend.Logic.LoginSource.Host ? "host" : "own", c.OwnLogin, choices[c.Id].AddressNotTrusted)).ToArray();
        return new HostLoginDto(login.User, login.HasPassword, box.Enabled, login.User is null ? Suggest(mine) : null, rows, pinned);
    }

    /// <summary>Beendet die gepoolten SFTP-Sitzungen aller Verbindungen des Hosts: die Dateien-App nimmt danach nur noch die neuen Zugangsdaten. Laufende Terminals bleiben (sie haben sich schon angemeldet).</summary>
    private static async Task CloseSftpAsync(Guid userId, Guid hostId, ConnectionStore connections, SftpPool pool, CancellationToken ct)
    {
        foreach (var c in (await connections.ListAsync(userId, ct)).Where(c => c.HostId == hostId)) await pool.InvalidateAsync(userId, c.Id);
    }

    public static void MapHostLogin(this IEndpointRouteBuilder app)
    {
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("HostLogin");

        app.MapGet("/api/hosts/{host:guid}/login", async (Guid host, HttpContext ctx, HostLoginStore logins, ConnectionStore connections, SecretBox box, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (!hits.Allow("hostlogin-read:" + id.UserId, 60, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var login = await logins.GetAsync(id.UserId, host, ctx.RequestAborted);
            if (login is null) return Error(404, "Host nicht gefunden.");
            return Results.Json(await DescribeAsync(id.UserId, host, login, connections, box, 0, ctx.RequestAborted), AppJson.Default.HostLoginDto);
        });

        // Setzen oder aendern. Ohne Passwort in der Anfrage bleibt ein gespeichertes (ausser ForgetPassword oder ein anderer Benutzer). Der Eintrag im Verlauf steht VOR der Aenderung: kann er nicht geschrieben werden, aendert sich nichts.
        app.MapPut("/api/hosts/{host:guid}/login", async (Guid host, HostLoginRequest body, HttpContext ctx, HostStore hosts, HostLoginStore logins, ConnectionStore connections, HostAuditStore audit, SftpPool pool, SecretBox box, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var ct = ctx.RequestAborted;
            if (!hits.Allow("hostlogin:" + id.UserId, 20, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var (user, error) = ValidateUser(body.User);
            if (user is null) return Error(400, error!);
            var password = string.IsNullOrEmpty(body.Password) ? null : body.Password;
            if (password is { Length: > MaxPassword }) return Error(400, "Das Passwort ist zu lang.");
            if (password is not null && !box.Enabled) return Error(400, "Passwörter speichern ist nicht eingerichtet (JULOS_SECRET_KEY fehlt).");
            var h = await hosts.GetAsync(id.UserId, host, ct);
            var before = await logins.GetAsync(id.UserId, host, ct);
            if (h is null || before is null) return Error(404, "Host nicht gefunden.");

            var what = password is not null ? (before.User is null || before.User != user ? "user+password" : "password") : body.ForgetPassword == true ? "password removed" : before.User is null || before.User != user ? "user" : "unchanged";
            try { await audit.AddAsync(id.UserId, host, h.Name, "login.set", null, "ok", what, ct: CancellationToken.None); }
            catch (Exception e) { log.LogError(e, "Verlauf des Host-Logins konnte nicht geschrieben werden"); return Error(503, "Der Verlauf lässt sich nicht schreiben: nichts wurde geändert."); }

            var result = await logins.SetAsync(id.UserId, host, user, password is null ? null : box.Encrypt(password, host), body.ForgetPassword == true && password is null, ct: ct);
            if (!result.Found) return Error(404, "Host nicht gefunden.");
            await CloseSftpAsync(id.UserId, host, connections, pool, ct);
            log.LogInformation("Host-Login geändert: Host {Host}, Benutzer {User}, was: {What}", host, id.UserName, what);          // nie ein Wert: nicht der Benutzername des Logins, nie das Passwort
            var now = (await logins.GetAsync(id.UserId, host, ct))!;
            return Results.Json(await DescribeAsync(id.UserId, host, now, connections, box, result.Pinned, ct), AppJson.Default.HostLoginDto);
        });

        app.MapDelete("/api/hosts/{host:guid}/login", async (Guid host, HttpContext ctx, HostStore hosts, HostLoginStore logins, ConnectionStore connections, HostAuditStore audit, SftpPool pool, SecretBox box, HitCounter hits) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var ct = ctx.RequestAborted;
            if (!hits.Allow("hostlogin:" + id.UserId, 20, Minute)) return Error(429, "Zu viele Anfragen, bitte kurz warten.");
            var h = await hosts.GetAsync(id.UserId, host, ct);
            if (h is null) return Error(404, "Host nicht gefunden.");
            try { await audit.AddAsync(id.UserId, host, h.Name, "login.remove", null, "ok", ct: CancellationToken.None); }
            catch (Exception e) { log.LogError(e, "Verlauf des Host-Logins konnte nicht geschrieben werden"); return Error(503, "Der Verlauf lässt sich nicht schreiben: nichts wurde geändert."); }
            if (!await logins.ClearAsync(id.UserId, host, ct)) return Error(404, "Host nicht gefunden.");
            await CloseSftpAsync(id.UserId, host, connections, pool, ct);
            log.LogInformation("Host-Login entfernt: Host {Host}, Benutzer {User}", host, id.UserName);
            return Results.Json(await DescribeAsync(id.UserId, host, (await logins.GetAsync(id.UserId, host, ct))!, connections, box, 0, ct), AppJson.Default.HostLoginDto);
        });
    }
}
