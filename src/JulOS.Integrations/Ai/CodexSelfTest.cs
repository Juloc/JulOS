using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JulOS.Integrations.Ai;

/// <summary>Das Ergebnis des Selbsttests: wann, ob alles gruen war, welche Pruefungen rot waren (Namen), die Version, die der Container nannte, und die Version, fuer die JulOS getestet hat.</summary>
public sealed record CodexSelfTestResult(DateTimeOffset At, bool Ok, string[] Failed, string? Version, string Pinned = CodexRelease.PinnedVersion)
{
    /// <summary>Wie lange ein gruener Befund gilt: der Test laeuft taeglich, ein Tag und ein halber sind Luft fuer einen Neustart; danach gilt die Bruecke wieder als ungeprueft.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(36);

    /// <summary>Ob dieser Befund die Bruecke fuer Apps freigibt: gruen, fuer die jetzt festgelegte Version, nicht zu alt und nicht aus der Zukunft.</summary>
    public bool Counts(DateTimeOffset now) => Ok && Pinned == CodexRelease.PinnedVersion && now - At < MaxAge && At - now < TimeSpan.FromMinutes(5);
}

/// <summary>
/// Der Selbsttest der Trennung (docs/CODEX-SIDECAR.md): kein Modell, keine Tokens. JulOS legt ein Wort in eine Datei seines Datenordners und fragt das Tor im Container des Programms, ob
/// es von dort aus irgendetwas davon sieht (Datei, Wort in einer Datei, den Schluessel, den Docker-Socket), ob es nicht Root ist, sein Ordner privat ist und das Programm das geprueft
/// abgelegte. Gruen zaehlt nur, wenn JEDE der erwarteten Pruefungen ausdruecklich true meldet; eine fehlende oder unlesbare Antwort ist rot. Das "ok" des Tores wird nicht geglaubt.
/// </summary>
public sealed class CodexSelfTest(ICodexRunner runner, string dataDir, TimeProvider time)
{
    public static readonly string[] Checks = ["uid_not_root", "no_julos_data", "no_docker_socket", "home_private", "codex_pinned"];
    /// <summary>Die Pruefung auf JulOS-Seite: der Container meldet genau die festgelegte Version.</summary>
    public const string VersionCheck = "version_matches";

    public string CanaryPath => Path.Combine(dataDir, "ai", "canary");

    /// <summary>
    /// Das Wort der Kanarien-Datei (0600). Es bleibt zwischen den Laeufen gleich, damit eine von Hand in den Container gelegte Kopie beim naechsten Lauf auch gefunden wird;
    /// die Datei selbst ist schon ein Treffer, wenn der Container sie sieht.
    /// </summary>
    public string EnsureCanary()
    {
        if (File.Exists(CanaryPath) && File.ReadAllText(CanaryPath).Trim() is { Length: 16 } old && old.All(Uri.IsHexDigit)) return old.ToLowerInvariant();
        var word = RandomNumberGenerator.GetHexString(16, lowercase: true);
        Directory.CreateDirectory(Path.GetDirectoryName(CanaryPath)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(CanaryPath, options)) f.Write(Encoding.ASCII.GetBytes(word));
        return word;
    }

    public async Task<CodexSelfTestResult> RunAsync(CancellationToken ct)
    {
        var failed = new List<string>();
        await runner.RefreshAsync(ct);
        var version = runner.Version;
        if (!runner.Available) return new CodexSelfTestResult(time.GetUtcNow(), false, ["unreachable"], null);
        if (version is null || !version.Contains(CodexRelease.PinnedVersion, StringComparison.Ordinal)) failed.Add(VersionCheck);

        JsonElement? answer = null;
        try { answer = await AskAsync(EnsureCanary(), ct); }
        catch (Exception e) when (e is AiProviderException or IOException or JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }
        foreach (var name in Checks)
            if (answer is not { ValueKind: JsonValueKind.Object } a || !a.TryGetProperty("checks", out var c) || c.ValueKind != JsonValueKind.Object
                || !c.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.True) failed.Add(name);
        return new CodexSelfTestResult(time.GetUtcNow(), failed.Count == 0, [.. failed], version);
    }

    private async Task<JsonElement?> AskAsync(string word, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(25));          // das Tor sucht hoechstens 10 Sekunden
        using var process = runner.Start(CodexCommand.SelfTest, new CodexSelfTestArgs(CanaryPath, word));
        using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8);
        var text = reader.ReadToEndAsync(limit.Token);
        var code = await process.WaitForExitAsync(limit.Token);
        if (code != 0) return null;
        var line = (await text).Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return line is null ? null : JsonDocument.Parse(line).RootElement.Clone();
    }
}
