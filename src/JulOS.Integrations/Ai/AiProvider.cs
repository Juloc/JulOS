using System.Text.RegularExpressions;

namespace JulOS.Integrations.Ai;

/// <summary>Ein Modell eines Anbieters. Efforts: die Denkstufen, die es kennt (leer: keine).</summary>
public sealed record AiModel(string Id, string Name, string[] Efforts, string? DefaultEffort);

public sealed record AiImage(string MediaType, string Base64);

/// <summary>Eine Nachricht (Rolle system, user oder assistant). Bilder nur dort, wo der Anbieter sie kann.</summary>
public sealed record AiMessage(string Role, string Text, IReadOnlyList<AiImage>? Images = null);

public sealed record AiChatRequest(string Model, IReadOnlyList<AiMessage> Messages, string? Effort, int MaxTokens);

public readonly record struct AiUsage(long InputTokens, long OutputTokens);

/// <summary>Ein Stueck der Antwort: Text, oder am Ende der Verbrauch.</summary>
public sealed record AiChunk(string? Text, AiUsage? Usage = null);

/// <summary>
/// Zustand eines Anbieters als Code (die Oberflaeche uebersetzt ihn): off, no_key, not_installed, downloading, install_failed, not_signed_in, login_waiting, ready, error.
/// Detail ist hoechstens ein kurzer Text ohne Geheimnis.
/// </summary>
public sealed record AiProviderState(string Status, string? Detail = null)
{
    public bool Ready => Status == "ready";
}

/// <summary>
/// Fehler eines Anbieters mit festem Code (not_signed_in, provider_unavailable, plan_limit, timeout, provider_error, bad_request). Der Text ist kurz und frei von Geheimnissen
/// (<see cref="AiText.Safe"/>); er darf an die aufrufende App weitergegeben werden.
/// </summary>
public sealed class AiProviderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Die eine Schnittstelle aller Anbieter (docs/AI-GATEWAY.md): Modelle, eine Anfrage als Strom von Textstuecken mit dem Verbrauch am Ende, Zustand. Abbruch ueber das Token.</summary>
public interface IAiProvider
{
    string Id { get; }
    Task<AiProviderState> GetStateAsync(CancellationToken ct);
    Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct);
    IAsyncEnumerable<AiChunk> ChatAsync(AiChatRequest request, CancellationToken ct);
}

/// <summary>Texte von Anbietern, bevor sie eine App sieht: gekuerzt und ohne alles, was nach Schluessel aussieht.</summary>
public static partial class AiText
{
    public const int MaxErrorLength = 200;

    [GeneratedRegex(@"(?i)bearer\s+\S+|\bsk-[A-Za-z0-9_\-]{6,}|\bjos_ai_\S+|[A-Za-z0-9_\-\.]{32,}", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Secretish();

    public static string Safe(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string cleaned;
        try { cleaned = Secretish().Replace(text, "[entfernt]"); }
        catch (RegexMatchTimeoutException) { return ""; }
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= MaxErrorLength ? cleaned : cleaned[..MaxErrorLength] + "…";
    }

    /// <summary>Grobe Schaetzung (4 Zeichen je Token), nur wenn der Anbieter keinen Verbrauch nennt, zum Beispiel nach einem Abbruch.</summary>
    public static long EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
