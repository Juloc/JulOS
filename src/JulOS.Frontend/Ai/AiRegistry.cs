using System.Collections.Concurrent;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Integrations.Ai;

namespace JulOS.Frontend.Ai;

/// <summary>
/// Die Anbieter dieses Servers und ob sie gerade gelten. Gelten heisst: KI ist eingeschaltet (aus ist der Anfang) UND der Anbieter ist eingeschaltet. Mit aus laeuft nichts und es wird
/// nichts geladen. Die Modelllisten werden kurz gemerkt, damit nicht jede Anfrage den Anbieter fragt.
/// </summary>
internal sealed class AiRegistry(IReadOnlyList<IAiProvider> providers, CodexBridge codex, CodexInstaller installer, AiProviderStore store, AiSettings settings, TimeProvider time)
{
    private static readonly TimeSpan ModelTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<AiModel> Models)> _models = new();

    public IReadOnlyList<IAiProvider> All => providers;
    public CodexBridge Codex => codex;
    public CodexInstaller Installer => installer;

    public IAiProvider? Get(string id) => providers.FirstOrDefault(p => p.Id == id);

    /// <summary>Ob die Codex-Bruecke fuer Apps freigegeben ist: der Besitzer hat die Pruefung von Hand fuer die jetzt festgelegte Version bestaetigt.</summary>
    public bool CodexConfirmed => settings.CodexConfirmedVersion == CodexRelease.PinnedVersion;

    /// <summary>
    /// Die Anbieter, die gerade benutzt werden duerfen (Schalter an). forApps: fuer Apps mit Schluessel; die Codex-Bruecke zaehlt dafuer nur mit der Bestaetigung des Besitzers
    /// (aus per Vorgabe). Ohne forApps (Einstellungen: ausprobieren, anmelden) gilt sie, sobald sie eingeschaltet ist.
    /// </summary>
    public async Task<IReadOnlyList<IAiProvider>> ActiveAsync(CancellationToken ct, bool forApps = false)
    {
        if (!settings.Enabled) return [];
        var flags = await store.ListAsync(ct);
        return [.. providers.Where(p => p.Id == AiRules.FakeId || flags.TryGetValue(p.Id, out var f) && f.Enabled && (!forApps || p.Id != "codex" || CodexConfirmed))];
    }

    public async Task<IReadOnlyList<AiModel>> ModelsAsync(IAiProvider provider, CancellationToken ct)
    {
        if (_models.TryGetValue(provider.Id, out var c) && time.GetUtcNow() - c.At < ModelTtl) return c.Models;
        var models = await provider.ListModelsAsync(ct);
        _models[provider.Id] = (time.GetUtcNow(), models);
        return models;
    }

    public void ForgetModels(string? id = null) { if (id is null) _models.Clear(); else _models.TryRemove(id, out _); }
}

internal static class AiServices
{
    /// <summary>Alles des KI-Zugangs in den Dienstvorrat: Speicher, Grenzen, Anbieter (der Fake nur mit JULOS_AI_FAKE=on) und das Tor fuer die Apps.</summary>
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        // Der Server laeuft ohne Reflection-JSON: die Anfragen der Einstellungen werden mit dem eigenen Kontext gelesen (die Tests laufen MIT Reflection und merken ein Fehlen nicht: AiGatewayTests)
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Add(AiJson.Default));
        services.AddSingleton<AiAppStore>();
        services.AddSingleton<AiUsageStore>();
        services.AddSingleton<AiProviderStore>();
        services.AddSingleton<AiSettings>();
        services.AddSingleton(sp => new AiAuthLimiter(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new AiConcurrency(AiRules.MaxConcurrentPerApp));
        // Das Codex-Programm liegt im Datenordner und wird erst beim Einschalten geladen; ohne Datenordner gibt es die Bruecke nicht
        services.AddSingleton(_ => new CodexInstaller(configuration["JULOS_DATA"] ?? "", string.IsNullOrWhiteSpace(configuration["JULOS_DATA"]) ? null : CodexRelease.ForThisMachine()));
        services.AddSingleton(sp => new CodexBridge(sp.GetRequiredService<CodexInstaller>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<AiProviderStore>();
            var list = new List<IAiProvider>
            {
                sp.GetRequiredService<CodexBridge>(),
                new OpenAiProvider(ct => store.GetKeyAsync("openai", ct)),
                new AnthropicProvider(ct => store.GetKeyAsync("anthropic", ct)),
            };
            if (configuration["JULOS_AI_FAKE"] == "on") list.Add(new FakeAiProvider());
            return new AiRegistry(list, sp.GetRequiredService<CodexBridge>(), sp.GetRequiredService<CodexInstaller>(), store, sp.GetRequiredService<AiSettings>(), sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<AiGateway>();
        return services;
    }
}
