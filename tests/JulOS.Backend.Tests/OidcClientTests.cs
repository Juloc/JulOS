using System.Net;
using System.Security.Claims;
using JulOS.Backend.Logic;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Tests;

/// <summary>
/// Eine kleine Test-App mit Microsofts Standard-Handler (AddOpenIdConnect) gegen JulOS: der Beweis, dass ein fertiger Standard-Client die Zentrale benutzen kann, ohne dass irgendetwas
/// an ihm angepasst wird (nur die Adresse, die Kennung, das Geheimnis). Die Test-App steht unter https://app.example, JulOS wird ueber sein Test-Handler erreicht.
/// </summary>
public sealed class OidcClientTests
{
    private sealed class ClientApp : IAsyncDisposable
    {
        private WebApplication _app = null!;
        public HttpClient Http = null!;
        private readonly Dictionary<string, string> _jar = [];

        public static async Task<ClientApp> StartAsync(OidcRig rig, OidcRig.App registered, string? secret = null, Action<OpenIdConnectOptions>? tweak = null)
        {
            var c = new ClientApp();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri("https://app.example"));
            builder.Services.AddAuthentication(o => { o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme; o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme; })
                .AddCookie()
                .AddOpenIdConnect(o =>
                {
                    o.Authority = OidcServer.Issuer;
                    o.RequireHttpsMetadata = false;                                                  // JulOS steht in den Tests unter http://localhost
                    o.ClientId = registered.ClientId;
                    o.ClientSecret = secret ?? registered.Secret;
                    o.ResponseType = "code";
                    o.UsePkce = true;
                    o.CallbackPath = "/cb";
                    o.SaveTokens = true;
                    o.MapInboundClaims = false;
                    o.GetClaimsFromUserInfoEndpoint = true;                                         // auch userinfo mit dem Zugriffs-Token
                    o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile");
                    o.TokenValidationParameters.NameClaimType = "preferred_username";
                    o.TokenValidationParameters.RoleClaimType = "role";
                    o.BackchannelHttpHandler = rig.Server.Server.CreateHandler();
                    tweak?.Invoke(o);
                });
            builder.Services.AddAuthorization();
            c._app = builder.Build();
            c._app.UseAuthentication();
            c._app.UseAuthorization();
            c._app.MapGet("/me", (ClaimsPrincipal u) => Results.Json(new { name = u.Identity!.Name, role = u.FindFirst("role")?.Value, sub = u.FindFirst("sub")?.Value, sid = u.FindFirst("sid")?.Value })).RequireAuthorization();
            await c._app.StartAsync();
            c.Http = c._app.GetTestClient();
            return c;
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await _app.DisposeAsync(); }

