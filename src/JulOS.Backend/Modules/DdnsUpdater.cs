using System.Net;
using System.Net.Sockets;

namespace JulOS.Backend.Modules;

public sealed record DdnsHistoryEntry(DateTimeOffset At, string Host, string Type, string? OldIp, string NewIp, string Trigger, bool Ok, string Message);

/// <summary>Wo der Verlauf liegt (PostgreSQL, Tabelle ddns_history; in Tests im Speicher).</summary>
public interface IDdnsHistory
{
    Task AddAsync(DdnsHistoryEntry entry, CancellationToken ct);
    Task<IReadOnlyList<DdnsHistoryEntry>> RecentAsync(int count, CancellationToken ct);
    /// <summary>Die Adresse, die zuletzt erfolgreich fuer diesen Namen geschrieben wurde.</summary>
    Task<string?> LastWrittenAsync(string host, string type, CancellationToken ct);
}

public sealed record DdnsChange(string Host, string Type, string? OldIp, string NewIp, bool Ok, string Message);
/// <summary>Outcome: updated, current (nichts zu tun), failed, noIp, privateIp, noProvider, tooSoon.</summary>
public sealed record DdnsUpdateResult(string Outcome, DdnsChange[] Changes, DateTimeOffset At);
/// <summary>Was die Oberflaeche ueber die Schreibstufe wissen darf: nie Zugangsdaten oder Adressen der Anbieter.</summary>
public sealed record DdnsControlInfo(string Mode, string? Provider, bool CanUpdate, bool Auto, int AutoSeconds, DateTimeOffset? LastAt, string? LastOutcome);

/// <summary>
/// DDNS, Schreibstufe (JULOS_DDNS=control): aendert nur die in JULOS_DDNS_HOSTS genannten A- oder AAAA-Eintraege (die Art richtet sich nach der Adresse), nur wenn sie von der oeffentlichen
/// Adresse abweichen, nie mit einer privaten Adresse, hoechstens alle 5 Minuten automatisch (30 s Pause nach Knopfdruck), und legt nie etwas an oder loescht etwas. Jede Aenderung kommt in den Verlauf.
/// Laesst sich der Anbieter lesen, entscheidet sein Inhalt; sonst der Namensdienst und die zuletzt geschriebene Adresse (so wird nicht bei jedem Lauf neu geschrieben, solange ein Zwischenspeicher alt antwortet).
/// Ein Name, bei dem das Schreiben scheiterte, wird automatisch eine Stunde lang nicht erneut versucht.
/// </summary>
public sealed class DdnsUpdater(DdnsOptions options, DdnsMonitor monitor, IDnsProvider? provider, IDdnsHistory history, TimeProvider? time = null)
{
    public static readonly TimeSpan ButtonGap = TimeSpan.FromSeconds(30), AutoGap = TimeSpan.FromMinutes(5), FailureBackoff = TimeSpan.FromHours(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _failedUntil = [];
    private DateTimeOffset? _lastWrite, _lastAt;
    private string? _lastOutcome;

    public bool IsControl => options.Mode == ModuleMode.Control;
    public bool CanUpdate => options.Mode == ModuleMode.Control && provider is not null;
    public bool AutoOn => CanUpdate && options.Auto;
    public TimeSpan AutoEvery => options.AutoEvery < AutoGap ? AutoGap : options.AutoEvery;

    public DdnsControlInfo Info() => new(ModuleModes.Name(options.Mode), provider?.Name, CanUpdate, AutoOn, (int)AutoEvery.TotalSeconds, _lastAt, _lastOutcome);

    public async Task<DdnsUpdateResult> UpdateAsync(string trigger, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!CanUpdate) return new DdnsUpdateResult("noProvider", [], now);
        if (!await _gate.WaitAsync(0, ct)) return new DdnsUpdateResult("tooSoon", [], now);
        try
        {
            var gap = trigger == "auto" ? AutoEvery : ButtonGap;
            if (_lastWrite is { } w && now - w < gap) return Done("tooSoon", []);

            var status = await monitor.GetAsync(ct, force: true);
            if (status.PublicIp is null || !IPAddress.TryParse(status.PublicIp, out var ip)) return Done("noIp", []);
            if (DdnsMonitor.IsPrivate(ip)) return Done("privateIp", []);
            var type = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A";

            var records = await provider!.ReadAsync(options.Hosts, type, ct);
            var changes = new List<DdnsChange>();
            foreach (var host in options.Hosts)
            {
                if (trigger == "auto" && _failedUntil.TryGetValue(host, out var until) && until > now) continue;
                string? old;
                bool needs;
                if (records is not null)
                {
                    var contents = records.Where(r => r.Host == host).Select(r => r.Content).ToArray();
                    old = contents.FirstOrDefault();
                    needs = !contents.Any(c => Same(c, ip));
                }
                else
                {
                    var addresses = (status.Hosts.FirstOrDefault(h => h.Host == host)?.Addresses ?? []).Where(a => IPAddress.TryParse(a, out var x) && x.AddressFamily == ip.AddressFamily).ToArray();
                    var last = await history.LastWrittenAsync(host, type, ct);
                    old = addresses.FirstOrDefault() ?? last;
                    needs = !addresses.Any(a => Same(a, ip)) && !(last is not null && Same(last, ip));
                }
                if (!needs) continue;

                DnsWriteResult result;
                try { result = await provider.UpdateAsync(host, ip, ct); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { result = new DnsWriteResult(false, "unreachable"); }
                _lastWrite = _time.GetUtcNow();
                if (!result.Ok) _failedUntil[host] = _lastWrite.Value + FailureBackoff; else _failedUntil.Remove(host);
                changes.Add(new DdnsChange(host, type, old, ip.ToString(), result.Ok, result.Message));
                await history.AddAsync(new DdnsHistoryEntry(_lastWrite.Value, host, type, old, ip.ToString(), trigger, result.Ok, result.Message), ct);
            }
            if (changes.Any(c => c.Ok)) monitor.Forget();      // die naechste Anzeige misst neu
            return Done(changes.Count == 0 ? "current" : changes.Any(c => c.Ok) ? "updated" : "failed", [.. changes]);
        }
        finally { _gate.Release(); }

        DdnsUpdateResult Done(string outcome, DdnsChange[] c) { _lastAt = _time.GetUtcNow(); _lastOutcome = outcome; return new DdnsUpdateResult(outcome, c, _lastAt.Value); }
    }

    private static bool Same(string text, IPAddress ip) => IPAddress.TryParse(text, out var x) && x.Equals(ip);
}
