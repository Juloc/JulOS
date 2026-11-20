using System.Text.Json;
using System.Text.Json.Nodes;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Services;

/// <summary>
/// Was mit dem Schluessel der Passwoerter (<c>secret.key</c>) im Backup geschieht. <c>CopyKey</c>: eine Kopie neben den Auszug (und aufs zweite Ziel). Source: <c>app</c> (Einstellung in der App),
/// <c>env</c> (JULOS_BACKUP_KEY), <c>legacy</c> (nichts gesetzt, aber der Ordner hat schon eine Kopie aus frueheren Versionen: sie bleibt, bis in der App umgestellt wird, damit nichts verloren geht)
/// oder <c>default</c> (nichts gesetzt, keine Kopie). <c>RemoveCopies</c>: nur nach ausdruecklichem Ausschalten in der App werden vorhandene Kopien entfernt.
/// <c>Wrapped</c>: der mit dem Wiederherstellungsschluessel umhuellte Schluessel, der immer neben den Auszug darf (<see cref="RecoveryKey"/>).
/// </summary>
public sealed record BackupKeyPlan(bool CopyKey, string Source, bool RemoveCopies, byte[]? Wrapped);

/// <summary>Zustand des Wiederherstellungsschluessels: none (nie erzeugt), pending (erzeugt, noch nicht als gesichert bestaetigt), confirmed. Replacing: ein neuer wartet, ein bestaetigter gilt weiter.</summary>
public sealed record RecoveryInfo(string State, DateTimeOffset? CreatedAt, DateTimeOffset? ConfirmedAt, bool Replacing);

public sealed class BackupKeys(InstanceSettings settings, BackupOptions options, SecretBox box, TimeProvider clock)
{
    public const string CopyKeyKey = "backup.copyKey", RecoveryKeyName = "backup.recovery", PendingKeyName = "backup.recoveryPending";

    /// <summary>Die reine Entscheidung, auch fuer BackupService ohne App-Einstellungen (Konsole, Tests).</summary>
    public static BackupKeyPlan PlanFor(BackupOptions options, bool? app, byte[]? wrapped)
    {
        if (app is { } a) return new(a, "app", !a, wrapped);
        if (options.CopyKey is { } e) return new(e, "env", false, wrapped);
        var legacy = Directory.Exists(options.Dir) && File.Exists(Path.Combine(options.Dir, BackupService.KeyName));
        return new(legacy, legacy ? "legacy" : "default", false, wrapped);
    }

    public BackupKeyPlan Plan() =>
        PlanFor(options, settings.GetElement(CopyKeyKey) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null, ReadWrapped(RecoveryKeyName));

    public RecoveryInfo Recovery()
    {
        var active = settings.GetElement(RecoveryKeyName);
        var pending = settings.GetElement(PendingKeyName);
        if (pending is { } p) return new("pending", Time(p, "createdAt"), active is { } a ? Time(a, "confirmedAt") : null, active is not null);
        return active is { } c ? new("confirmed", Time(c, "createdAt"), Time(c, "confirmedAt"), false) : new("none", null, null, false);
    }

    private static DateTimeOffset? Time(JsonElement o, string name) => o.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String && t.TryGetDateTimeOffset(out var d) ? d : null;

    private byte[]? ReadWrapped(string key) =>
        settings.GetElement(key) is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("wrapped", out var w) && w.ValueKind == JsonValueKind.String ? Convert.FromBase64String(w.GetString()!) : null;

    public Task SetCopyKeyAsync(bool copy, CancellationToken ct = default) => settings.SetAsync(CopyKeyKey, copy ? "true" : "false", ct);

    public Task ResetCopyKeyAsync(CancellationToken ct = default) => settings.DeleteAsync(CopyKeyKey, ct);

    /// <summary>Erzeugt einen neuen Wiederherstellungsschluessel und gibt ihn <b>einmal</b> als Text zurueck. Gespeichert wird nur die umhuellte Form, und sie gilt erst nach <see cref="ConfirmRecoveryAsync"/>.</summary>
    public async Task<string> NewRecoveryAsync(CancellationToken ct = default)
    {
        var secret = box.ExportKey() ?? throw new InvalidOperationException("Es gibt keinen Schlüssel für Passwörter, also nichts zu sichern.");
        var recovery = RecoveryKey.New();
        var node = new JsonObject { ["wrapped"] = Convert.ToBase64String(RecoveryKey.Wrap(secret, recovery)), ["createdAt"] = clock.GetUtcNow().ToString("O") };
        await settings.SetAsync(PendingKeyName, node.ToJsonString(), ct);
        return RecoveryKey.Format(recovery);
    }

    /// <summary>"Ich habe ihn gesichert": der wartende Schluessel gilt ab jetzt (die naechste Sicherung legt die umhuellte Datei ab). false, wenn keiner wartet.</summary>
    public async Task<bool> ConfirmRecoveryAsync(CancellationToken ct = default)
    {
        if (settings.GetElement(PendingKeyName) is not { ValueKind: JsonValueKind.Object } p) return false;
        var node = JsonNode.Parse(p.GetRawText())!.AsObject();
        node["confirmedAt"] = clock.GetUtcNow().ToString("O");
        await settings.SetAsync(RecoveryKeyName, node.ToJsonString(), ct);
        await settings.DeleteAsync(PendingKeyName, ct);
        return true;
    }
}
