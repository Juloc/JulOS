using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Frontend.Oidc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Der Server der Anmelde-Zentrale fuer die Tests: echte Middleware und Datenbank, feste oeffentliche Adresse, ein Server-Schluessel. Der Hintergrunddienst fuer die Logout-Token laeuft nicht (die Tests rufen RunOnceAsync selbst auf).</summary>
internal sealed class OidcServer(TestDb db, bool withKey = true, string? publicUrl = "http://localhost", TimeProvider? time = null) : WebApplicationFactory<Program>
{
    public const string Issuer = "http://localhost";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("JULOS_DB", db.ConnectionString);
        builder.UseSetting("JULOS_SETUP_CODE", ApiKit.SetupCode);
        if (withKey) builder.UseSetting("JULOS_SECRET_KEY", Key);
        if (publicUrl is not null) builder.UseSetting("JULOS_PUBLIC_URL", publicUrl);
        builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
        builder.ConfigureTestServices(s =>
        {
            if (time is not null) s.AddSingleton(time);                                       // die letzte Eintragung gilt: eine steuerbare Uhr fuer Ablaufzeiten
            foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OidcLogoutWorker)).ToList()) s.Remove(d);
        });
    }

    /// <summary>Ein Client wie ein Browser ohne eigenes Cookie-Glas und ohne Weiterleitungen: jede Antwort wird einzeln angesehen.</summary>
    public HttpClient Http() => CreateDefaultClient(new Buffered());

    /// <summary>Ein Browser schickt die Laenge des Koerpers mit (die Wache verlangt sie); ohne diesen Handler kennt der Test-Client sie nicht.</summary>
    private sealed class Buffered : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) await request.Content.LoadIntoBufferAsync(ct);
            return await base.SendAsync(request, ct);
        }
    }
}

/// <summary>Ein Ablauf mit allem Noetigen: Server, Konto des Eigentuemers (angemeldet), Apps, und die Schritte des Protokolls als kleine Hilfen.</summary>
internal sealed class OidcRig : IAsyncDisposable
{
    public const string Redirect = "https://app.example/cb";
    public const string BackchannelUri = "https://app.example/logout";
    public const string PostLogout = "https://app.example/bye";

    public TestDb Db = null!;
    public OidcServer Server = null!;
    public HttpClient Http = null!;
    public string Cookie = "";
    public Guid OwnerId;
    public OidcLogoutService Logout => Server.Services.GetRequiredService<OidcLogoutService>();
    public OidcKeys Keys => Server.Services.GetRequiredService<OidcKeys>();
    public OidcHttp Outbound => Server.Services.GetRequiredService<OidcHttp>();

    public sealed record App(Guid Id, string ClientId, string Secret, string Name, string RedirectUri = Redirect);

    public static async Task<OidcRig> CreateAsync(bool withKey = true, string? publicUrl = OidcServer.Issuer, TimeProvider? time = null)
    {
        var r = new OidcRig { Db = await TestDb.CreateAsync() };
        r.Server = new OidcServer(r.Db, withKey, publicUrl, time);
        r.Http = r.Server.Http();
        (r.Cookie, r.OwnerId) = await SignedInAsync(r.Http, r.Db);
        return r;
    }

    public async ValueTask DisposeAsync() { Http.Dispose(); await Server.DisposeAsync(); await Db.DisposeAsync(); }

    // ---- Verwaltung ----

