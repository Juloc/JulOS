using System.Buffers.Text;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations.Ai;
using Microsoft.AspNetCore.Http.Features;

namespace JulOS.Frontend.Ai;

internal sealed record AiFail(int Status, string Code, string Message, string? Scope = null, DateTimeOffset? ResetsAt = null, int? RetryAfter = null);

/// <summary>
/// Das Tor fuer die Apps der Familie (docs/AI-GATEWAY.md): prueft den Schluessel, die Rechte, das Ratenlimit und das Budget, ruft den Anbieter, reicht die Antwort als Strom oder ganz weiter
/// und verbucht nur Zaehler. Nichts vom Inhalt einer Anfrage oder Antwort wird gespeichert oder geloggt. Eine abgelehnte Anfrage erreicht den Anbieter nie.
/// </summary>
internal sealed class AiGateway(AiAppStore apps, AiUsageStore usage, AiSettings settings, AiRegistry registry, HitCounter hits, AiAuthLimiter limiter, AiConcurrency concurrency, TimeProvider time, ILogger<AiGateway> log)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];
    public const int MaxImages = 10;

    // ---------------------------------------------------------------- Zugang

    /// <summary>
    /// Der Schluessel aus "Authorization: Bearer ...". Ein Cookie wird nie gelesen. Die Sperre nach Fehlversuchen gilt vor der Pruefung des Schluessels; auch eine Anfrage ohne Schluessel zaehlt.
    /// </summary>
    public async Task<(AiApp? App, AiFail? Fail)> AuthenticateAsync(HttpContext ctx)
    {
        var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (limiter.Blocked(client)) return (null, new AiFail(429, "rate_limited", "Zu viele ungültige Anfragen, bitte später wieder versuchen.", RetryAfter: 60));
        var key = AiRules.BearerKey(ctx.Request.Headers.Authorization.ToString());
        if (key is null) { limiter.Fail(client); return (null, new AiFail(401, "missing_key", "Authorization: Bearer <Schlüssel> fehlt.")); }
        var app = await apps.FindByKeyAsync(key, ctx.RequestAborted);
        if (app is null) { limiter.Fail(client); return (null, new AiFail(401, "invalid_key", "Der Schlüssel ist ungültig.")); }
        if (!app.Active) return (null, new AiFail(403, "app_disabled", "Diese App ist gesperrt."));
        return (app, null);
    }

    /// <summary>Schluessel pruefen und dasselbe Ratenlimit wie fuer /chat anwenden (auch /models und /usage kosten: sie fragen die Datenbank und die Anbieter). Bei einem Fehler ist die Antwort schon geschrieben und das Ergebnis null.</summary>
    public async Task<AiApp?> AuthorizeAsync(HttpContext ctx)
    {
        var (app, fail) = await AuthenticateAsync(ctx);
        if (fail is null && app is not null && !hits.Allow("ai-rate:" + app.Id, app.RatePerMinute, Minute)) fail = new AiFail(429, "rate_limited", $"Mehr als {app.RatePerMinute} Anfragen pro Minute.", RetryAfter: 15);
        if (fail is null) return app;
        await WriteFail(ctx, fail);
        return null;
    }

    public static async Task WriteFail(HttpContext ctx, AiFail fail)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = fail.Status;
        if (fail.RetryAfter is { } s) ctx.Response.Headers.RetryAfter = s.ToString();
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new AiErrorBody(new AiErrorDto(fail.Code, fail.Message, fail.Scope, fail.ResetsAt)), AiJson.Default.AiErrorBody), Encoding.UTF8);
    }

    public static Task WriteJson<T>(HttpContext ctx, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(value, info), Encoding.UTF8);
    }

    // ---------------------------------------------------------------- Modelle und Verbrauch

    /// <summary>Die Modelle der eingeschalteten Anbieter, fuer eine App nur die erlaubten (app null: alle, fuer die Einstellungen). Unavailable: Anbieter, die nicht antworteten.</summary>
    public async Task<(ModelDto[] Models, string[] Unavailable)> ModelsAsync(AiApp? app, CancellationToken ct)
    {
        var models = new List<ModelDto>(); var down = new List<string>();
        foreach (var p in await registry.ActiveAsync(ct, forApps: app is not null))
        {
            if (app is not null && !app.Allowed.Any(a => a == p.Id || a.StartsWith(p.Id + "/", StringComparison.Ordinal))) continue;
            try
            {
                if (!(await p.GetStateAsync(ct)).Ready) { down.Add(p.Id); continue; }
                foreach (var m in await registry.ModelsAsync(p, ct))
                {
                    var id = p.Id + "/" + m.Id;
                    if (app is null || AiRules.Allows(app.Allowed, id)) models.Add(new ModelDto(id, p.Id, m.Name, m.Efforts, id == settings.DefaultModel));
                }
            }
            catch (Exception e) when (e is AiProviderException or HttpRequestException or IOException or TimeoutException) { down.Add(p.Id); }
        }
        return ([.. models], [.. down]);
    }

    public static BudgetDto ToDto(AiBudget b) => new(new BudgetPartDto(b.DayUsed, b.DayLimit), new BudgetPartDto(b.MonthUsed, b.MonthLimit));

    // ---------------------------------------------------------------- Anfrage

    public async Task ChatAsync(HttpContext ctx)
    {
        var (app, fail) = await AuthenticateAsync(ctx);
        if (fail is not null || app is null) { await WriteFail(ctx, fail!); return; }
        var ct = ctx.RequestAborted;
        if (!settings.Enabled) { await WriteFail(ctx, new AiFail(503, "ai_disabled", "Der KI-Zugang ist in JulOS ausgeschaltet.")); return; }
        if (!hits.Allow("ai-rate:" + app.Id, app.RatePerMinute, Minute)) { await WriteFail(ctx, new AiFail(429, "rate_limited", $"Mehr als {app.RatePerMinute} Anfragen pro Minute.", RetryAfter: 15)); return; }

        var (request, stream, providerId, parseFail) = await ParseAsync(ctx, app);
        if (parseFail is not null || request is null) { await WriteFail(ctx, parseFail!); return; }
        var provider = (await registry.ActiveAsync(ct, forApps: true)).FirstOrDefault(p => p.Id == providerId);
        if (provider is null)
        {
            // Die Codex-Bruecke ist eingeschaltet, aber der Besitzer hat die Pruefung von Hand noch nicht bestaetigt: fuer Apps aus
            var unverified = providerId == "codex" && (await registry.ActiveAsync(ct)).Any(p => p.Id == "codex");
            await WriteFail(ctx, unverified ? new AiFail(503, "provider_unverified", registry.Codex.Runner.Isolated ? "Der Selbsttest des Codex-Containers ist nicht grün; die Brücke bleibt für Apps aus." : "Die Codex-Brücke ist noch nicht vom Besitzer freigegeben.") : new AiFail(503, "provider_off", "Dieser Anbieter ist nicht eingeschaltet."));
            return;
        }
        // Nur Modelle, die der Anbieter selbst nennt (dieselbe Liste wie /models, kurz gemerkt): ein frei erfundener Name erreicht weder den Anbieter noch die Tabelle des Verbrauchs
        try
        {
            if (!(await registry.ModelsAsync(provider, ct)).Any(m => m.Id == request.Model)) { await WriteFail(ctx, new AiFail(400, "unknown_model", "Dieses Modell kennt der Anbieter nicht (siehe GET /models).")); return; }
        }
        catch (AiProviderException e) { await WriteFail(ctx, FromProvider(e)); return; }
        catch (Exception e) when (e is HttpRequestException or IOException) { await WriteFail(ctx, new AiFail(503, "provider_unavailable", "Der Anbieter ist gerade nicht erreichbar.")); return; }

        var before = await usage.BudgetAsync(app, ct);
        if (before.Exceeded is { } scope)
        {
            var now = time.GetUtcNow();
            var resets = scope == "day" ? new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero) : new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1), TimeSpan.Zero);
            await WriteFail(ctx, new AiFail(429, "budget_exceeded", scope == "day" ? "Das Tagesbudget dieser App ist aufgebraucht." : "Das Monatsbudget dieser App ist aufgebraucht.", scope, resets));
            return;
        }
        if (!concurrency.TryEnter(app.Id)) { await WriteFail(ctx, new AiFail(429, "too_many_requests", $"Höchstens {AiRules.MaxConcurrentPerApp} Anfragen gleichzeitig.", RetryAfter: 5)); return; }

        var id = "req_" + Guid.NewGuid().ToString("N")[..16];
        var model = providerId + "/" + request.Model;
        var watch = Stopwatch.StartNew();
        var text = new StringBuilder();
        AiUsage? reported = null;
        var recorded = false; var started = false; long tin = 0, tout = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AiRules.MaxDuration);

        async Task<AiBudget> Record(string result)
        {
            recorded = true;
            // Gebucht wird das Groessere aus der Angabe des Anbieters und der Schaetzung; ein Abbruch ohne Angabe kostet mindestens maxTokens (AiRules.Book)
            var estimatedIn = request.Messages.Sum(m => AiText.EstimateTokens(m.Text) + (m.Images?.Count ?? 0) * AiRules.TokensPerImage);
            (tin, tout) = AiRules.Book(reported?.InputTokens ?? 0, reported?.OutputTokens ?? 0, estimatedIn, AiText.EstimateTokens(text.ToString()), result == "aborted", request.MaxTokens);
            try { await usage.RecordAsync(app.Id, providerId, request.Model, result, tin, tout, watch.ElapsedMilliseconds); await apps.TouchAsync(app.Id); }
            catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException) { log.LogError(e, "KI: Verbrauch konnte nicht gebucht werden"); }
            return await usage.BudgetAsync(app, CancellationToken.None);
        }

        async Task Emit(string evt, string json)
        {
            await ctx.Response.WriteAsync($"event: {evt}\ndata: {json}\n\n", cts.Token);
            await ctx.Response.Body.FlushAsync(cts.Token);
        }

        try
        {
            if (before.Warning is { } w0) ctx.Response.Headers["X-AI-Budget-Warning"] = w0;
            await using var chunks = provider.ChatAsync(request, cts.Token).GetAsyncEnumerator(cts.Token);
            var has = await chunks.MoveNextAsync();     // Fehler des Anbieters vor der ersten Antwort werden noch ein richtiger HTTP-Fehler
            if (stream)
            {
                ctx.Response.ContentType = "text/event-stream; charset=utf-8";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                started = true;
                await Emit("start", JsonSerializer.Serialize(new StartEventDto(id, model), AiJson.Default.StartEventDto));
            }
            while (has)
            {
                var chunk = chunks.Current;
                if (chunk.Text is { Length: > 0 } piece)
                {
                    if (text.Length + piece.Length > AiRules.MaxResponseChars) throw new AiProviderException("provider_error", "Die Antwort ist länger als erlaubt.");
                    text.Append(piece); if (stream) await Emit("text", JsonSerializer.Serialize(new TextEventDto(piece), AiJson.Default.TextEventDto));
                }
                if (chunk.Usage is { } u) reported = u;
                has = await chunks.MoveNextAsync();
            }
            var after = await Record("ok");
            var used = new UsageDto(tin, tout);
            if (stream) await Emit("done", JsonSerializer.Serialize(new DoneEventDto(id, model, used, ToDto(after), after.Warning), AiJson.Default.DoneEventDto));
            else await WriteJson(ctx, new ChatResponseDto(id, model, text.ToString(), used, ToDto(after), after.Warning), AiJson.Default.ChatResponseDto);
        }
        catch (Exception e) when (ctx.RequestAborted.IsCancellationRequested && (e is OperationCanceledException or IOException or ObjectDisposedException))
        {
            // Der Aufrufer ist weg: der Anbieter-Aufruf wurde ueber das Token mit abgebrochen
            if (!recorded) await Record("aborted");
        }
        catch (Exception e)
        {
            var failure = e switch
            {
                AiProviderException pe => FromProvider(pe),
                OperationCanceledException => new AiFail(504, "timeout", "Die Anfrage hat zu lange gedauert."),
                _ => new AiFail(503, "provider_unavailable", "Der Anbieter ist gerade nicht erreichbar."),
            };
            // Im Log nur Code und Anbieter, nie ein Inhalt
            log.LogWarning("KI: Anfrage von {App} an {Model} fehlgeschlagen: {Code} ({Type})", app.Name, model, failure.Code, e.GetType().Name);
            if (!recorded) await Record("failed");
            if (started) { try { await Emit("error", JsonSerializer.Serialize(new AiErrorBody(new AiErrorDto(failure.Code, failure.Message)), AiJson.Default.AiErrorBody)); } catch (Exception w) when (w is OperationCanceledException or IOException) { } }
            else await WriteFail(ctx, failure);
        }
        finally
        {
            concurrency.Leave(app.Id);
            if (!recorded) await Record("aborted");
        }
    }

    private static AiFail FromProvider(AiProviderException e) => e.Code switch
    {
        "plan_limit" => new AiFail(429, "plan_limit", e.Message),
        "timeout" => new AiFail(504, "timeout", e.Message),
        "bad_request" => new AiFail(400, "bad_request", e.Message),
        "provider_error" => new AiFail(502, "provider_error", e.Message),
        "not_signed_in" => new AiFail(503, "provider_not_ready", e.Message),
        _ => new AiFail(503, "provider_unavailable", e.Message),
    };

    // ---------------------------------------------------------------- Eingabe

    /// <summary>Liest und prueft den Koerper. Gibt die Anfrage fuer den Anbieter (Modell ohne Anbieter-Vorsatz), ob ein Strom gewuenscht ist, und den Anbieter zurueck.</summary>
    private async Task<(AiChatRequest? Request, bool Stream, string Provider, AiFail? Fail)> ParseAsync(HttpContext ctx, AiApp app)
    {
        AiFail Bad(string message, string code = "bad_request") => new(400, code, message);
        var max = app.AllowFiles ? AiRules.MaxBodyWithFiles : AiRules.MaxBodyTextOnly;
        if (ctx.Request.ContentLength is { } len && len > max) return (null, false, "", new AiFail(413, "request_too_large", "Die Anfrage ist zu groß."));
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = max;
        ChatRequestDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AiJson.Default.ChatRequestDto, ctx.RequestAborted); }
        catch (JsonException) { return (null, false, "", Bad("Der Körper ist kein gültiges JSON.")); }
        catch (BadHttpRequestException) { return (null, false, "", new AiFail(413, "request_too_large", "Die Anfrage ist zu groß.")); }
        if (dto?.Messages is not { Length: > 0 } raw) return (null, false, "", Bad("messages fehlt."));
        if (raw.Length > AiRules.MaxMessages) return (null, false, "", Bad($"Höchstens {AiRules.MaxMessages} Nachrichten."));

        var messages = new List<AiMessage>(); var images = 0; var bytes = 0;
        foreach (var m in raw)
        {
            if (m.Role is not ("system" or "user" or "assistant")) return (null, false, "", Bad("Die Rolle einer Nachricht ist system, user oder assistant."));
            var textBuilder = new StringBuilder(); var pics = new List<AiImage>();
            if (m.Content.ValueKind == JsonValueKind.String) textBuilder.Append(m.Content.GetString());
            else if (m.Content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in m.Content.EnumerateArray())
                {
                    var type = part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "text" && part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) textBuilder.Append(tx.GetString());
                    else if (type == "image")
                    {
                        if (!app.AllowFiles) return (null, false, "", new AiFail(403, "files_not_allowed", "Diese App darf keine Bilder senden."));
                        var mediaType = part.TryGetProperty("mediaType", out var mt) ? mt.GetString() : null;
                        var data = part.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                        if (mediaType is null || !ImageTypes.Contains(mediaType) || data is null || !Base64.IsValid(data)) return (null, false, "", Bad("Ein Bild braucht mediaType (png, jpeg, gif, webp) und data (Base64)."));
                        if (++images > MaxImages) return (null, false, "", Bad($"Höchstens {MaxImages} Bilder."));
                        pics.Add(new AiImage(mediaType, data));
                    }
                    else return (null, false, "", Bad("Ein Teil einer Nachricht ist text oder image."));
                }
            }
            else return (null, false, "", Bad("content ist ein Text oder eine Liste von Teilen."));
            var text = textBuilder.ToString();
            bytes += Encoding.UTF8.GetByteCount(text);
            if (bytes > AiRules.MaxTextBytes) return (null, false, "", new AiFail(413, "request_too_large", "Der Text ist zu lang."));
            messages.Add(new AiMessage(m.Role, text, pics.Count > 0 ? pics : null));
        }
        if (messages[^1].Role == "system" || messages.All(m => m.Role == "system")) return (null, false, "", Bad("Die letzte Nachricht muss user oder assistant sein."));
        if (!AiRules.ValidEffort(dto.Effort)) return (null, false, "", Bad("effort ist minimal, low, medium, high oder xhigh."));
        var maxTokens = dto.MaxTokens ?? AiRules.DefaultMaxTokens;
        if (maxTokens is < 1 or > AiRules.MaxMaxTokens) return (null, false, "", Bad($"maxTokens liegt zwischen 1 und {AiRules.MaxMaxTokens}."));

        var wanted = string.IsNullOrWhiteSpace(dto.Model) || dto.Model == "default" ? settings.DefaultModel : dto.Model;
        if (wanted is null) return (null, false, "", Bad("Es ist kein Standardmodell eingestellt; ein Modell als anbieter/modell angeben.", "no_default_model"));
        if (!AiRules.TrySplitModel(wanted, out var provider, out var model)) return (null, false, "", Bad("Das Modell ist anbieter/modell, zum Beispiel openai/gpt-4.1."));
        if (!AiRules.Allows(app.Allowed, wanted)) return (null, false, "", new AiFail(403, "model_not_allowed", "Diese App darf dieses Modell nicht benutzen."));
        return (new AiChatRequest(model, messages, dto.Effort, maxTokens), dto.Stream == true, provider, null);
    }
}
