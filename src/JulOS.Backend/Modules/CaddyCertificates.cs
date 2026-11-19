using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace JulOS.Backend.Modules;

/// <summary>State: ok, warn (laeuft in hoechstens 14 Tagen ab), expired, error (Handshake fehlgeschlagen), skipped (nicht pruefbar). Error: timeout, refused, handshake, notAllowed.</summary>
public sealed record CertInfo(string Domain, string State, string? Issuer, DateTimeOffset? NotAfter, int? DaysLeft, bool Trusted, string? Error);
/// <summary>Die interne CA von Caddy (Admin-API /pki/ca/local), falls es sie gibt.</summary>
public sealed record CaInfo(string Name, string? Intermediate, DateTimeOffset? NotAfter, int? DaysLeft);
public sealed record CaddyCertificates(CertInfo[] Certificates, CaInfo? Ca, DateTimeOffset CheckedAt);

public static class CaddyCerts
{
    public const int WarnDays = 14, MaxDomains = 40;

    /// <summary>Die Domains der Konfiguration, die sich per TLS pruefen lassen: Namen mit Punkt, ohne Platzhalter, Adressen, localhost und Schema.</summary>
    public static string[] Domains(IEnumerable<CaddyRoute> routes) =>
        [.. routes.Select(r => r.Domain.Trim().ToLowerInvariant()).Where(Checkable).Distinct().Take(MaxDomains)];

    public static bool Checkable(string d) =>
        d.Length is > 3 and <= 253 && d.Contains('.') && !d.Contains('*') && !d.Contains('/') && !d.Contains(':') && !d.Contains('{') && !d.Contains(' ')
        && !IPAddress.TryParse(d, out _) && !d.EndsWith(".localhost") && !d.EndsWith(".local") && !d.EndsWith(".internal");

    public static CertInfo Classify(string domain, string? issuer, DateTimeOffset notAfter, bool trusted, DateTimeOffset now)
    {
        var days = (int)Math.Floor((notAfter - now).TotalDays);
        var state = notAfter <= now ? "expired" : days <= WarnDays ? "warn" : "ok";
        return new CertInfo(domain, state, issuer, notAfter, days, trusted, null);
    }

    /// <summary>
    /// Ein TLS-Handshake mit dem Namen als SNI. Verbunden wird mit <paramref name="connect"/> (JULOS_CADDY_TLS_CONNECT, Host:Port, zum Beispiel der Caddy im eigenen Netz), sonst mit der Domain auf Port 443.
    /// Es wird nichts gesendet ausser dem Handshake; das Zertifikat wird gelesen, nicht als vertrauenswuerdig angenommen (Trusted sagt, ob die Kette stimmt). Adressen, zu denen nie verbunden wird, siehe <see cref="UpstreamTarget.IsForbidden"/>.
    /// </summary>
    public static async Task<CertInfo> ProbeAsync(string domain, UpstreamTarget? connect, TimeProvider time, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        var host = connect?.Host ?? domain;
        var port = connect?.Port ?? 443;
        try
        {
            var all = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, cts.Token);
            var allowed = all.Where(a => !UpstreamTarget.IsForbidden(a)).ToArray();
            if (allowed.Length == 0) return new CertInfo(domain, "skipped", null, null, null, false, "notAllowed");
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(allowed, port, cts.Token);
            X509Certificate2? cert = null;
            var errors = SslPolicyErrors.None;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, e) => { if (c is not null) cert = new X509Certificate2(c); errors = e; return true; });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = domain }, cts.Token);
            if (cert is null) return new CertInfo(domain, "error", null, null, null, false, "handshake");
            using (cert)
                return Classify(domain, IssuerName(cert), new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero), errors == SslPolicyErrors.None, time.GetUtcNow());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new CertInfo(domain, "error", null, null, null, false, "timeout"); }
        catch (SocketException) { return new CertInfo(domain, "error", null, null, null, false, "refused"); }
        catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or IOException) { return new CertInfo(domain, "error", null, null, null, false, "handshake"); }
    }

    private static string? IssuerName(X509Certificate2 c)
    {
        var name = c.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        return string.IsNullOrWhiteSpace(name) ? null : CaddyRedact.Clip(name);
    }

    /// <summary>Liest aus der Antwort von GET /pki/ca/local den Namen und das Ablaufdatum des Zwischenzertifikats.</summary>
    public static CaInfo? ParseCa(System.Text.Json.JsonElement ca, DateTimeOffset now)
    {
        if (ca.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        string? S(string n) => ca.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        var name = S("name") ?? S("id") ?? "local";
        DateTimeOffset? after = null;
        if (S("intermediate_certificate") is { Length: > 0 } pem)
        {
            try { using var c = X509Certificate2.CreateFromPem(pem); after = new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero); }
            catch (System.Security.Cryptography.CryptographicException) { /* Zertifikat nicht lesbar: nur der Name */ }
        }
        return new CaInfo(CaddyRedact.Clip(name), S("intermediate_common_name") is { } cn ? CaddyRedact.Clip(cn) : null, after, after is { } a ? (int)Math.Floor((a - now).TotalDays) : null);
    }
}