    public async Task<App> AddAppAsync(string name = "Notes", string? backchannel = BackchannelUri, string[]? redirects = null, string[]? roles = null, string[]? postLogout = null)
    {
        var res = await Http.SendAsync(Req(HttpMethod.Post, "/api/security/oidc/apps", Cookie,
            new { name, redirectUris = redirects ?? [Redirect], postLogoutUris = postLogout ?? [PostLogout], backchannelUri = backchannel, roles = roles ?? ["admin", "member", "viewer"] }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var j = await Json(res);
        return new App(j.GetProperty("app").GetProperty("id").GetGuid(), j.GetProperty("app").GetProperty("clientId").GetString()!, j.GetProperty("clientSecret").GetString()!, name, (redirects ?? [Redirect])[0]);
    }

    /// <summary>Ein zweites Konto mit einer eigenen Sitzung (ohne Anmeldung ueber den Server: Konto und Sitzung direkt in der Datenbank).</summary>
    public async Task<(string Cookie, Guid UserId)> SecondPersonAsync(string name = "anna")
    {
        var users = new UserStore(Db.Data);
        var u = await users.CreateAsync(name, "x", Totp.NewSecret(), confirmed: true, [], replacePending: false);
        var token = await new SessionStore(Db.Data).CreateAsync(u.Id, "127.0.0.1", "test");
        return ("julos_sid=" + token, u.Id);
    }

    public Task AssignAsync(App app, Guid userId, string role) => new OidcAppStore(Db.Data, TimeProvider.System).SetRoleAsync(app.Id, userId, role);

    // ---- Protokoll ----

    /// <summary>"Die Adresse der App" als Vorgabe beim Tausch (null heisst: gar keine mitschicken).</summary>
    public const string Same = "\u0001";

    public static string NewVerifier() => OidcRules.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    public Task<HttpResponseMessage> AuthorizeAsync(App app, string? cookie, string verifier, Action<Dictionary<string, string?>>? tweak = null)
    {
        var q = new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = app.ClientId, ["redirect_uri"] = app.RedirectUri, ["scope"] = "openid profile", ["state"] = "st-1", ["nonce"] = "n-1",
            ["code_challenge"] = OidcRules.S256(verifier), ["code_challenge_method"] = "S256",
        };
        tweak?.Invoke(q);
        // "raw": haengt Text unveraendert an (zum Beispiel einen Parameter doppelt)
        var raw = q.GetValueOrDefault("raw"); q.Remove("raw");
        var url = "/oidc/authorize?" + string.Join('&', q.Where(p => p.Value is not null).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!))) + (raw is null ? "" : "&" + raw);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return Http.SendAsync(req);
    }

    public static Dictionary<string, string> Query(HttpResponseMessage res)
    {
        var loc = res.Headers.Location ?? throw new InvalidOperationException("keine Weiterleitung: " + (int)res.StatusCode);
        return System.Web.HttpUtility.ParseQueryString(loc.Query).AllKeys.ToDictionary(k => k!, k => System.Web.HttpUtility.ParseQueryString(loc.Query)[k]!);
    }

    /// <summary>authorize mit Sitzung: gibt den Code (die Antwort muss eine Weiterleitung an die Adresse der App mit code und state sein).</summary>
    public async Task<string> CodeAsync(App app, string verifier, string? cookie = null, Action<Dictionary<string, string?>>? tweak = null)
    {
        var res = await AuthorizeAsync(app, cookie ?? Cookie, verifier, tweak);
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.StartsWith(app.RedirectUri + "?", res.Headers.Location!.ToString());
        var q = Query(res);
        Assert.Equal(OidcServer.Issuer, q["iss"]);
        return q["code"];
    }

    public Task<HttpResponseMessage> TokenAsync(App app, string? code, string? verifier, bool basic = true, string? secret = null, string grant = "authorization_code", string? redirect = Same, string? clientId = null)
    {
        var form = new Dictionary<string, string>();
        if (grant is not null) form["grant_type"] = grant;
        if (code is not null) form["code"] = code;
        if (redirect is not null) form["redirect_uri"] = redirect == Same ? app.RedirectUri : redirect;
        if (verifier is not null) form["code_verifier"] = verifier;
        var req = new HttpRequestMessage(HttpMethod.Post, "/oidc/token");
        var id = clientId ?? app.ClientId;
        if (basic) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(id) + ":" + Uri.EscapeDataString(secret ?? app.Secret))));
        else { form["client_id"] = id; form["client_secret"] = secret ?? app.Secret; }
        req.Content = new FormUrlEncodedContent(form);
        return Http.SendAsync(req);
    }

    public sealed record Tokens(string IdToken, string AccessToken, JsonWebToken Jwt);

    /// <summary>Der ganze Weg: authorize, token, und das ID-Token geprueft gegen die veroeffentlichten Schluessel (wie es eine App taete).</summary>
    public async Task<Tokens> SignInAsync(App app, string? cookie = null, Action<Dictionary<string, string?>>? tweak = null)
    {
        var verifier = NewVerifier();
        var code = await CodeAsync(app, verifier, cookie, tweak);
        var res = await TokenAsync(app, code, verifier);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Json(res);
        var idToken = body.GetProperty("id_token").GetString()!;
        return new Tokens(idToken, body.GetProperty("access_token").GetString()!, await ValidateAsync(idToken, app.ClientId));
    }

    public async Task<IList<SecurityKey>> PublishedKeysAsync()
    {
        var json = await (await Http.GetAsync("/oidc/jwks")).Content.ReadAsStringAsync();
        return new JsonWebKeySet(json).GetSigningKeys();
    }

    /// <summary>Prueft ein Token wie eine App: Signatur mit den veroeffentlichten Schluesseln (nur ES256), Aussteller, Zielgruppe, Laufzeit.</summary>
    public async Task<JsonWebToken> ValidateAsync(string token, string audience, bool lifetime = true)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OidcServer.Issuer, ValidAudience = audience, IssuerSigningKeys = await PublishedKeysAsync(), ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256], ValidateLifetime = lifetime,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return (JsonWebToken)result.SecurityToken;
    }

    public static string? Claim(JsonWebToken t, string name) => t.TryGetPayloadValue<string>(name, out var v) ? v : null;
}
