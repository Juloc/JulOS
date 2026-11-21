using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JulOS.Backend.Data;

namespace JulOS.Backend.Logic;

/// <summary>Eine Liste aus den Einstellungen. Source: <c>app</c> (in der App gesetzt), <c>env</c> (aus der Umgebungsvariable) oder <c>default</c> (nichts gesetzt, der eingebaute Wert gilt).</summary>
public sealed record ListSetting(string[] Entries, string Source);

/// <summary>Ein Schalter aus den Einstellungen mit seiner Herkunft (<c>app</c>, <c>env</c> oder <c>default</c>, wie bei <see cref="ListSetting"/>).</summary>
public sealed record FlagSetting(bool Enabled, string Source);

/// <summary>Zweiter Faktor fuer Aktionen: ob verlangt, wie lange eine Freigabe gilt (Minuten; 0 = bei jeder Aktion neu fragen).</summary>
public sealed record StepUpConfig(bool Enabled, int Minutes)
{
    public const int DefaultMinutes = 10, MaxMinutes = 60;

    /// <summary>Bei "immer fragen" gilt eine Freigabe nur fuer die naechste Aktion und hoechstens so lange.</summary>
    public static readonly TimeSpan OnceLifetime = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Die Sicherheits-Einstellungen der Instanz (Tabelle instance_settings): vertraute Proxy-Adressen, erlaubte Zielhosts, zweiter Faktor fuer Aktionen, Bestaetigung bei gefaehrlichen Aktionen,
/// Schreiben in der Dateien-App. Der Wert aus der App gilt vor der Umgebungsvariable (JULOS_TRUSTED_PROXIES, JULOS_ALLOWED_HOSTS, JULOS_FILES_WRITE), die nur der Rueckfall ist; ohne beides gilt der eingebaute Wert.
/// Leser (<see cref="HostPolicy"/>, der Proxy-Filter) fragen bei jeder Anfrage nach, eine Aenderung gilt also ohne Neustart.
/// </summary>
public sealed class SecurityConfig
{
    public const string ProxiesKey = "security.trustedProxies", HostsKey = "security.allowedHosts", StepUpKey = "security.stepUp", ConfirmModeKey = "security.confirmMode", FilesWriteKey = "security.filesWrite";

    /// <summary>Ohne Einstellung glaubt der Server X-Forwarded-* von jedem privaten Netz (wie vor 0.18): bequem, aber schwach, darum die Warnung (F-03).</summary>
    public static readonly string[] DefaultProxies = ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

    private readonly InstanceSettings _settings;
    private readonly string? _envProxies, _envHosts;
    private volatile ListSetting _proxies = new(DefaultProxies, "default");
    private volatile ListSetting _hosts = new([], "default");
    private volatile StepUpConfig _stepUp = new(true, StepUpConfig.DefaultMinutes);
    private volatile ConfirmMode _confirmMode = ConfirmMode.Check;
    private readonly bool _envFilesWrite, _envFilesWriteSet;
    private volatile FlagSetting _filesWrite = new(false, "default");

    public SecurityConfig(InstanceSettings settings, Func<string, string?> env)
    {
        _settings = settings;
        _envProxies = env("JULOS_TRUSTED_PROXIES");
        _envHosts = env("JULOS_ALLOWED_HOSTS");
        _envFilesWrite = FilesOptions.Parse(env("JULOS_FILES_WRITE"), null, null).Write;      // strenger Wert: nur "on" schaltet ein, ein Tippfehler nie
        _envFilesWriteSet = !string.IsNullOrWhiteSpace(env("JULOS_FILES_WRITE"));
        if (!string.IsNullOrWhiteSpace(_envProxies) && AddressList.Validate(Split(_envProxies), names: false) is { Error: { } error })
            throw new InvalidOperationException("JULOS_TRUSTED_PROXIES: " + error);
        Reload();
        settings.Changed += Reload;
    }

