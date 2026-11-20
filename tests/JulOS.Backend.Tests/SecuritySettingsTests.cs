using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Logic;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Einstellungen, Sicherheit (F-02, F-03): vertraute Proxys und erlaubte Zielhosts in der App, Rueckfall auf die Umgebung, Wirkung ohne Neustart, Schutz vor Selbstaussperren.</summary>
public sealed class SecuritySettingsTests
{
    [Fact]
    public void Address_lists_are_checked_and_matched_by_name_address_and_network()
    {
        var (ok, error) = AddressList.Validate([" 192.0.2.10 ", "192.0.2.0/24", "192.0.2.10", "", "::1"], names: false);
        Assert.Null(error);
        Assert.Equal(["192.0.2.10", "192.0.2.0/24", "::1"], ok!);
        foreach (var bad in new[] { "nope", "192.0.2.10/33", "999.1.1.1", "a b" })
            Assert.NotNull(AddressList.Validate([bad], names: false).Error);
        Assert.Equal(["192.0.2.0/24"], AddressList.Validate(["192.0.2.5/24"], names: false).Entries!);          // ein Netz wird auf seine Grundadresse gerundet
        Assert.Equal(["pve.example", "192.0.2.0/24"], AddressList.Validate(["PVE.example", "192.0.2.0/24"], names: true).Entries!);
        Assert.NotNull(AddressList.Validate(["999.1.1.1"], names: true).Error);                      // eine Zahlenfolge ist eine falsche Adresse, kein Name
        Assert.NotNull(AddressList.Validate(Enumerable.Range(0, AddressList.MaxEntries + 1).Select(i => "10.0.0." + i), names: false).Error);

        Assert.True(AddressList.ContainsAddress(["192.0.2.0/24"], IPAddress.Parse("192.0.2.77")));
        Assert.True(AddressList.ContainsAddress(["192.0.2.0/24"], IPAddress.Parse("::ffff:192.0.2.77")));
        Assert.False(AddressList.ContainsAddress(["192.0.2.0/24"], IPAddress.Parse("192.0.3.1")));
        Assert.False(AddressList.ContainsAddress(["pve.example"], IPAddress.Parse("192.0.2.1")));

        var policy = new HostPolicy("pve.example, 192.0.2.0/28");
        Assert.True(policy.Allows("PVE.example")); Assert.True(policy.Allows("192.0.2.9")); Assert.False(policy.Allows("192.0.2.99")); Assert.False(policy.Allows("169.254.169.254"));
        Assert.True(new HostPolicy((string?)null).Allows("anything.example"));                        // leer: keine Einschraenkung
    }

