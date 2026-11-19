using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Connections;

/// <summary>Die Liste der gespeicherten Verbindungen (Name, Art, Adresse, Port, Benutzername). Ein Passwort wird nur auf Wunsch verschluesselt abgelegt und nie wieder ausgegeben.</summary>
internal static class ConnectionEndpoints
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static ConnectionDto ToDto(ConnectionRecord c) => new(c.Id, c.Name, c.Kind, c.Host, c.Port, c.Username, c.HasPassword, c.Engine);

    private static string? CheckPassword(string? password, SecretBox box)
    {
        if (string.IsNullOrEmpty(password)) return null;
        if (!box.Enabled) return "Passwörter speichern ist nicht eingerichtet (JULOS_SECRET_KEY fehlt).";
        return password.Length > 256 ? "Das Passwort ist zu lang." : null;
    }

    public static void MapConnections(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/connections", async (HttpContext ctx, ConnectionStore store, SecretBox box) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return new ConnectionsResponse([.. (await store.ListAsync(id.UserId, ctx.RequestAborted)).Select(ToDto)], box.Enabled);
        });

        app.MapPost("/api/connections", async (ConnectionRequest body, HttpContext ctx, ConnectionStore store, HostPolicy policy, SecretBox box) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (v, error) = ConnectionRules.Validate(body.Name, body.Kind, body.Host, body.Port, body.Username, body.Engine);
            if (v is null) return Error(400, error!);
            if (!policy.Allows(v.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
            var passwordError = CheckPassword(body.Password, box);
            if (passwordError is not null) return Error(400, passwordError);
            if (await store.CountAsync(id.UserId, ctx.RequestAborted) >= ConnectionRules.MaxPerUser) return Error(400, "Zu viele Verbindungen.");
            var c = await store.CreateAsync(id.UserId, v.Name, v.Kind, v.Host, v.Port, v.Username, v.Engine, ctx.RequestAborted);
            if (!string.IsNullOrEmpty(body.Password))
            {
                await store.SetSecretAsync(id.UserId, c.Id, box.Encrypt(body.Password, c.Id), ctx.RequestAborted);
                c = c with { HasPassword = true };
            }
            return Results.Json(ToDto(c), AppJson.Default.ConnectionDto);
        });

        app.MapPut("/api/connections/{connection:guid}", async (Guid connection, ConnectionRequest body, HttpContext ctx, ConnectionStore store, HostPolicy policy, SecretBox box) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var before = await store.GetAsync(id.UserId, connection, ctx.RequestAborted);
            if (before is null) return Error(404, "Verbindung nicht gefunden.");
            var (v, error) = ConnectionRules.Validate(body.Name, body.Kind, body.Host, body.Port, body.Username, body.Engine, before.Engine);
            if (v is null) return Error(400, error!);
            if (!policy.Allows(v.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
            var passwordError = CheckPassword(body.Password, box);
            if (passwordError is not null) return Error(400, passwordError);
            var c = await store.UpdateAsync(id.UserId, connection, v.Name, v.Kind, v.Host, v.Port, v.Username, v.Engine, ctx.RequestAborted);
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
            return Results.Json(ToDto(c), AppJson.Default.ConnectionDto);
        });

        app.MapDelete("/api/connections/{connection:guid}", async (Guid connection, HttpContext ctx, ConnectionStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return await store.DeleteAsync(id.UserId, connection, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Verbindung nicht gefunden.");
        });
    }
}
