using System.Text;
using JulOS.Integrations.Ssh;

namespace JulOS.Backend.Tests;

/// <summary>
/// SshNetSession gegen das Test-SSH-Ziel der Test-Instanz (JULOS_TEST_SSH=host:port, JULOS_TEST_TARGET_PASSWORD; deploy/dev/test.sh setzt sie).
/// Ohne die Umgebungsvariablen werden die Tests uebersprungen.
/// </summary>
public sealed class RealSshTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    internal static (string Host, int Port, string Password) Target()
    {
        var v = Environment.GetEnvironmentVariable("JULOS_TEST_SSH");
        var password = Environment.GetEnvironmentVariable("JULOS_TEST_TARGET_PASSWORD");
        Assert.SkipWhen(v is null || password is null, "JULOS_TEST_SSH / JULOS_TEST_TARGET_PASSWORD nicht gesetzt (deploy/dev/test.sh benutzen)");
        var parts = v!.Split(':');
        return (parts[0], int.Parse(parts[1]), password!);
    }

    /// <summary>Liest, bis der Text vorkommt (oder die Zeit ablaeuft).</summary>
    private static async Task<string> ReadUntilAsync(ISshSession s, string needle)
    {
        var all = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(Wait);
        while (!all.ToString().Contains(needle, StringComparison.Ordinal))
        {
            var n = await s.ReadAsync(buf, cts.Token);
            if (n == 0) break;
            all.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        return all.ToString();
    }

    private static Task WriteAsync(ISshSession s, string text) => s.WriteAsync(Encoding.UTF8.GetBytes(text), CancellationToken.None).AsTask();

    [Fact]
    public async Task Login_runs_commands_in_a_shell_with_a_pty_and_reports_the_host_key()
    {
        var (host, port, password) = Target();
        await using var s = new SshNetSession();
        SshHostKey? seen = null;
        await s.ConnectAsync(new SshConnectOptions(host, port, "tester", password, 100, 30), k => { seen = k; return true; }, CancellationToken.None);
        Assert.NotNull(seen);
        Assert.Equal(seen, s.PresentedHostKey);
        Assert.Matches("^SHA256:[A-Za-z0-9+/]{43}$", seen!.Fingerprint);          // wie "ssh-keygen -l", ohne Auffuellung
        Assert.StartsWith("ssh-", seen.Algorithm);

        await WriteAsync(s, "echo julos-$((6*7)); stty size; tty\n");
        var output = await ReadUntilAsync(s, "/dev/pts");
        Assert.Contains("julos-42", output);
        Assert.Contains("30 100", output);                                       // Zeilen Spalten des Pseudo-Terminals
        Assert.Contains("/dev/pts", output);
    }

    [Fact]
    public async Task Window_change_reaches_the_shell_and_unicode_survives()
    {
        var (host, port, password) = Target();
        await using var s = new SshNetSession();
        await s.ConnectAsync(new SshConnectOptions(host, port, "tester", password, 80, 24), _ => true, CancellationToken.None);
        s.Resize(132, 43);
        await Task.Delay(300);
        await WriteAsync(s, "stty size; echo ä$((1+1))ö€\n");        // gerechnet, damit nicht die Rueckmeldung der eingetippten Zeile gefunden wird
        var output = await ReadUntilAsync(s, "ä2ö€");
        Assert.Contains("43 132", output);
        Assert.Contains("ä2ö€", output);
    }

    [Fact]
    public async Task A_lot_of_output_arrives_complete_and_in_order_and_a_reader_that_stops_does_not_hang_dispose()
    {
        var (host, port, password) = Target();
        await using var s = new SshNetSession();
        await s.ConnectAsync(new SshConnectOptions(host, port, "tester", password, 100, 30), _ => true, CancellationToken.None);
        await WriteAsync(s, "stty -echo; seq 1 200000; echo ENDE-$((40+2))\n");
        var output = await ReadUntilAsync(s, "ENDE-42");
        Assert.Contains("\r\n199999\r\n200000\r\nENDE-42", output);
        Assert.Contains("\r\n2\r\n3\r\n4\r\n", output);
        Assert.Equal(200000, System.Text.RegularExpressions.Regex.Matches(output, @"(?<=[\r\n])\d+\r\n").Count);          // jede Zeile genau einmal

        // Ausgabe, die niemand mehr abholt (der Kanal ist voll): Freigeben muss trotzdem sofort gehen
        var second = new SshNetSession();
        await second.ConnectAsync(new SshConnectOptions(host, port, "tester", password, 100, 30), _ => true, CancellationToken.None);
        await WriteAsync(second, "yes | head -c 50000000\n");
        await Task.Delay(1500);
        var disposing = second.DisposeAsync().AsTask();
        Assert.Same(disposing, await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task Exiting_the_shell_ends_the_read_with_zero()
    {
        var (host, port, password) = Target();
        await using var s = new SshNetSession();
        await s.ConnectAsync(new SshConnectOptions(host, port, "tester", password, 80, 24), _ => true, CancellationToken.None);
        await WriteAsync(s, "exit\n");
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(Wait);
        while (await s.ReadAsync(buf, cts.Token) > 0) { }
    }

    [Fact]
    public async Task A_wrong_password_is_a_logon_failure_without_leaking_it()
    {
        var (host, port, _) = Target();
        await using var s = new SshNetSession();
        var e = await Assert.ThrowsAsync<SshException>(() => s.ConnectAsync(new SshConnectOptions(host, port, "tester", "falsches-passwort-123", 80, 24), _ => true, CancellationToken.None));
        Assert.Equal(SshFailure.LogonFailed, e.Failure);
        Assert.DoesNotContain("falsches-passwort-123", e.ToString().Replace("SshConnectOptions", ""));
    }

    [Fact]
    public async Task A_refused_host_key_stops_before_any_login()
    {
        var (host, port, password) = Target();
        await using var s = new SshNetSession();
        var e = await Assert.ThrowsAsync<SshException>(() => s.ConnectAsync(new SshConnectOptions(host, port, "tester", password, 80, 24), _ => false, CancellationToken.None));
        Assert.Equal(SshFailure.HostKeyRejected, e.Failure);
        Assert.NotNull(s.PresentedHostKey);                                       // der Relay zeigt ihn dem Benutzer
    }

    [Fact]
    public async Task An_unreachable_target_fails_fast_and_a_cancelled_connect_is_not_an_error()
    {
        Target();                                                                 // nur ausfuehren, wo die Test-Instanz laeuft
        await using var s = new SshNetSession();
        var e = await Assert.ThrowsAsync<SshException>(() => s.ConnectAsync(new SshConnectOptions("127.0.0.1", 1, "u", "p", 80, 24) { ConnectTimeout = TimeSpan.FromSeconds(5) }, _ => true, CancellationToken.None));
        Assert.Contains(e.Failure, new[] { SshFailure.Unreachable, SshFailure.Timeout });

        using var cts = new CancellationTokenSource(); cts.Cancel();
        await using var s2 = new SshNetSession();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s2.ConnectAsync(new SshConnectOptions("127.0.0.1", 1, "u", "p", 80, 24), _ => true, cts.Token));
    }
}
