using System.Text.Json.Serialization;

namespace JulOS.Backend.Logic;

/// <summary>Schalter und Schwellen der Regeln eines Benutzers (docs/PUSH.md). Zone und ZoneOffset sagen, wo "22 bis 7 Uhr" gilt (der Browser nennt beides).</summary>
public sealed record AlertSettings(
    bool Security = true, bool Disk = true, bool Service = true, bool Offline = true, bool Reboot = true,
    int DiskPercent = 90, int OfflineMinutes = 10,
    bool RecoverOffline = true, bool RecoverDisk = false, bool RecoverService = false,
    bool Quiet = false, int QuietFrom = 22, int QuietTo = 7, string? Zone = null, int ZoneOffset = 0)
{
    /// <summary>Bringt Werte aus dem Browser in erlaubte Grenzen (der Server vertraut keiner Eingabe).</summary>
    public AlertSettings Clamped() => this with
    {
        DiskPercent = Math.Clamp(DiskPercent, 50, 99),
        OfflineMinutes = Math.Clamp(OfflineMinutes, 5, 1440),
        QuietFrom = Math.Clamp(QuietFrom, 0, 23),
        QuietTo = Math.Clamp(QuietTo, 0, 23),
        ZoneOffset = Math.Clamp(ZoneOffset, -14 * 60, 14 * 60),
        Zone = Zone is { Length: > 0 and <= 64 } z && z.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+') ? z : null,
    };
}

/// <summary>Was die Auswertung von einem Host weiss. Null heisst "nicht bekannt" (der Agent kann es nicht oder hat nicht geantwortet): dann bleibt der alte Stand dieser Regel.</summary>
public sealed record DiskFill(string Mount, double Percent);
public sealed record HostObservation(Guid HostId, string Name, bool Online, int? Security = null, bool? Reboot = null, DiskFill[]? Disks = null,
    string[]? Failed = null, string[]? Running = null, string[]? Stopped = null);

/// <summary>Der Zustand je Host, den die Auswertung zwischen zwei Abfragen merkt. Daran liegt es, dass nur der Wechsel gemeldet wird und nicht jede Abfrage.</summary>
public sealed record HostAlertState
{
    public DateTimeOffset? OfflineSince { get; init; }
    public bool OfflineSent { get; init; }
    public int? Security { get; init; }
    public bool Reboot { get; init; }
    public string[] DiskHigh { get; init; } = [];
    public string[] Failed { get; init; } = [];
    public string[] Running { get; init; } = [];
    public string[] Down { get; init; } = [];
    public Dictionary<string, DateTimeOffset> Sent { get; init; } = [];
}

/// <summary>Rule: security, disk, service, offline, reboot. Kind: alert oder ok ("wieder in Ordnung"). Detail: kurzer Text fuer die Liste in der App (nie in der Push-Nachricht).</summary>
public sealed record AlertEvent(string Rule, string Kind, string? Detail);

