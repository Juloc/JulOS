using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Integrations.Ai;

namespace JulOS.Frontend.Ai;

/// <summary>
/// KI-Zugang (docs/AI-GATEWAY.md). Zwei getrennte Teile:
/// 1. <c>/api/ai/v1/*</c> fuer die Apps der Familie: Zugang NUR mit dem Schluessel der App (Authorization: Bearer), ohne Cookie. Das ist die begruendete Ausnahme von "jede API hinter Sitzung":
///    der Schluessel gibt Zugang zu diesen drei Wegen und zu sonst nichts in JulOS; <see cref="ApiGuard"/> laesst den Pfad durch, das Tor (<see cref="AiGateway"/>) prueft.
/// 2. <c>/api/ai/*</c> fuer die Einstellungen: wie alles andere hinter ApiGuard (Sitzung, Origin); jede Aenderung verlangt die frische Freigabe mit dem zweiten Faktor (StepUpPolicy) und steht im Verlauf.
/// </summary>
internal static class AiEndpoints
{
    public const string AppPrefix = "/api/ai/v1";

    /// <summary>Gehoert der Pfad zur API der Apps (Schluessel statt Sitzung)?</summary>
    public static bool IsAppPath(PathString path) => path.Equals(AppPrefix) || path.StartsWithSegments(AppPrefix);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static IResult Error(int status, string message, string code = "invalid") =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(message, Code: code), AiJson.Default.ErrorBody), "application/json", Encoding.UTF8, status);

    public static void MapAi(this IEndpointRouteBuilder app)
    {
        MapAppApi(app);
        MapAdmin(app);
    }

    // ------------------------------------------------------------------ fuer die Apps

    private static void MapAppApi(IEndpointRouteBuilder app)
    {
        app.MapGet(AppPrefix + "/models", async (HttpContext ctx, AiGateway gw, AiSettings settings) =>
        {
            var (a, fail) = await gw.AuthenticateAsync(ctx);
            if (fail is not null || a is null) { await AiGateway.WriteFail(ctx, fail!); return; }
            if (!settings.Enabled) { await AiGateway.WriteFail(ctx, new AiFail(503, "ai_disabled", "Der KI-Zugang ist in JulOS ausgeschaltet.")); return; }
            var (models, down) = await gw.ModelsAsync(a, ctx.RequestAborted);
            await AiGateway.WriteJson(ctx, new ModelsResponseDto(models, settings.DefaultModel is { } d && AiRules.Allows(a.Allowed, d) ? d : null, down), AiJson.Default.ModelsResponseDto);
        });

        app.MapGet(AppPrefix + "/usage", async (HttpContext ctx, AiGateway gw, AiUsageStore usage) =>
        {
            var (a, fail) = await gw.AuthenticateAsync(ctx);
            if (fail is not null || a is null) { await AiGateway.WriteFail(ctx, fail!); return; }
            var budget = await usage.BudgetAsync(a, ctx.RequestAborted);
            var rows = await usage.ListAsync(a.Id, 7, ctx.RequestAborted);
            await AiGateway.WriteJson(ctx, new UsageResponseDto(a.Name, AiGateway.ToDto(budget), budget.Warning ?? budget.Exceeded, a.RatePerMinute,
                [.. rows.Select(r => new UsageDayDto(r.Day.ToString("yyyy-MM-dd"), r.Provider + "/" + r.Model, r.Requests, r.TokensIn, r.TokensOut))]), AiJson.Default.UsageResponseDto);
        });

        app.MapPost(AppPrefix + "/chat", (HttpContext ctx, AiGateway gw) => gw.ChatAsync(ctx));
    }

    // ------------------------------------------------------------------ Einstellungen

    private static void MapAdmin(IEndpointRouteBuilder app)
    {
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Ai");
        SessionIdentity Who(HttpContext ctx) => (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;

        async Task Audit(SecurityAuditStore audit, HttpContext ctx, string evt, string? detail = null)
        {
            log.LogWarning("KI: {Event} von {User}{Detail}", evt, Who(ctx).UserName, detail is null ? "" : ": " + detail);
            try { await audit.AddAsync(Who(ctx).UserName, evt, detail, CancellationToken.None); }
            catch (Exception e) { log.LogError(e, "Verlauf der KI konnte nicht geschrieben werden"); }
        }

        bool Limited(HttpContext ctx, HitCounter hits, string what = "ai-write", int max = 30) => !hits.Allow(what + ":" + Who(ctx).UserId, max, Minute);
        IResult TooMany() => Error(429, "Zu viele Anfragen, bitte kurz warten.", "rate_limited");

        // Nach jeder Aenderung: Ausschalten haelt die Bruecke an, Einschalten laedt das Codex-Programm erst jetzt (wenn dieser Anbieter an ist)
        async Task Apply(AiRegistry registry, AiSettings settings, AiProviderStore providers, CancellationToken ct)
        {
            registry.ForgetModels();
            if (!settings.Enabled) { registry.Codex.Stop(); return; }
            if ((await providers.ListAsync(ct)).TryGetValue("codex", out var f) && f.Enabled) _ = registry.Installer.StartAsync();
        }

        async Task<AppDto> ToAppDto(AiApp a, AiUsageStore usage, CancellationToken ct) =>
            new(a.Id, a.Name, a.KeyHint, a.Active, a.Allowed, a.DailyTokens, a.MonthlyTokens, a.RatePerMinute, a.AllowFiles, a.CreatedAt, a.LastUsedAt, AiGateway.ToDto(await usage.BudgetAsync(a, ct)));

        app.MapGet("/api/ai", async (HttpContext ctx, AiSettings settings, AiRegistry registry, AiProviderStore providers, AiAppStore apps, AiUsageStore usage) =>
        {
            var ct = ctx.RequestAborted;
            var flags = await providers.ListAsync(ct);
            var list = new List<ProviderDto>();
            foreach (var p in registry.All)
            {
                var fake = p.Id == AiRules.FakeId;
                var enabled = fake || flags.TryGetValue(p.Id, out var f) && f.Enabled;
                var keySet = flags.TryGetValue(p.Id, out var k) && k.KeySet;
                AiProviderState state;
                try { state = enabled ? await p.GetStateAsync(ct) : new AiProviderState("off"); }
                catch (Exception e) when (e is AiProviderException or IOException) { state = new AiProviderState("error"); }
                CodexDto? codex = null; PlanUsageDto? plan = null;
                if (p is CodexBridge bridge)
                {
                    var login = bridge.LoginState();
                    codex = new CodexDto(CodexRelease.PinnedVersion, registry.Installer.Supported, new LoginDto(login.State, login.Url, login.Code, login.ExpiresAt));
                    // Das Kontingent nur fragen, wenn die Bruecke ohnehin laeuft (das Ansehen startet nichts)
                    if (enabled && settings.Enabled && bridge.Running && state.Ready)
                    {
                        try { if (await bridge.GetPlanUsageAsync(ct) is { } u) plan = new PlanUsageDto(u.Plan, [.. u.Windows.Select(w => new PlanWindowDto(w.UsedPercent, w.Minutes, w.ResetsAt))]); }
                        catch (AiProviderException) { }
                    }
                }
                list.Add(new ProviderDto(p.Id, enabled, keySet, state.Status, state.Detail, codex, plan));
            }
            var appList = new List<AppDto>();
            foreach (var a in await apps.ListAsync(ct)) appList.Add(await ToAppDto(a, usage, ct));
            return Results.Json(new AiStateDto(settings.Enabled, settings.DefaultModel, providers.CanStoreKeys, [.. list], [.. appList]), AiJson.Default.AiStateDto);
        });

        app.MapGet("/api/ai/models", async (HttpContext ctx, AiGateway gw) =>
        {
            var (models, _) = await gw.ModelsAsync(null, ctx.RequestAborted);
            return Results.Json(new AdminModelsDto(models), AiJson.Default.AdminModelsDto);
        });

        app.MapGet("/api/ai/usage", async (HttpContext ctx, AiUsageStore usage, AiAppStore apps) =>
        {
            var days = int.TryParse(ctx.Request.Query["days"], out var n) ? n : 7;
            var names = (await apps.ListAsync(ctx.RequestAborted)).ToDictionary(a => a.Id, a => a.Name);
            var rows = await usage.ListAsync(null, days, ctx.RequestAborted);
            return Results.Json(new AdminUsageDto([.. rows.Select(r => new UsageRowDto(r.Day.ToString("yyyy-MM-dd"), r.AppId, names.GetValueOrDefault(r.AppId, "?"), r.Provider, r.Model, r.Requests, r.Ok, r.Failed, r.Aborted, r.TokensIn, r.TokensOut, r.Millis))]), AiJson.Default.AdminUsageDto);
        });

        app.MapPut("/api/ai/enabled", async (EnabledRequest body, HttpContext ctx, AiSettings settings, AiRegistry registry, AiProviderStore providers, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            if (body.Enabled is not { } on) return Error(400, "enabled fehlt.");
            await settings.SetEnabledAsync(on, ctx.RequestAborted);
            await Apply(registry, settings, providers, ctx.RequestAborted);
            await Audit(audit, ctx, on ? "ai.enable" : "ai.disable");
            return Results.NoContent();
        });

        app.MapPut("/api/ai/default-model", async (DefaultModelRequest body, HttpContext ctx, AiSettings settings, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            var model = string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim();
            if (model is not null && (!AiRules.TrySplitModel(model, out var p, out _) || !AiRules.ProviderIds.Append(AiRules.FakeId).Contains(p))) return Error(400, "Das Modell ist anbieter/modell.");
            await settings.SetDefaultModelAsync(model, ctx.RequestAborted);
            await Audit(audit, ctx, "ai.default", model ?? "(keins)");
            return Results.NoContent();
        });

        app.MapPut("/api/ai/providers/{id}", async (string id, ProviderRequest body, HttpContext ctx, AiProviderStore providers, AiSettings settings, AiRegistry registry, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            if (!AiRules.ProviderIds.Contains(id)) return Error(404, "Anbieter nicht gefunden.", "not_found");
            if (body.Key is { } key)
            {
                if (id == "codex") return Error(400, "Die Codex-Brücke hat keinen Schlüssel; sie wird mit dem ChatGPT-Konto angemeldet.");
                if (!providers.CanStoreKeys) return Error(409, "Schlüssel speichern ist aus: JULOS_SECRET_KEY setzen oder JULOS_DATA beschreibbar machen.", "no_secret_box");
                key = key.Trim();
                if (key.Length is < 16 or > 300 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return Error(400, "Das ist kein gültiger API-Schlüssel.", "bad_key");
                await providers.SetKeyAsync(id, key, ctx.RequestAborted);
                await Audit(audit, ctx, "ai.key.set", id);          // nie der Schluessel selbst
            }
            if (body.Enabled is { } on)
            {
                await providers.SetEnabledAsync(id, on, ctx.RequestAborted);
                if (!on && id == "codex") registry.Codex.Stop();
                await Audit(audit, ctx, on ? "ai.provider.on" : "ai.provider.off", id);
            }
            await Apply(registry, settings, providers, ctx.RequestAborted);
            return Results.NoContent();
        });

        app.MapDelete("/api/ai/providers/{id}/key", async (string id, HttpContext ctx, AiProviderStore providers, AiRegistry registry, SecurityAuditStore audit) =>
        {
            if (id is not ("openai" or "anthropic")) return Error(404, "Anbieter nicht gefunden.", "not_found");
            await providers.ClearKeyAsync(id, ctx.RequestAborted);
            registry.ForgetModels(id);
            await Audit(audit, ctx, "ai.key.clear", id);
            return Results.NoContent();
        });

        // Probiert den Anbieter aus (listet seine Modelle): aendert nichts und braucht keine Freigabe
        app.MapGet("/api/ai/providers/{id}/test", async (string id, HttpContext ctx, AiRegistry registry, AiSettings settings, HitCounter hits) =>
        {
            if (Limited(ctx, hits, "ai-test", 10)) return TooMany();
            var provider = registry.Get(id);
            if (provider is null) return Error(404, "Anbieter nicht gefunden.", "not_found");
            if (!settings.Enabled) return Error(409, "Der KI-Zugang ist ausgeschaltet.", "ai_off");
            if (!(await registry.ActiveAsync(ctx.RequestAborted)).Contains(provider)) return Error(409, "Dieser Anbieter ist nicht eingeschaltet.", "provider_off");
            try
            {
                var state = await provider.GetStateAsync(ctx.RequestAborted);
                if (!state.Ready) return Results.Json(new TestDto(false, 0, state.Status), AiJson.Default.TestDto);
                registry.ForgetModels(id);
                return Results.Json(new TestDto(true, (await registry.ModelsAsync(provider, ctx.RequestAborted)).Count, null), AiJson.Default.TestDto);
            }
            catch (AiProviderException e) { return Results.Json(new TestDto(false, 0, e.Code), AiJson.Default.TestDto); }
        });

        // ---- Codex: Geraete-Anmeldung ----
        app.MapPost("/api/ai/providers/codex/login", async (HttpContext ctx, AiRegistry registry, AiSettings settings, AiProviderStore providers, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            if (!settings.Enabled) return Error(409, "Der KI-Zugang ist ausgeschaltet.", "ai_off");
            if (!(await providers.ListAsync(ctx.RequestAborted)).TryGetValue("codex", out var f) || !f.Enabled) return Error(409, "Dieser Anbieter ist nicht eingeschaltet.", "provider_off");
            try
            {
                var s = registry.Codex.StartLogin();
                await Audit(audit, ctx, "ai.login.start");
                return Results.Json(new LoginDto(s.State, s.Url, s.Code, s.ExpiresAt), AiJson.Default.LoginDto);
            }
            catch (AiProviderException e) { return Error(409, e.Message, e.Code); }
        });

        app.MapDelete("/api/ai/providers/codex/login", async (HttpContext ctx, AiRegistry registry, SecurityAuditStore audit) =>
        {
            registry.Codex.CancelLogin();
            await Audit(audit, ctx, "ai.login.cancel");
            return Results.NoContent();
        });

        app.MapPost("/api/ai/providers/codex/logout", async (HttpContext ctx, AiRegistry registry, SecurityAuditStore audit) =>
        {
            await registry.Codex.LogoutAsync(ctx.RequestAborted);
            await Audit(audit, ctx, "ai.logout");
            return Results.NoContent();
        });

        // ---- Apps ----
        (AppRequest? Body, string[]? Allowed, IResult? Fail) CheckApp(AppRequest body, AiRegistry registry)
        {
            if (AiRules.ValidateName(body.Name) is { } nameProblem) return (null, null, Error(400, nameProblem));
            var (allowed, allowedProblem) = AiRules.ValidateAllowed(body.Allowed, registry.Get(AiRules.FakeId) is not null);
            if (allowed is null) return (null, null, Error(400, allowedProblem!));
            var rate = body.RatePerMinute ?? 30;
            return AiRules.ValidateLimits(body.DailyTokens, body.MonthlyTokens, rate) is { } limitProblem ? (null, null, Error(400, limitProblem)) : (body, allowed, null);
        }

        app.MapPost("/api/ai/apps", async (AppRequest body, HttpContext ctx, AiAppStore apps, AiUsageStore usage, AiRegistry registry, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            var (b, allowed, fail) = CheckApp(body, registry);
            if (fail is not null) return fail;
            var (created, key) = await apps.CreateAsync(b!.Name!, allowed!, b.DailyTokens, b.MonthlyTokens, b.RatePerMinute ?? 30, b.AllowFiles == true, ctx.RequestAborted);
            if (created is null) return Error(409, "Eine App mit diesem Namen gibt es schon.", "name_taken");
            await Audit(audit, ctx, "ai.app.create", created.Name);
            return Results.Json(new AppCreatedDto(await ToAppDto(created, usage, ctx.RequestAborted), key!), AiJson.Default.AppCreatedDto, statusCode: 201);
        });

        app.MapPut("/api/ai/apps/{id:guid}", async (Guid id, AppRequest body, HttpContext ctx, AiAppStore apps, AiRegistry registry, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            var (b, allowed, fail) = CheckApp(body, registry);
            if (fail is not null) return fail;
            var (found, conflict) = await apps.UpdateAsync(id, b!.Name!, allowed!, b.DailyTokens, b.MonthlyTokens, b.RatePerMinute ?? 30, b.AllowFiles == true, ctx.RequestAborted);
            if (conflict) return Error(409, "Eine App mit diesem Namen gibt es schon.", "name_taken");
            if (!found) return Error(404, "App nicht gefunden.", "not_found");
            await Audit(audit, ctx, "ai.app.edit", b.Name!.Trim());
            return Results.NoContent();
        });

        app.MapPut("/api/ai/apps/{id:guid}/active", async (Guid id, ActiveRequest body, HttpContext ctx, AiAppStore apps, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            if (body.Active is not { } active) return Error(400, "active fehlt.");
            if (!await apps.SetActiveAsync(id, active, ctx.RequestAborted)) return Error(404, "App nicht gefunden.", "not_found");
            await Audit(audit, ctx, active ? "ai.app.unblock" : "ai.app.block", (await apps.GetAsync(id, ctx.RequestAborted))?.Name);
            return Results.NoContent();
        });

        app.MapPost("/api/ai/apps/{id:guid}/key", async (Guid id, HttpContext ctx, AiAppStore apps, SecurityAuditStore audit, HitCounter hits) =>
        {
            if (Limited(ctx, hits)) return TooMany();
            var key = await apps.RotateKeyAsync(id, ctx.RequestAborted);
            if (key is null) return Error(404, "App nicht gefunden.", "not_found");
            await Audit(audit, ctx, "ai.app.key", (await apps.GetAsync(id, ctx.RequestAborted))?.Name);
            return Results.Json(new KeyDto(key), AiJson.Default.KeyDto);
        });

        app.MapDelete("/api/ai/apps/{id:guid}", async (Guid id, HttpContext ctx, AiAppStore apps, SecurityAuditStore audit) =>
        {
            var name = (await apps.GetAsync(id, ctx.RequestAborted))?.Name;
            if (!await apps.DeleteAsync(id, ctx.RequestAborted)) return Error(404, "App nicht gefunden.", "not_found");
            await Audit(audit, ctx, "ai.app.delete", name);
            return Results.NoContent();
        });
    }
}
