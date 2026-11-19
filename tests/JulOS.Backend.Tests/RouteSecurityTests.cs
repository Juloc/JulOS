using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using JulOS.Frontend.Agent;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>
/// Sicherheitsnetz fuer jede Route (docs/SECURITY-REVIEW.md): alle registrierten Endpunkte werden aufgezaehlt und ohne Sitzung aufgerufen.
/// Alles ausser einer ausdruecklichen Erlaubnisliste muss 401 liefern; kommt eine neue oeffentliche Route dazu, schlaegt dieser Test fehl,
/// bis sie hier bewusst eingetragen ist. Dazu: schreibende Routen ohne Kopfzeile oder mit fremdem Origin (403), WebSocket mit fremdem Origin.
/// </summary>
public sealed class RouteSecurityTests
{
    /// <summary>Routen, die ohne Sitzung antworten duerfen (Methode + Muster) und warum. Alles andere: 401.</summary>
    private static readonly Dictionary<string, string> Public = new()
    {
        ["GET /api/health"] = "Release-Pruefung und Container-Healthcheck",
        ["GET /api/setup/status"] = "die Seite fragt, ob eingerichtet werden muss",
        ["POST /api/setup/begin"] = "Ersteinrichtung (Setup-Code, nur solange es kein Konto gibt)",
        ["POST /api/setup/finish"] = "Ersteinrichtung (Setup-Code, nur solange es kein Konto gibt)",
        ["GET /api/auth/me"] = "Seite fragt, ob eine Sitzung besteht",
        ["POST /api/auth/login"] = "Anmeldung",
        ["GET /agent-files/{name}"] = "Agent-Dateien und Pruefsummen (Quelltext, kein Geheimnis)",
        ["POST /api/agent-link/enroll"] = "Agent tauscht den Einmalcode gegen ein Token",
        ["ANY /api/agent-link"] = "WebSocket des Agenten (prueft sein Token selbst)",
    };

    private static string Fill(string pattern) =>
        Regex.Replace(pattern, @"\{\*?\*?(\w+)(:[^}]*)?\}", m => m.Groups[2].Value == ":guid" ? Guid.NewGuid().ToString() : "x");

    private static IEnumerable<(string Method, string Pattern, string Path)> Routes(IServiceProvider services)
    {
        var routes = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.True(routes.Count > 60, "zu wenige Routen gefunden: Aufzaehlung kaputt?");
        foreach (var e in routes)
        {
            var pattern = e.RoutePattern.RawText ?? "";
            var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            foreach (var m in methods is { Count: > 0 } ? methods : ["GET", "POST"])      // Map(...) ohne Methode: GET und POST pruefen
                yield return (m, pattern, Fill(pattern));
        }
    }

    private static bool IsPublic(string method, string pattern) =>
        Public.ContainsKey(method + " " + pattern) || Public.ContainsKey("ANY " + pattern);

    [Fact]
    public async Task Every_registered_route_needs_a_session_except_the_explicit_allow_list()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var seen = new HashSet<string>();
        var offenders = new List<string>();

        foreach (var (method, pattern, path) in Routes(app.Services))
        {
            seen.Add(method + " " + pattern);
            // Mit allem, was ein Browser mitschickt (Kopfzeile, eigener Origin): nur die fehlende Sitzung darf ablehnen
            var req = Req(new HttpMethod(method), path, cookie: null, body: method is "GET" or "HEAD" or "DELETE" ? null : new { });
            var res = await c.SendAsync(req);
            var status = (int)res.StatusCode;
            if (IsPublic(method, pattern)) continue;
            if (status != 401) offenders.Add($"{method} {pattern} -> {status}");
        }

