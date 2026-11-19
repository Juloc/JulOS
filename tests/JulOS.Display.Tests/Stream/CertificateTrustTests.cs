using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Vertrauen auf erste Nutzung fuer das TLS-Zertifikat eines RDP-Ziels: die Regeln ohne Netz, dazu der echte xrdp der Test-Instanz.</summary>
public sealed class CertificateTrustTests
{
    private const string A = "SHA256:AA:AA", B = "SHA256:BB:BB";

    [Fact]
    public void The_first_certificate_is_taken_and_a_known_one_is_known()
    {
        Assert.Equal(CertificateDecision.FirstUse, CertificatePolicy.Decide(null, null, A));
        Assert.Equal(CertificateDecision.FirstUse, CertificatePolicy.Decide("", null, A));
        Assert.Equal(CertificateDecision.Known, CertificatePolicy.Decide(A, null, A));
    }

    [Fact]
    public void A_changed_certificate_is_never_taken_silently_and_only_exactly_the_confirmed_one_counts()
    {
        Assert.Equal(CertificateDecision.Changed, CertificatePolicy.Decide(A, null, B));
        Assert.Equal(CertificateDecision.Changed, CertificatePolicy.Decide(A, "SHA256:CC:CC", B));
        Assert.Equal(CertificateDecision.Changed, CertificatePolicy.Decide(A, A, B));       // das alte zu bestaetigen hilft nicht
        Assert.Equal(CertificateDecision.AcceptedChange, CertificatePolicy.Decide(A, B, B));
    }

    [Fact]
    public void The_fingerprint_is_written_like_openssl_does()
    {
        Assert.Equal("SHA256:00:0F:A0:FF", CertificateFingerprint.Of([0x00, 0x0F, 0xA0, 0xFF]));
    }

    [Fact]
    public async Task A_new_or_confirmed_certificate_is_saved_after_the_logon_and_a_known_or_refused_one_is_not()
    {
        var saved = new List<string>();
        Task Save(string fingerprint, CancellationToken ct) { saved.Add(fingerprint); return Task.CompletedTask; }

        var first = new CertificateTrust(null, null, Save);
        Assert.True(first.Check(A));
        Assert.Empty(saved);                                  // erst nach der Anmeldung (CommitAsync), nicht schon beim Handshake
        Assert.True(await first.CommitAsync(default));
        Assert.Equal([A], saved);

        Assert.True(new CertificateTrust(A, null, Save).Check(A));
        Assert.False(await new CertificateTrust(A, null, Save).CommitAsync(default));

        var refused = new CertificateTrust(A, null, Save);
        Assert.False(refused.Check(B));
        Assert.Equal(B, refused.Presented);
        Assert.False(await refused.CommitAsync(default));
        Assert.Equal([A], saved);

        var accepted = new CertificateTrust(A, B, Save);
        Assert.True(accepted.Check(B));
        Assert.True(await accepted.CommitAsync(default));
        Assert.Equal([A, B], saved);
    }

    [Fact]
    public void The_warning_carries_both_fingerprints_and_the_error_code_800()
    {
        var message = ScreenProtocol.CertificateChanged(B, A);
        Assert.Equal(4, message[0]);
        Assert.Equal(800, (message[1] << 8) | message[2]);
        Assert.Equal(B + "\n" + A, System.Text.Encoding.UTF8.GetString(message, 3, message.Length - 3));
        Assert.EndsWith("\n", System.Text.Encoding.UTF8.GetString(ScreenProtocol.CertificateChanged(B, null)));
    }

    private static string Host()
    {
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_RDP_HOST nicht gesetzt (deploy/dev/test-display.sh benutzen)");
        Assert.SkipUnless(ScreenStream.IsAvailable, ScreenStream.UnavailableReason ?? "libopenh264 fehlt");
        return host!;
    }

    private static async Task<(string? Presented, RdpException? Failure)> ConnectAsync(string host, CertificateTrust trust, string password = "ubuntu")
    {
        var options = new RdpOptions { Host = host, UserName = "ubuntu", Password = password, Width = 800, Height = 600, CertificateCheck = trust.Check };
        await using var stream = new ScreenStream(options, trust: trust);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = stream.RunAsync(cts.Token);
        try
        {
            // angemeldet = die Groesse wird gemeldet
            while (await stream.Events.WaitToReadAsync(cts.Token))
            {
                while (stream.Events.TryRead(out var evt))
                {
                    if (evt is ScreenSize)
                    {
                        await cts.CancelAsync();
                        try { await run; } catch (OperationCanceledException) { /* beendet */ }
                        return (trust.Presented, null);
                    }
                }
            }

            await run;
            return (trust.Presented, null);
        }
        catch (RdpException e)
        {
            return (trust.Presented, e);
        }
    }

    [Fact]
    public async Task The_real_desktop_is_remembered_on_first_use_known_afterwards_and_a_changed_certificate_stops_before_the_password_is_sent()
    {
        var host = Host();
        string? remembered = null;
        Task Save(string fingerprint, CancellationToken ct) { remembered = fingerprint; return Task.CompletedTask; }

        var (presented, failure) = await ConnectAsync(host, new CertificateTrust(null, null, Save));
        Assert.Null(failure);
        Assert.StartsWith("SHA256:", presented);
        Assert.Equal(presented, remembered);

        var known = new CertificateTrust(remembered, null, Save);
        (_, failure) = await ConnectAsync(host, known);
        Assert.Null(failure);
        Assert.Equal(CertificateDecision.Known, known.Decision);

        // Gemerkt ist ein anderes Zertifikat, und das Passwort ist falsch: es kommt die Warnung, nicht die Ablehnung der Anmeldung - es wurde nichts gesendet
        var changed = new CertificateTrust("SHA256:00:11", null, Save);
        (presented, failure) = await ConnectAsync(host, changed, password: "falsch");
        Assert.NotNull(failure);
        Assert.False(failure!.IsLogonFailure);
        Assert.Equal(remembered, failure.CertificateFingerprint);

        // Mit genau dem gezeigten Fingerabdruck als Bestaetigung geht es weiter und das neue Zertifikat wird gemerkt
        remembered = null;
        var accepted = new CertificateTrust("SHA256:00:11", presented, Save);
        (_, failure) = await ConnectAsync(host, accepted);
        Assert.Null(failure);
        Assert.Equal(presented, remembered);
    }
}
