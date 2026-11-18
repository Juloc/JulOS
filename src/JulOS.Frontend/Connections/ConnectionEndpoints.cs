using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;

namespace JulOS.Frontend.Connections;

/// <summary>Die Liste der gespeicherten Verbindungen (Name, Art, Adresse, Port, Benutzername; nie ein Passwort).</summary>
internal static class ConnectionEndpoints
{
    private static IResult Error(int status, string message) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message), AppJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    private static ConnectionDto ToDto(ConnectionRecord c) => new(c.Id, c.Name, c.Kind, c.Host, c.Port, c.Username);

    public static void MapConnections(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/connections", async (HttpContext ctx, ConnectionStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return new ConnectionsResponse([.. (await store.ListAsync(id.UserId, ctx.RequestAborted)).Select(ToDto)]);
        });

        app.MapPost("/api/connections", async (ConnectionRequest body, HttpContext ctx, ConnectionStore store, HostPolicy policy) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (v, error) = ConnectionRules.Validate(body.Name, body.Kind, body.Host, body.Port, body.Username);
            if (v is null) return Error(400, error!);
            if (!policy.Allows(v.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
            if (await store.CountAsync(id.UserId, ctx.RequestAborted) >= ConnectionRules.MaxPerUser) return Error(400, "Zu viele Verbindungen.");
            var c = await store.CreateAsync(id.UserId, v.Name, v.Kind, v.Host, v.Port, v.Username, ctx.RequestAborted);
            return Results.Json(ToDto(c), AppJson.Default.ConnectionDto);
        });

        app.MapPut("/api/connections/{connection:guid}", async (Guid connection, ConnectionRequest body, HttpContext ctx, ConnectionStore store, HostPolicy policy) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            var (v, error) = ConnectionRules.Validate(body.Name, body.Kind, body.Host, body.Port, body.Username);
            if (v is null) return Error(400, error!);
            if (!policy.Allows(v.Host)) return Error(403, "Dieses Ziel ist nicht erlaubt.");
            var c = await store.UpdateAsync(id.UserId, connection, v.Name, v.Kind, v.Host, v.Port, v.Username, ctx.RequestAborted);
            return c is null ? Error(404, "Verbindung nicht gefunden.") : Results.Json(ToDto(c), AppJson.Default.ConnectionDto);
        });

        app.MapDelete("/api/connections/{connection:guid}", async (Guid connection, HttpContext ctx, ConnectionStore store) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            return await store.DeleteAsync(id.UserId, connection, ctx.RequestAborted) ? Results.NoContent() : Error(404, "Verbindung nicht gefunden.");
        });
    }
}