        Assert.True(offenders.Count == 0, "Routen ohne Sitzungspflicht, die nicht in der Erlaubnisliste stehen:\n" + string.Join("\n", offenders));
        // die Erlaubnisliste darf nicht veralten (eine entfernte Route bliebe sonst als stille Ausnahme stehen)
        foreach (var key in Public.Keys.Where(k => !k.StartsWith("ANY ")))
            Assert.Contains(key, seen);
        foreach (var key in Public.Keys.Where(k => k.StartsWith("ANY ")))
            Assert.Contains(seen, s => s.EndsWith(key[3..]));
    }

    [Fact]
    public async Task Unknown_api_paths_and_methods_are_401_without_a_session_and_never_404()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        foreach (var path in new[] { "/api/", "/api/nope", "/api/auth/", "/api/auth/login/x", "/api/hosts/not-a-guid", "/api/files/..%2fauth%2fme", "/api//connections" })
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            {
                var res = await c.SendAsync(Req(method, path, null, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { }));
                // unter /api/auth und /api/setup verlangt die Wache fuer schreibende Aufrufe zuerst einen kleinen Koerper (413): auch das verraet nichts und liefert nichts aus
                var smallBodyFirst = res.StatusCode == HttpStatusCode.RequestEntityTooLarge && (path.StartsWith("/api/auth") || path.StartsWith("/api/setup")) && method != HttpMethod.Get;
                Assert.True(res.StatusCode is HttpStatusCode.Unauthorized || smallBodyFirst, $"{method} {path} -> {(int)res.StatusCode}");
            }
        // OPTIONS und TRACE: nie eine Antwort mit CORS-Kopfzeilen
        foreach (var method in new[] { HttpMethod.Options, HttpMethod.Trace, HttpMethod.Head })
        {
            var res = await c.SendAsync(new HttpRequestMessage(method, "/api/connections"));
            Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        }
    }

    [Fact]
    public async Task Every_state_changing_route_refuses_requests_without_the_header_or_with_a_foreign_origin_even_with_a_session()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var offenders = new List<string>();

        foreach (var (method, pattern, path) in Routes(app.Services))
        {
            if (method is "GET" or "HEAD") continue;
            if (pattern.StartsWith("/api/agent-link")) continue;                 // der Agent ist kein Browser: Token statt Cookie und Origin
            foreach (var (name, tweak) in new (string, Action<HttpRequestMessage>)[]
            {
                ("ohne Kopfzeile", r => r.Headers.Remove("X-JulOS")),
                ("ohne Origin", r => r.Headers.Remove("Origin")),
                ("fremder Origin", r => { r.Headers.Remove("Origin"); r.Headers.Add("Origin", "https://evil.example"); }),
                ("Origin mit Zusatz", r => { r.Headers.Remove("Origin"); r.Headers.Add("Origin", "http://localhost.evil.example"); }),
            })
            {
                var req = Req(new HttpMethod(method), path, cookie, new { });
                tweak(req);
                var res = await c.SendAsync(req);
                if (res.StatusCode != HttpStatusCode.Forbidden) offenders.Add($"{method} {pattern} ({name}) -> {(int)res.StatusCode}");
            }
        }
        Assert.True(offenders.Count == 0, "schreibende Routen ohne CSRF-Schutz:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public async Task WebSocket_upgrades_with_a_valid_session_and_ticket_are_refused_for_a_foreign_origin()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db, allowed: "127.0.0.1");
        var c = app.Http();
        var (cookie, _) = await SignedInAsync(c, db);

        // Terminal-Verbindung und Ticket: der Ticketweg verbindet noch nicht (das passiert erst im WebSocket)
        var create = await c.SendAsync(Req(HttpMethod.Post, "/api/connections", cookie, new { name = "t", kind = "terminal", host = "127.0.0.1", port = 1, username = "u" }));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = (await Json(create)).GetProperty("id").GetGuid();

        async Task<string> TicketAsync()
        {
            var r = await c.SendAsync(Req(HttpMethod.Post, "/api/remote/ticket", cookie, new { connection = id, password = "x", sid = "s1" }));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (await Json(r)).GetProperty("ticket").GetString()!;
        }

        async Task<WebSocket> Open(string ticket, string? origin, bool withCookie = true)
        {
            var ws = app.Server.CreateWebSocketClient();
            ws.ConfigureRequest = r =>
            {
                if (withCookie) r.Headers["Cookie"] = cookie;
                if (origin is not null) r.Headers["Origin"] = origin;
            };
            return await ws.ConnectAsync(new Uri("ws://localhost/api/remote/ssh?ticket=" + ticket), CancellationToken.None);
        }

        var ticket = await TicketAsync();
        foreach (var origin in new string?[] { "https://evil.example", "http://localhost.evil.example", null })
            await Assert.ThrowsAnyAsync<Exception>(() => Open(ticket, origin));
        await Assert.ThrowsAnyAsync<Exception>(() => Open(ticket, "http://localhost", withCookie: false));

        // dasselbe Ticket ist durch die Fehlversuche nicht verbraucht und geht mit dem richtigen Origin durch (Beweis, dass es am Origin lag)
        using var good = await Open(ticket, "http://localhost");
        Assert.Equal(WebSocketState.Open, good.State);
    }

    [Fact]
    public async Task Security_headers_are_on_every_kind_of_answer()
    {
        await using var db = await TestDb.CreateAsync();
        await using var app = new Server(db);
        var c = app.Http();
        foreach (var path in new[] { "/", "/api/health", "/api/connections", "/api/nope", "/agent-files/SHA256SUMS", "/app.js", "/does-not-exist.txt" })
        {
            var res = await c.GetAsync(path);
            var h = res.Headers;
            Assert.Equal("nosniff", h.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", h.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", h.GetValues("X-Frame-Options").Single());
            var csp = h.GetValues("Content-Security-Policy").Single();
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.Contains("script-src 'self'", csp);
            Assert.DoesNotContain("script-src 'self' 'unsafe", csp);
            Assert.Equal("same-origin", h.GetValues("Cross-Origin-Opener-Policy").Single());
            Assert.Equal("same-origin", h.GetValues("Cross-Origin-Resource-Policy").Single());
            Assert.Contains("camera=()", h.GetValues("Permissions-Policy").Single());
            Assert.False(h.Contains("Server"));
            Assert.False(h.Contains("Access-Control-Allow-Origin"));
            Assert.False(h.Contains("Strict-Transport-Security"), "ohne HTTPS (oder Proxy) kein HSTS");
        }
        var proxied = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        proxied.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Contains("max-age=", (await c.SendAsync(proxied)).Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public void A_window_list_from_an_agent_keeps_only_numeric_window_ids()
    {
        var hostile = """{"api":1,"mode":"desktop","windows":[{"id":12345,"title":"ok"},{"id":"../../auth/logout-others?","title":"x"},{"id":"7","title":"string id"},{"id":-1},{"id":1e30},{"title":"no id"},{"id":99999999999999},"text",{"id":42,"title":"also ok"}]}""";
        var clean = WindowList.Sanitize(hostile);
        var ids = ApiKit.J(clean).GetProperty("windows").EnumerateArray().Select(w => w.GetProperty("id").GetInt64()).ToArray();
        Assert.Equal([12345L, 42L], ids);
        Assert.DoesNotContain("logout", clean);
        Assert.Equal("kein json", WindowList.Sanitize("kein json"));
        Assert.Equal("""{"windows":5}""", WindowList.Sanitize("""{"windows":5}"""));
    }
}
