using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

/// <summary>Die reinen Regeln der Anmelde-Zentrale (ohne Datenbank): PKCE nach dem Beispiel des RFC, Adressen, Scopes, Geheimnisse.</summary>
public sealed class OidcRulesTests
{
    [Fact]
    public void Pkce_matches_the_example_of_RFC_7636_and_rejects_anything_else()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        Assert.Equal(challenge, OidcRules.S256(verifier));
        Assert.True(OidcRules.VerifierMatches(verifier, challenge));
        Assert.False(OidcRules.VerifierMatches(verifier + "x", challenge));
        Assert.False(OidcRules.VerifierMatches("kurz", OidcRules.S256("kurz")));                          // zu kurz: kein gueltiger Verifier
        Assert.False(OidcRules.VerifierMatches(null, challenge));
        Assert.False(OidcRules.VerifierMatches(new string('a', 129), challenge));
        Assert.True(OidcRules.ChallengeOk(challenge));
        Assert.False(OidcRules.ChallengeOk("plain-text"));
        Assert.False(OidcRules.ChallengeOk(null));
    }

    [Theory]
    [InlineData("https://app.example/cb", true)]
    [InlineData("https://app.example:8443/a/b?x=1", true)]
    [InlineData("http://localhost:5000/cb", true)]
    [InlineData("http://127.0.0.1/cb", true)]
    [InlineData("http://[::1]:5000/cb", true)]
    [InlineData("http://dev.localhost/cb", true)]
    [InlineData("http://app.example/cb", false)]                  // http nur zu localhost
    [InlineData("http://localhost.evil.example/cb", false)]
    [InlineData("https://*.example/cb", false)]                   // keine Platzhalter
    [InlineData("https://app.example/cb#x", false)]               // kein Fragment
    [InlineData("https://u:p@app.example/cb", false)]             // kein Benutzer:Passwort
    [InlineData("//app.example/cb", false)]
    [InlineData("/cb", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("https://app.example/c b", false)]
    [InlineData("https://app.example/cb\r\nX: y", false)]
    [InlineData("", false)]
    public void Registered_addresses_are_absolute_https_without_wildcards_fragments_or_credentials(string uri, bool ok) =>
        Assert.Equal(ok, OidcRules.UriProblem(uri) is null);

    [Fact]
    public void Scopes_need_openid_and_unknown_ones_are_dropped_not_granted()
    {
        Assert.Equal(["openid"], OidcRules.GrantedScopes("openid")!);
        Assert.Equal(["openid", "profile"], OidcRules.GrantedScopes("profile openid")!.Order());
        Assert.Equal(["openid"], OidcRules.GrantedScopes("openid offline_access admin")!);         // es gibt kein offline_access und keine eigenen Scopes
        Assert.Null(OidcRules.GrantedScopes("profile"));
        Assert.Null(OidcRules.GrantedScopes(null));
        Assert.Null(OidcRules.GrantedScopes(new string('a', 600)));
    }

    [Fact]
    public void Secrets_are_long_random_and_compared_by_hash()
    {
        var a = OidcRules.NewSecret(); var b = OidcRules.NewSecret();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        var hash = OidcRules.Hash(a);
        Assert.Matches("^[a-f0-9]{64}$", hash);
        Assert.True(OidcRules.SecretMatches(a, hash));
        Assert.False(OidcRules.SecretMatches(b, hash));
        Assert.False(OidcRules.SecretMatches(null, hash));
        Assert.False(OidcRules.SecretMatches(a, "kurz"));
        Assert.True(OidcRules.LooksLikeClientId(OidcRules.NewClientId()));
        Assert.False(OidcRules.LooksLikeClientId("x' or '1'='1"));
    }

    [Fact]
    public void The_return_address_gets_its_fields_encoded_and_keeps_a_query_it_already_has()
    {
        Assert.Equal("https://a.example/cb?code=c&state=a%20b%26c", OidcRules.WithQuery("https://a.example/cb", ("code", "c"), ("state", "a b&c")));
        Assert.Equal("https://a.example/cb?x=1&code=c", OidcRules.WithQuery("https://a.example/cb?x=1", ("code", "c"), ("state", null)));
    }

    [Fact]
    public void The_public_address_is_https_or_localhost_and_never_has_a_trailing_slash_or_a_query()
    {
        Assert.Equal("https://julos.example", OidcRules.CheckIssuer(" https://julos.example/ ").Issuer);
        Assert.Equal("http://localhost:8080", OidcRules.CheckIssuer("http://localhost:8080").Issuer);
        Assert.Null(OidcRules.CheckIssuer("http://julos.example").Issuer);
        Assert.Null(OidcRules.CheckIssuer("https://julos.example/?a=b").Issuer);
        Assert.Null(OidcRules.CheckIssuer(null).Issuer);
    }
}
