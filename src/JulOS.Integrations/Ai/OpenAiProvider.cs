using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JulOS.Integrations.Ai;

/// <summary>
/// OpenAI mit einem API-Schluessel. Die Adresse ist fest (<see cref="BaseUrl"/>); der Schluessel kommt erst beim Aufruf aus dem Speicher (verschluesselt in der Datenbank, nie im Log).
/// Chat ueber /chat/completions als Strom; den Verbrauch nennt der letzte Block (stream_options.include_usage).
/// </summary>
public sealed partial class OpenAiProvider(Func<CancellationToken, Task<string?>> keySource, HttpMessageHandler? handler = null) : IAiProvider
{
    public const string BaseUrl = "https://api.openai.com/v1/";
    private static readonly string[] Efforts = ["low", "medium", "high"];
    private readonly HttpClient _http = AiHttp.Create(new Uri(BaseUrl), handler);

    public string Id => "openai";

    public async Task<AiProviderState> GetStateAsync(CancellationToken ct) => await keySource(ct) is null ? new AiProviderState("no_key") : new AiProviderState("ready");

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "models", null, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<AiModel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { } name && IsChatModel(name))
                    list.Add(new AiModel(name, name, SupportsEffort(name) ? Efforts : [], null));
        return [.. list.OrderBy(m => m.Id, StringComparer.Ordinal)];
    }

    // Die Liste nennt auch Bild-, Ton- und Einbettungsmodelle; hier zaehlen nur die zum Chatten.
    internal static bool IsChatModel(string id) =>
        (id.StartsWith("gpt-", StringComparison.Ordinal) || OSeries().IsMatch(id) || id.StartsWith("chatgpt-", StringComparison.Ordinal))
        && !new[] { "audio", "realtime", "transcribe", "tts", "image", "embedding", "moderation", "search", "instruct", "whisper" }.Any(id.Contains);

    internal static bool SupportsEffort(string id) => OSeries().IsMatch(id) || id.StartsWith("gpt-5", StringComparison.Ordinal);

    [GeneratedRegex(@"^o\d", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex OSeries();

    public async IAsyncEnumerable<AiChunk> ChatAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "chat/completions", Body(request), ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        AiUsage? usage = null;
        await foreach (var (_, data) in AiHttp.ReadEventsAsync(stream, ct))
        {
            if (data == "[DONE]") break;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new AiProviderException("provider_error", AiText.Safe(error.TryGetProperty("message", out var m) ? m.GetString() : null));
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                usage = new AiUsage(Number(u, "prompt_tokens"), Number(u, "completion_tokens"));
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                yield return new AiChunk(text);
        }
        if (usage is not null) yield return new AiChunk(null, usage);
    }

    internal static string Body(AiChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            JsonNode content = m.Images is { Count: > 0 }
                ? new JsonArray([new JsonObject { ["type"] = "text", ["text"] = m.Text }, .. m.Images.Select(i => (JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{i.MediaType};base64,{i.Base64}" } })])
                : JsonValue.Create(m.Text)!;
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = content });
        }
        var body = new JsonObject
        {
            ["model"] = request.Model, ["messages"] = messages, ["stream"] = true, ["stream_options"] = new JsonObject { ["include_usage"] = true }, ["max_completion_tokens"] = request.MaxTokens,
        };
        if (request.Effort is not null && SupportsEffort(request.Model)) body["reasoning_effort"] = request.Effort;
        return body.ToJsonString();
    }

    private static long Number(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        var key = await keySource(ct) ?? throw new AiProviderException("not_signed_in", "Für OpenAI ist kein Schlüssel eingetragen.");
        using var message = new HttpRequestMessage(method, path);
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (json is not null) message.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        try
        {
            var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return response;
            using (response) throw await AiHttp.ErrorAsync(response, ct);
        }
        catch (HttpRequestException) { throw new AiProviderException("provider_unavailable", "OpenAI ist nicht erreichbar."); }
    }
}