        /// <summary>Ein Aufruf mit dem Cookie-Glas der App (der Test-Client hat keins).</summary>
        public async Task<HttpResponseMessage> SendAsync(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (_jar.Count > 0) req.Headers.Add("Cookie", string.Join("; ", _jar.Select(p => p.Key + "=" + p.Value)));
            var res = await Http.SendAsync(req);
            if (res.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var c in cookies)
                {
                    var pair = c.Split(';')[0]; var i = pair.IndexOf('=');
                    if (c.Contains("expires=Thu, 01 Jan 1970")) _jar.Remove(pair[..i]); else _jar[pair[..i]] = pair[(i + 1)..];
                }
            return res;
        }
    }

    /// <summary>Wie ein Browser: der Aufruf der App, dann JulOS (mit dem Cookie der Person), dann zurueck zur App. Gibt die Antwort der App auf den Rueckruf.</summary>
    private static async Task<(HttpResponseMessage Callback, Uri AuthorizeUrl)> SignInAsync(OidcRig rig, ClientApp client, string? cookie = null, Func<Uri, Uri>? tamper = null)
    {
        var start = await client.SendAsync("/me");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(OidcServer.Issuer + "/oidc/authorize?", authorize.ToString());
        var req = new HttpRequestMessage(HttpMethod.Get, authorize.PathAndQuery);
        req.Headers.Add("Cookie", cookie ?? rig.Cookie);
        var julos = await rig.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Found, julos.StatusCode);
        var back = tamper?.Invoke(julos.Headers.Location!) ?? julos.Headers.Location!;
        return (await client.SendAsync(back.PathAndQuery), authorize);
    }

    [Fact]
    public async Task Microsofts_standard_handler_signs_in_with_code_and_pkce_and_gets_the_role_and_the_name()
    {
        await using var rig = await OidcRig.CreateAsync();
        var reg = await rig.AddAppAsync("Notes", redirects: ["https://app.example/cb"]);
        await using var client = await ClientApp.StartAsync(rig, reg);

        var (callback, authorize) = await SignInAsync(rig, client);
        var q = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);                                           // der Standard-Client benutzt PKCE von sich aus
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/me", callback.Headers.Location!.ToString());                                // der Handler hat alles geprueft (Signatur, iss, aud, nonce, state) und angemeldet

        var me = await client.SendAsync("/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var j = await ApiKit.Json(me);
        Assert.Equal("jul", j.GetProperty("name").GetString());
        Assert.Equal("admin", j.GetProperty("role").GetString());
        Assert.Equal(await rig.Db.ScalarAsync<Guid>("select oidc_sub from users"), Guid.Parse(j.GetProperty("sub").GetString()!));
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));
    }

    [Fact]
    public async Task The_standard_handler_refuses_a_tampered_state_or_code_a_wrong_secret_and_a_person_without_a_role()
    {
        await using var rig = await OidcRig.CreateAsync();
        var reg = await rig.AddAppAsync("Notes", redirects: ["https://app.example/cb"]);

        async Task<bool> SignedIn(ClientApp client) => (await client.SendAsync("/me")).StatusCode == HttpStatusCode.OK;
        static async Task<bool> Failed(Task<(HttpResponseMessage Callback, Uri AuthorizeUrl)> attempt)
        {
            try { var (cb, _) = await attempt; return cb.StatusCode != HttpStatusCode.Found || cb.Headers.Location!.ToString() != "/me"; }
            catch (Exception) { return true; }                                                      // der Handler wirft bei einer ungueltigen Antwort
        }

        await using (var c1 = await ClientApp.StartAsync(rig, reg))
        {
            Assert.True(await Failed(SignInAsync(rig, c1, tamper: u => new Uri(u.ToString().Replace("state=", "state=x")))));         // manipuliertes state
            Assert.False(await SignedIn(c1));
        }
        await using (var c2 = await ClientApp.StartAsync(rig, reg))
        {
            Assert.True(await Failed(SignInAsync(rig, c2, tamper: u => new Uri(u.ToString().Replace("code=", "code=x")))));           // manipulierter Code
            Assert.False(await SignedIn(c2));
        }
        await using (var c3 = await ClientApp.StartAsync(rig, reg, secret: "falsches-geheimnis"))
        {
            Assert.True(await Failed(SignInAsync(rig, c3)));
            Assert.False(await SignedIn(c3));
        }
        // eine Person ohne Rolle: JulOS stellt gar keinen Code aus, die App bekommt keinen Rueckruf
        var anna = await rig.SecondPersonAsync();
        await using (var c4 = await ClientApp.StartAsync(rig, reg))
        {
            var start = await c4.SendAsync("/me");
            var req = new HttpRequestMessage(HttpMethod.Get, start.Headers.Location!.PathAndQuery);
            req.Headers.Add("Cookie", anna.Cookie);
            var res = await rig.Http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Null(res.Headers.Location);
        }
        // nach all dem hat kein Versuch einen Grant hinterlassen
        Assert.Equal(0, await rig.Db.ScalarAsync<long>("select count(*) from oidc_grants"));
    }
}