    [Fact]
    public async Task Allowed_hosts_come_from_the_environment_until_the_app_sets_them_and_apply_without_a_restart()
    {
        await using var db = await TestDb.CreateAsync();
        await using var server = new Server(db, allowed: "alt.example");
        var c = server.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var ct = TestContext.Current.CancellationToken;

        var state = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/security", cookie), ct));
        Assert.Equal("env", state.GetProperty("hosts").GetProperty("source").GetString());
        Assert.Equal(["alt.example"], state.GetProperty("hosts").GetProperty("entries").EnumerateArray().Select(e => e.GetString()).ToArray());

        // eine Verbindung zu einem erlaubten Ziel; danach sperrt eine neue Liste in der App sie: erst Vorschau, dann ohne Bestaetigung 409, mit Bestaetigung gespeichert
        var made = await c.SendAsync(Req(HttpMethod.Post, "/api/connections", cookie, new { name = "Alt", kind = "terminal", host = "alt.example", username = "u" }), ct);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var preview = await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/security/hosts/preview", cookie, new { entries = new[] { "neu.example" } }), ct));
        Assert.Equal("Alt", preview.GetProperty("blocked")[0].GetProperty("name").GetString());

        var refused = await c.SendAsync(Req(HttpMethod.Put, "/api/security/hosts", cookie, new { entries = new[] { "neu.example" } }), ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("blocked_connections", (await Json(refused)).GetProperty("code").GetString());
        Assert.Equal("env", (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/security", cookie), ct))).GetProperty("hosts").GetProperty("source").GetString());   // nichts gespeichert

        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Put, "/api/security/hosts", cookie, new { entries = new[] { "neu.example" }, confirm = true }), ct)).StatusCode);
        Assert.Equal("app", (await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/security", cookie), ct))).GetProperty("hosts").GetProperty("source").GetString());
        // sofort wirksam, ohne Neustart: das neue Ziel geht, das alte nicht mehr
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/connections", cookie, new { name = "Neu", kind = "terminal", host = "neu.example", username = "u" }), ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, "/api/connections", cookie, new { name = "Alt2", kind = "terminal", host = "alt.example", username = "u" }), ct)).StatusCode);

        // zuruecksetzen: wieder die Umgebung
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Delete, "/api/security/hosts", cookie), ct)).StatusCode);
        var back = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/security", cookie), ct));
        Assert.Equal("env", back.GetProperty("hosts").GetProperty("source").GetString());

        // der Verlauf nennt, was geaendert wurde (kein Geheimnis)
        var audit = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/security/audit", cookie), ct));
        Assert.Contains(audit.GetProperty("entries").EnumerateArray(), e => e.GetProperty("event").GetString() == "hosts.set" && e.GetProperty("detail").GetString()!.Contains("neu.example"));
        Assert.Contains(audit.GetProperty("entries").EnumerateArray(), e => e.GetProperty("event").GetString() == "hosts.reset");
    }

    [Fact]
    public async Task Trusted_proxies_apply_without_a_restart_and_warn_before_cutting_off_the_current_proxy()
    {
        await using var db = await TestDb.CreateAsync();
        await using var server = new Server(db);
        var c = server.Http();
        var (cookie, _) = await SignedInAsync(c, db);
        var ct = TestContext.Current.CancellationToken;

        HttpRequestMessage Forwarded(HttpMethod m, string url, object? body = null)
        {
            var r = Req(m, url, cookie, body);
            r.Headers.Add("X-Forwarded-For", "203.0.113.9");
            return r;
        }

        // ohne Einstellung gilt der weite Standard (Warnung), also wird der Proxy im privaten Netz (hier: Loopback) geglaubt
        var open = await Json(await c.SendAsync(Forwarded(HttpMethod.Get, "/api/security"), ct));
        Assert.True(open.GetProperty("proxiesUnset").GetBoolean());
        Assert.Equal("default", open.GetProperty("proxies").GetProperty("source").GetString());
        Assert.Equal("203.0.113.9", open.GetProperty("client").GetProperty("address").GetString());
        Assert.Equal("127.0.0.1", open.GetProperty("client").GetProperty("peer").GetString());
        Assert.True(open.GetProperty("client").GetProperty("forwarded").GetBoolean());

        // Validierung
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/security/proxies", cookie, new { entries = new[] { "kein-netz" } }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Put, "/api/security/proxies", cookie, new { entries = Array.Empty<string>() }), ct)).StatusCode);

        // eine Liste ohne den Proxy, ueber den dieser Zugriff kommt: ohne Bestaetigung 409, mit Bestaetigung gespeichert
        var refused = await c.SendAsync(Forwarded(HttpMethod.Put, "/api/security/proxies", new { entries = new[] { "192.0.2.10/32" } }), ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("proxy_not_included", (await Json(refused)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Forwarded(HttpMethod.Put, "/api/security/proxies", new { entries = new[] { "192.0.2.10/32" }, confirm = true }), ct)).StatusCode);

        // sofort wirksam: der Loopback-Proxy ist nicht mehr vertraut, seine Kopfzeile wird ignoriert
        var now = await Json(await c.SendAsync(Forwarded(HttpMethod.Get, "/api/security"), ct));
        Assert.False(now.GetProperty("proxiesUnset").GetBoolean());
        Assert.Equal("app", now.GetProperty("proxies").GetProperty("source").GetString());
        Assert.Equal("127.0.0.1", now.GetProperty("client").GetProperty("address").GetString());
        Assert.False(now.GetProperty("client").GetProperty("forwarded").GetBoolean());

        // zuruecksetzen: wieder der Standard
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Delete, "/api/security/proxies", cookie), ct)).StatusCode);
        Assert.Equal("203.0.113.9", (await Json(await c.SendAsync(Forwarded(HttpMethod.Get, "/api/security"), ct))).GetProperty("client").GetProperty("address").GetString());
    }

    [Fact]
    public async Task The_security_routes_need_a_session_and_the_own_origin()
    {
        await using var db = await TestDb.CreateAsync();
        await using var server = new Server(db);
        var c = server.Http();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Get, "/api/security", null), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Put, "/api/security/hosts", null, new { entries = Array.Empty<string>() }), ct)).StatusCode);
        var (cookie, _) = await SignedInAsync(c, db);
        var noOrigin = new HttpRequestMessage(HttpMethod.Put, "/api/security/hosts") { Content = JsonContent.Create(new { entries = Array.Empty<string>() }) };
        noOrigin.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(noOrigin, ct)).StatusCode);
    }
}