    private static string[] Split(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Reload()
    {
        _proxies = Pick(ProxiesKey, _envProxies, DefaultProxies, names: false);
        _hosts = Pick(HostsKey, _envHosts, [], names: true);
        _stepUp = ReadStepUp() ?? new StepUpConfig(true, StepUpConfig.DefaultMinutes);
        _filesWrite = _settings.GetElement(FilesWriteKey) is { ValueKind: JsonValueKind.True or JsonValueKind.False } w ? new FlagSetting(w.GetBoolean(), "app")
            : _envFilesWriteSet ? new FlagSetting(_envFilesWrite, "env") : new FlagSetting(false, "default");
        _confirmMode = _settings.GetElement(ConfirmModeKey) is { ValueKind: JsonValueKind.String } m ? ConfirmPolicy.ParseMode(m.GetString()) : ConfirmMode.Check;
    }

    private StepUpConfig? ReadStepUp() =>
        _settings.GetElement(StepUpKey) is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            && o.TryGetProperty("minutes", out var m) && m.TryGetInt32(out var minutes) && minutes is >= 0 and <= StepUpConfig.MaxMinutes ? new StepUpConfig(e.GetBoolean(), minutes) : null;

    private static string Json(string[] entries) => new JsonArray([.. entries.Select(e => (JsonNode?)JsonValue.Create(e))]).ToJsonString();

    private ListSetting Pick(string key, string? env, string[] fallback, bool names)
    {
        if (_settings.GetStrings(key) is { } app) return new ListSetting(app, "app");
        if (!string.IsNullOrWhiteSpace(env)) return new ListSetting(names ? Split(env) : AddressList.Validate(Split(env), false).Entries!, "env");
        return new ListSetting(fallback, "default");
    }

    public ListSetting Proxies => _proxies;
    public ListSetting Hosts => _hosts;
    public StepUpConfig StepUp => _stepUp;

    /// <summary>Wie gefaehrliche Aktionen bestaetigt werden (Haekchen, Rechnernamen tippen, nur Klick). Steht in der Datenbank, gilt ohne Neustart; ohne Einstellung das Haekchen.</summary>
    public ConfirmMode ConfirmMode => _confirmMode;

    /// <summary>Ob die Dateien-App schreiben darf (hochladen, neu anlegen, umbenennen, verschieben, kopieren, loeschen). Steht in der Datenbank und gilt ohne Neustart; ohne Einstellung gilt JULOS_FILES_WRITE, ohne beides aus.</summary>
    public FlagSetting FilesWrite => _filesWrite;

    /// <summary>Ob eine Verbindung von dieser Adresse als vertrauter Proxy gilt (dann werden seine X-Forwarded-Kopfzeilen geglaubt).</summary>
    public bool IsTrustedProxy(IPAddress? address) => AddressList.ContainsAddress(_proxies.Entries, address);

    /// <summary>Die Proxy-Liste ist weder in der App noch in der Umgebung gesetzt: es gilt der weite Standard (Warnung, F-03).</summary>
    public bool ProxiesUnset => _proxies.Source == "default";

    public Task SetProxiesAsync(string[] entries, CancellationToken ct = default) => _settings.SetAsync(ProxiesKey, Json(entries), ct);
    public Task SetHostsAsync(string[] entries, CancellationToken ct = default) => _settings.SetAsync(HostsKey, Json(entries), ct);
    public Task ResetProxiesAsync(CancellationToken ct = default) => _settings.DeleteAsync(ProxiesKey, ct);
    public Task ResetHostsAsync(CancellationToken ct = default) => _settings.DeleteAsync(HostsKey, ct);

    public Task SetFilesWriteAsync(bool enabled, CancellationToken ct = default) => _settings.SetAsync(FilesWriteKey, enabled ? "true" : "false", ct);
    /// <summary>Die Einstellung der App vergessen: dann gilt JULOS_FILES_WRITE oder aus.</summary>
    public Task ResetFilesWriteAsync(CancellationToken ct = default) => _settings.DeleteAsync(FilesWriteKey, ct);

    /// <returns>null bei gueltigen Werten, sonst die Meldung.</returns>
    public static string? ValidateStepUp(bool enabled, int minutes) =>
        minutes is < 0 or > StepUpConfig.MaxMinutes ? $"Die Dauer muss zwischen 0 (immer fragen) und {StepUpConfig.MaxMinutes} Minuten liegen." : null;

    public Task SetStepUpAsync(bool enabled, int minutes, CancellationToken ct = default) => _settings.SetAsync(StepUpKey, new JsonObject { ["enabled"] = enabled, ["minutes"] = minutes }.ToJsonString(), ct);

    /// <summary>Nur "check", "type" oder "click" werden gespeichert.</summary>
    public Task SetConfirmModeAsync(ConfirmMode mode, CancellationToken ct = default) => _settings.SetAsync(ConfirmModeKey, JsonValue.Create(ConfirmPolicy.Name(mode))!.ToJsonString(), ct);
}
