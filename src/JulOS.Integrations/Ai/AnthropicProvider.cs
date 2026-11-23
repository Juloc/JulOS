using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JulOS.Integrations.Ai;

/// <summary>
/// Anthropic mit einem API-Schluessel (Messages-API als Strom). Adresse fest, Schluessel erst beim Aufruf aus dem Speicher. Die Denkstufe (effort) gibt es hier nicht: sie wird ueberlesen.
/// Den Verbrauch nennen message_start (Eingabe) und message_delta (Ausgabe).
/// </summary>
public sealed class AnthropicProvider(Func<CancellationToken, Task<string?>> keySource, HttpMessageHandler? handler = null) : IAiProvider
{
    public const string BaseUrl = "https://api.anthropic.com/v1/";
    public const string ApiVersion = "2023-06-01";
    private readonly HttpClient _http = AiHttp.Create(new Uri(BaseUrl), handler);

    public string Id => "anthropic";

    public async Task<AiProviderState> GetStateAsync(CancellationToken ct) => await keySource(ct) is null ? new AiProviderState("no_key") : new AiProviderState("ready");

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "models?limit=100", null, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<AiModel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { } name)
                    list.Add(new AiModel(name, m.TryGetProperty("display_name", out var d) && d.GetString() is { Length: > 0 } display ? display : name, [], null));
        return list;
    }

    public async IAsyncEnumerable<AiChunk> ChatAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "messages", Body(request), ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        long input = 0, output = 0; var seen = false;
        await foreach (var (evt, data) in AiHttp.ReadEventsAsync(stream, ct))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            switch (evt)
            {
                case "message_start" when root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var start):
                    input = Number(start, "input_tokens") + Number(start, "cache_creation_input_tokens") + Number(start, "cache_read_input_tokens");
                    output = Number(start, "output_tokens"); seen = true;
                    break;
                case "content_block_delta" when root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } piece:
                    yield return new AiChunk(piece);
                    break;
                case "message_delta" when root.TryGetProperty("usage", out var end):
                    output = Number(end, "output_tokens"); seen = true;
                    break;
                case "error":
                    var type = root.TryGetProperty("error", out var error) && error.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var msg = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    throw new AiProviderException(type is "overloaded_error" or "api_error" ? "provider_unavailable" : "provider_error", AiText.Safe(msg));
            }
        }
        if (seen) yield return new AiChunk(null, new AiUsage(input, output));
    }

    internal static string Body(AiChatRequest request)
    {
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Text));
        var messages = new JsonArray();
        foreach (var m in request.Messages.Where(m => m.Role != "system"))
        {
            JsonNode content = m.Images is { Count: > 0 }
                ? new JsonArray([.. m.Images.Select(i => (JsonNode)new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = i.MediaType, ["data"] = i.Base64 } }), new JsonObject { ["type"] = "text", ["text"] = m.Text }])
                : JsonValue.Create(m.Text)!;
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = content });
        }
        var body = new JsonObject { ["model"] = request.Model, ["max_tokens"] = request.MaxTokens, ["stream"] = true, ["messages"] = messages };
        if (system.Length > 0) body["system"] = system;
        return body.ToJsonString();
    }

    private static long Number(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        var key = await keySource(ct) ?? throw new AiProviderException("not_signed_in", "Für Anthropic ist kein Schlüssel eingetragen.");
        using var message = new HttpRequestMessage(method, path);
        message.Headers.TryAddWithoutValidation("x-api-key", key);
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (json is not null) message.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        try
        {
            var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return response;
            using (response) throw await AiHttp.ErrorAsync(response, ct);
        }
        catch (HttpRequestException) { throw new AiProviderException("provider_unavailable", "Anthropic ist nicht erreichbar."); }
    }
}
