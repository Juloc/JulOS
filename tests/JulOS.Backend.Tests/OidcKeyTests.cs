using System.Net;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using static JulOS.Backend.Tests.ApiKit;

namespace JulOS.Backend.Tests;

/// <summary>Die Signaturschluessel: der private Teil nur verschluesselt gespeichert, nie veroeffentlicht; Rotation mit Schonfrist fuer die Token des alten Schluessels.</summary>
public sealed class OidcKeyTests
{
    [Fact]
    public async Task The_private_key_is_stored_sealed_and_never_published_and_a_rotation_keeps_the_old_public_key_until_its_tokens_have_expired()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var rig = await OidcRig.CreateAsync(time: time);
        var app = await rig.AddAppAsync();
        var ct = TestContext.Current.CancellationToken;

        var before = await rig.SignInAsync(app);                                           // der erste Schluessel entsteht beim ersten Bedarf
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_keys"));
        var stored = await rig.Db.ScalarAsync<byte[]>("select private_sealed from oidc_keys");
        var kidId = await rig.Db.ScalarAsync<Guid>("select id from oidc_keys");
        // verschluesselt: mit einem anderen Server-Schluessel nicht zu oeffnen; kein PKCS#8-Anfang im Klartext
        Assert.Null(new SecretBox(Convert.ToBase64String(new byte[32])).Decrypt(stored, kidId));
        Assert.DoesNotContain("MIGH", System.Text.Encoding.ASCII.GetString(stored));
        Assert.DoesNotContain("\"d\"", await (await rig.Http.GetAsync("/oidc/jwks")).Content.ReadAsStringAsync(ct));

        // Rotation (mit Freigabe durch den zweiten Faktor, die die Anmeldung gerade gab)
        var rotate = await rig.Http.SendAsync(Req(HttpMethod.Post, "/api/security/oidc/keys/rotate", rig.Cookie, new { }));
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var newKid = (await Json(rotate)).GetProperty("kid").GetString();
        Assert.NotEqual(before.Jwt.Kid, newKid);
        var jwks = (await Json(await rig.Http.GetAsync("/oidc/jwks"))).GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();
        Assert.Equal(2, jwks.Length);                                                       // beide stehen im JWKS
        Assert.Contains(before.Jwt.Kid, jwks);
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_keys where private_sealed is not null"));   // der alte kann nicht mehr signieren
        await rig.ValidateAsync(before.IdToken, app.ClientId);                              // das alte Token gilt weiter
        var after = await rig.SignInAsync(app);
        Assert.Equal(newKid, after.Jwt.Kid);                                                // neue Token mit dem neuen Schluessel

        // nach Ablauf der Schonfrist (Lebensdauer der Token plus Abweichung) verschwindet der alte aus dem JWKS, bleibt aber fuer id_token_hint
        time.Advance(OidcKeys.Grace + TimeSpan.FromMinutes(1));
        var later = (await Json(await rig.Http.GetAsync("/oidc/jwks"))).GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();
        Assert.Equal([newKid], later);
        var oidc = rig.Server.Services.GetRequiredService<OidcService>();
        Assert.NotNull(await oidc.ReadHintAsync(before.IdToken, ct));
        time.Advance(OidcKeys.Keep);
        Assert.Equal(1, await rig.Keys.PurgeAsync(ct));
        Assert.Null(await oidc.ReadHintAsync(before.IdToken, ct));                          // nach 30 Tagen auch dafuer nicht mehr
        Assert.NotNull(await oidc.ReadHintAsync(after.IdToken, ct));
    }

    [Fact]
    public async Task An_active_key_this_server_cannot_open_is_replaced_instead_of_blocking_every_sign_in()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var rig = await OidcRig.CreateAsync(time: time);
        var app = await rig.AddAppAsync();
        var first = await rig.SignInAsync(app);
        // zum Beispiel nach einer Wiederherstellung ohne den alten Server-Schluessel: der verschluesselte Teil ist nicht mehr lesbar
        await rig.Db.ExecAsync("update oidc_keys set private_sealed = $1", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 });
        time.Advance(TimeSpan.FromMinutes(1));                                           // der Zwischenspeicher der Schluessel ist abgelaufen
        var second = await rig.SignInAsync(app);
        Assert.NotEqual(first.Jwt.Kid, second.Jwt.Kid);
        Assert.Equal(2, await rig.Db.ScalarAsync<long>("select count(*) from oidc_keys"));
        Assert.Equal(1, await rig.Db.ScalarAsync<long>("select count(*) from oidc_keys where retired_at is null"));
    }
}
