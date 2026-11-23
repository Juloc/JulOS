using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Services;

/// <summary>
/// Die oeffentliche Adresse dieser Instanz, die als Aussteller (iss) in jedem Token steht. Sie kommt aus der Einstellung der App (Einstellungen, Sicherheit) oder, als Rueckfall,
/// aus JULOS_PUBLIC_URL; NIE aus dem Host-Kopf einer Anfrage (den kann jeder Absender setzen, ein gefaelschter Aussteller waere die Folge). Ist nichts gesetzt, ist die
/// Anmelde-Zentrale aus.
/// </summary>
public sealed class OidcIssuer(InstanceSettings settings, Func<string, string?> env)
{
    public const string Key = "oidc.issuer";

    /// <summary>Die gueltige Adresse oder null (nicht gesetzt oder ungueltig).</summary>
    public string? Value => FromSetting() ?? FromEnv();

    public string Source => FromSetting() is not null ? "app" : FromEnv() is not null ? "env" : "default";

    private string? FromSetting() =>
        settings.GetElement(Key) is { ValueKind: JsonValueKind.String } e && OidcRules.CheckIssuer(e.GetString()) is { Issuer: { } v } ? v : null;

    private string? FromEnv() => OidcRules.CheckIssuer(env("JULOS_PUBLIC_URL")) is { Issuer: { } v } ? v : null;

    public Task SetAsync(string issuer, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) w.WriteStringValue(issuer);       // ohne Reflection-JSON
        return settings.SetAsync(Key, System.Text.Encoding.UTF8.GetString(ms.ToArray()), ct);
    }

    public Task<bool> ResetAsync(CancellationToken ct = default) => settings.DeleteAsync(Key, ct);

    public string Url(string path) => (Value ?? throw new InvalidOperationException("Öffentliche Adresse nicht gesetzt.")) + path;
}
