using System.Text.Json;
using System.Text.Json.Nodes;
using JulOS.Backend.Data;
using JulOS.Integrations.Ai;

namespace JulOS.Frontend.Ai;

/// <summary>
/// Der Selbsttest des Codex-Containers im Betrieb (docs/CODEX-SIDECAR.md): laeuft beim Einschalten der Bruecke, wenn der Container nach einer Pause wieder antwortet, einmal am Tag und
/// auf Knopfdruck; das Ergebnis steht in den Einstellungen der Instanz. Nur mit dem eigenen Container (sonst gibt es nichts zu pruefen: dort gilt die Pruefung von Hand).
/// </summary>
internal sealed class CodexVerification(CodexBridge bridge, AiSettings settings, AiProviderStore providers, IConfiguration configuration, TimeProvider time, ILogger<CodexVerification> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(24), RedAgain = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public bool Applicable => bridge.Runner.Isolated;

    /// <summary>Das gespeicherte Ergebnis oder null.</summary>
    public static CodexSelfTestResult? Read(AiSettings settings)
    {
        if (settings.CodexSelfTest is not { ValueKind: JsonValueKind.Object } e) return null;
        try
        {
            var failed = e.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
            return new CodexSelfTestResult(e.GetProperty("at").GetDateTimeOffset(), e.GetProperty("ok").GetBoolean(), failed,
                e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null, e.GetProperty("pinned").GetString() ?? "");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Fuehrt den Test jetzt aus (einer zur Zeit) und speichert das Ergebnis.</summary>
    public async Task<CodexSelfTestResult> RunNowAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            var data = configuration["JULOS_DATA"];
            var result = string.IsNullOrWhiteSpace(data)
                ? new CodexSelfTestResult(time.GetUtcNow(), false, ["no_data_dir"], null)
                : await new CodexSelfTest(bridge.Runner, data, time).RunAsync(ct);
            var json = new JsonObject
            {
                ["at"] = result.At, ["ok"] = result.Ok, ["pinned"] = result.Pinned, ["version"] = result.Version,
                ["failed"] = new JsonArray([.. result.Failed.Select(x => (JsonNode?)JsonValue.Create(x))]),
            };
            await settings.SetCodexSelfTestAsync(json.ToJsonString(), CancellationToken.None);
            log.LogInformation("Selbsttest des Codex-Containers: {Result}{Failed}", result.Ok ? "gruen" : "rot", result.Ok ? "" : ": " + string.Join(", ", result.Failed));
            return result;
        }
        finally { _running.Release(); }
    }

    /// <summary>Stoesst einen Lauf an (Bruecke eingeschaltet, Container wieder da), ohne zu warten.</summary>
    public void Request() { if (Applicable) { try { _wake.Release(); } catch (SemaphoreFullException) { } } }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!Applicable) return;
        if (bridge.Runner is CodexSocketRunner socket) socket.BecameAvailable += Request;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var woken = await _wake.WaitAsync(TimeSpan.FromMinutes(1), stop);
                if (!settings.Enabled || !(await providers.ListAsync(stop)).TryGetValue("codex", out var f) || !f.Enabled) continue;
                var last = Read(settings);
                var age = last is null ? TimeSpan.MaxValue : time.GetUtcNow() - last.At;
                // Ohne Frage nach dem Container wuerde er nie als "wieder da" erkannt: eine Minute lang nur ein Blick auf die Version
                if (!woken) await bridge.Runner.RefreshAsync(stop);
                if (woken || last is null || last.Pinned != CodexRelease.PinnedVersion || age >= (last.Ok ? Every : RedAgain)) await RunNowAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { log.LogWarning(e, "Selbsttest des Codex-Containers: Lauf fehlgeschlagen"); }
        }
    }
}
