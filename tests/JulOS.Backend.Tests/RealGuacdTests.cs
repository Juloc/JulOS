using System.Net.WebSockets;
using JulOS.Integrations.Guacamole;
using Microsoft.Extensions.Logging.Abstractions;

namespace JulOS.Backend.Tests;

/// <summary>
/// Gegen ein echtes guacd der Test-Instanz (JULOS_TEST_GUACD=host:port) und die Wegwerf-Ziele dort.
/// Ohne die Umgebungsvariablen werden die Tests uebersprungen (deploy/dev/test.sh setzt sie).
/// </summary>
public sealed class RealGuacdTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static GuacdOptions Guacd()
    {
        var v = Environment.GetEnvironmentVariable("JULOS_TEST_GUACD");
        Assert.SkipWhen(v is null, "JULOS_TEST_GUACD nicht gesetzt (deploy/dev/test.sh benutzen)");
        var parts = v!.Split(':');
        return new GuacdOptions(parts[0], int.Parse(parts[1]));
    }

    private static string Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        Assert.SkipWhen(v is null, $"{name} nicht gesetzt");
        return v!;
    }

    /// <summary>Liest Anweisungen von guacd, bis die Bedingung erfuellt ist oder die Zeit ablaeuft. Gibt die Opcodes mit Zeitstempel zurueck.</summary>
    private static async Task<List<(string Op, long Ms, string[] Ins)>> CollectAsync(GuacdConnection conn, Func<string[], bool> stop)
    {
        var list = new List<(string, long, string[])>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Wait);
        while (true)
        {
            var block = await conn.Reader.ReadBlockAsync(cts.Token);
            if (block.IsEmpty) break;
            foreach (var ins in Split(block.Span))
            {
                list.Add((ins[0], clock.ElapsedMilliseconds, ins));
                // wie der Browser: jede sync-Anweisung quittieren, sonst haelt guacd den Benutzer fuer abwesend
                if (ins[0] == "sync") await conn.Stream.WriteAsync(GuacProtocol.Encode("sync", ins[1]), cts.Token);
                if (stop(ins)) return list;
            }
        }
        return list;
    }

    private static List<string[]> Split(ReadOnlySpan<byte> block)
    {
        var result = new List<string[]>();
        var s = new InstructionScanner();
        var start = 0;
        for (var i = 0; i < block.Length; i++)
            if (s.Scan(block.Slice(i, 1)) >= 0) { result.Add(GuacProtocol.Parse(block[start..(i + 1)])); start = i + 1; }
        return result;
    }

    [Fact]
    public async Task A_missing_target_comes_back_as_an_error_not_a_hang()
    {
        var opts = Guacd();
        var spec = GuacSpec.Desktop("gibt-es-nicht.invalid", 3389, "u", "p", 800, 600, 96);
        using var conn = await GuacdClient.ConnectAsync(opts.Host, opts.Port, spec, CancellationToken.None);
        var seen = await CollectAsync(conn, ins => ins[0] == "error");
        Assert.Contains(seen, s => s.Op == "error");
    }

    [Fact]
    public async Task Rdp_desktop_delivers_a_full_picture_within_a_few_seconds_and_stays_connected()
    {
        var opts = Guacd();
        var host = Environment.GetEnvironmentVariable("JULOS_TEST_RDP_HOST");
        Assert.SkipWhen(host is null, "JULOS_TEST_RDP_HOST nicht gesetzt (Test-Desktop der Test-Instanz)");
        var spec = GuacSpec.Desktop(host!, 3389, "ubuntu", "ubuntu", 856, 1448, 96);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var conn = await GuacdClient.ConnectAsync(opts.Host, opts.Port, spec, CancellationToken.None);
        var handshake = clock.ElapsedMilliseconds;

        // Bilddaten mitzaehlen: ein leerer/schwarzer Bildschirm waere winzig, ein Desktop hat mehrere zehntausend Bytes (bei flachem Test-Desktop mindestens ca. 17 kB)
        long bytes = 0, firstFullPicture = -1, firstBlob = -1;
        var ops = new Dictionary<string, int>();
        var seen = await CollectAsync(conn, ins =>
        {
            ops[ins[0]] = ops.GetValueOrDefault(ins[0]) + 1;
            if (ins[0] == "blob") { bytes += ins[2].Length * 3 / 4; if (firstBlob < 0) firstBlob = clock.ElapsedMilliseconds; }
            if (firstFullPicture < 0 && bytes > 8_000) firstFullPicture = clock.ElapsedMilliseconds;
            return ins[0] == "error" || clock.ElapsedMilliseconds > 8000;     // 8 s lang zuhoeren (mit quittierten sync-Anweisungen)
        });

        Assert.DoesNotContain(seen, s => s.Op == "error");
        Assert.True(handshake < 2000, $"Handshake dauerte {handshake} ms");
        var detail = $"erster Blob {firstBlob} ms, {bytes} Bytes, Anweisungen: {string.Join(' ', ops.OrderByDescending(o => o.Value).Take(8).Select(o => o.Key + '=' + o.Value))}";
        Assert.True(firstFullPicture is >= 0 && firstFullPicture <= int.Parse(Environment.GetEnvironmentVariable("JULOS_TEST_MAXMS") ?? "5000"), $"volles Bild nach {firstFullPicture} ms ({detail})");                              // volles Bild in unter 5 s (hier ca. 1,3 s)
    }
}
