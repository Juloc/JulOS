using System.Runtime.CompilerServices;

namespace JulOS.Integrations.Ai;

/// <summary>
/// Ein Anbieter ohne Netz und ohne Konto: antwortet mit dem Echo der letzten Nachricht, in Stuecken, und nennt einen Verbrauch. Er ist fuer Tests und fuer das Ausprobieren auf einem
/// Entwicklungs-Stack da und wird nur mit JULOS_AI_FAKE=on eingetragen (sonst gibt es ihn nicht). Besondere Woerter in der Nachricht: "FAIL" (Fehler), "SLOW" (wartet, bis abgebrochen wird).
/// </summary>
public sealed class FakeAiProvider : IAiProvider
{
    public string Id => "fake";

    /// <summary>Zaehlt die Aufrufe von ChatAsync, die den Anbieter wirklich erreicht haben (Tests pruefen damit, dass eine abgelehnte Anfrage ihn nie erreicht).</summary>
    public int Calls;

    /// <summary>Wird gesetzt, wenn ein Aufruf durch Abbruch endete.</summary>
    public int Aborted;

    public Task<AiProviderState> GetStateAsync(CancellationToken ct) => Task.FromResult(new AiProviderState("ready"));

    /// <summary>Zaehlt die Aufrufe von ListModelsAsync (Tests pruefen, dass die Modellliste nicht bei jeder Anfrage neu geholt wird).</summary>
    public int ModelCalls;

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AiModel>>(Count([new AiModel("echo", "Echo", ["low", "high"], "low"), new AiModel("echo-big", "Echo big", [], null)]));

    private AiModel[] Count(AiModel[] models) { Interlocked.Increment(ref ModelCalls); return models; }

    public async IAsyncEnumerable<AiChunk> ChatAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var last = request.Messages[^1].Text;
        if (last.Contains("FAIL", StringComparison.Ordinal)) throw new AiProviderException("provider_error", "Der Anbieter meldet einen Fehler.");
        var reply = "echo: " + last;
        var input = request.Messages.Sum(m => AiText.EstimateTokens(m.Text));
        yield return new AiChunk(reply[..Math.Min(6, reply.Length)]);
        if (last.Contains("SLOW", StringComparison.Ordinal))
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Aborted); throw; }
        }
        if (reply.Length > 6) yield return new AiChunk(reply[6..]);
        // NOUSAGE: der Anbieter nennt nichts; ZERO: er nennt 0 und 0 (die Schaetzung der Buchung muss dann greifen)
        if (last.Contains("NOUSAGE", StringComparison.Ordinal)) yield break;
        yield return new AiChunk(null, last.Contains("ZERO", StringComparison.Ordinal) ? new AiUsage(0, 0) : new AiUsage(input, AiText.EstimateTokens(reply)));
    }
}