/// <summary>
/// Die festen Regeln ohne KI, als reine Funktionen: Eingabe der Stand eines Hosts und der letzte Zustand, Ausgabe die Meldungen und der neue Zustand.
/// Gemeldet wird beim Wechsel in den Zustand. Je Host und Regel hoechstens eine Meldung je <see cref="Cooldown"/>: was in der Pause neu auftritt, wird nicht verworfen,
/// sondern bleibt "neu" und kommt mit der naechsten Abfrage nach der Pause. Abgeschaltete Regeln laufen im Zustand mit (ohne Meldung), damit beim Einschalten nicht alles Alte auf einmal kommt.
/// </summary>
public static class AlertRules
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);
    public const int DiskHysteresis = 2;        // eine Platte gilt erst als wieder in Ordnung, wenn sie 2 Punkte unter der Schwelle liegt (kein Flattern an der Grenze)
    public static readonly string[] Rules = ["security", "disk", "service", "offline", "reboot"];

    public static (List<AlertEvent> Events, HostAlertState State) Evaluate(HostObservation o, HostAlertState s, AlertSettings cfg, DateTimeOffset now)
    {
        var events = new List<AlertEvent>();
        var sent = new Dictionary<string, DateTimeOffset>(s.Sent.Where(kv => now - kv.Value < Cooldown * 4));

        // Meldet nur, wenn die Regel an ist; false: in der Pause, der Zustand darf nicht vorruecken
        bool Emit(string rule, bool enabled, bool recovery, string? detail)
        {
            if (!enabled) return true;
            var key = recovery ? rule + ":ok" : rule;
            if (sent.TryGetValue(key, out var last) && now - last < Cooldown) return false;
            sent[key] = now;
            events.Add(new AlertEvent(rule, recovery ? "ok" : "alert", detail));
            return true;
        }

        var state = s with { };

        // Agent offline
        if (o.Online)
        {
            if (s.OfflineSent)
            {
                if (Emit("offline", cfg.Offline && cfg.RecoverOffline, true, null)) state = state with { OfflineSince = null, OfflineSent = false };
            }
            else state = state with { OfflineSince = null };
        }
        else
        {
            var since = s.OfflineSince ?? now;
            state = state with { OfflineSince = since };
            if (!s.OfflineSent && now - since >= TimeSpan.FromMinutes(cfg.OfflineMinutes) && Emit("offline", cfg.Offline, false, null)) state = state with { OfflineSent = true };
            return (events, state with { Sent = sent });        // von einem Host ohne Verbindung weiss die Auswertung sonst nichts Neues
        }

        // Neustart noetig
        if (o.Reboot is { } reboot)
        {
            if (!reboot) state = state with { Reboot = false };
            else if (!s.Reboot && Emit("reboot", cfg.Reboot, false, null)) state = state with { Reboot = true };
        }

        // Neue Sicherheitsupdates: die erste Messung ist nur der Ausgangswert
        if (o.Security is { } sec)
        {
            if (s.Security is not { } known) state = state with { Security = sec };
            else if (sec > known) { if (Emit("security", cfg.Security, false, $"{sec - known} / {sec}")) state = state with { Security = sec }; }
            else if (sec < known) state = state with { Security = sec };
        }

        // Platte fast voll, je Einhängepunkt
        if (o.Disks is { } disks)
        {
            var high = new HashSet<string>(s.DiskHigh);
            var over = disks.Where(d => d.Percent >= cfg.DiskPercent).ToList();
            var fresh = over.Where(d => !high.Contains(d.Mount)).ToList();
            var ok = s.DiskHigh.Where(m => !disks.Any(d => d.Mount == m && d.Percent > cfg.DiskPercent - DiskHysteresis)).ToList();     // weg oder wieder unter der Schwelle
            if (fresh.Count > 0 && Emit("disk", cfg.Disk, false, string.Join(", ", fresh.Select(d => $"{d.Mount} {Math.Round(d.Percent)} %")))) foreach (var d in fresh) high.Add(d.Mount);
            if (ok.Count > 0 && Emit("disk", cfg.Disk && cfg.RecoverDisk, true, string.Join(", ", ok))) foreach (var m in ok) high.Remove(m);
            state = state with { DiskHigh = [.. high] };
        }

        // Dienst oder Container ausgefallen: neuer fehlgeschlagener Dienst, Container gestoppt, der vorher lief
        var failed = new HashSet<string>(state.Failed);
        var down = new HashSet<string>(state.Down);
        var newItems = new List<string>();
        var goneItems = new List<string>();
        if (o.Failed is { } nowFailed)
        {
            foreach (var u in nowFailed) if (!failed.Contains(u)) newItems.Add(u);
            goneItems.AddRange(failed.Where(u => !nowFailed.Contains(u)));
        }
        if (o.Stopped is { } stopped && o.Running is { } running)
        {
            foreach (var c in stopped) if (s.Running.Contains(c) && !down.Contains(c)) newItems.Add(c);
            goneItems.AddRange(down.Where(c => running.Contains(c)));
        }
        if (newItems.Count > 0 && Emit("service", cfg.Service, false, string.Join(", ", newItems.Take(5)) + (newItems.Count > 5 ? $" +{newItems.Count - 5}" : "")))
        {
            if (o.Failed is { } f) foreach (var u in f) failed.Add(u);
            if (o.Stopped is { } st) foreach (var c in st) if (s.Running.Contains(c)) down.Add(c);
        }
        if (goneItems.Count > 0 && (!(cfg.Service && cfg.RecoverService) || Emit("service", true, true, string.Join(", ", goneItems.Take(5)))))
        {
            if (o.Failed is { } f) failed.IntersectWith(f);
            if (o.Running is { } r) down.ExceptWith(r);
        }
        state = state with { Failed = [.. failed.Order()], Down = [.. down.Order()] };
        if (o.Running is { } run && o.Stopped is { } stop)
            state = state with { Running = [.. run.Concat(s.Running.Where(stop.Contains)).Distinct().Order().Take(200)] };    // laufende Container, dazu die gestoppten, die vorher liefen

        return (events, state with { Sent = sent });
    }

    /// <summary>Ob jetzt Ruhezeit ist (zum Beispiel 22 bis 7 Uhr, auch ueber Mitternacht). Von gleich bis gleich ist keine Ruhezeit.</summary>
    public static bool InQuietHours(AlertSettings cfg, DateTimeOffset now)
    {
        if (!cfg.Quiet || cfg.QuietFrom == cfg.QuietTo) return false;
        var hour = Local(cfg, now).Hour;
        return cfg.QuietFrom < cfg.QuietTo ? hour >= cfg.QuietFrom && hour < cfg.QuietTo : hour >= cfg.QuietFrom || hour < cfg.QuietTo;
    }

    private static DateTimeOffset Local(AlertSettings cfg, DateTimeOffset now)
    {
        if (cfg.Zone is { } zone)
        {
            try { return TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zone)); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { /* Zone auf diesem Server unbekannt: der gemerkte Versatz gilt */ }
        }
        return now.ToOffset(TimeSpan.FromMinutes(cfg.ZoneOffset));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AlertSettings))]
[JsonSerializable(typeof(HostAlertState))]
public sealed partial class AlertJson : JsonSerializerContext;
