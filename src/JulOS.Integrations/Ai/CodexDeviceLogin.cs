using System.ComponentModel;
using System.Diagnostics;

namespace JulOS.Integrations.Ai;

/// <summary>Stand einer Geraete-Anmeldung: starting, waiting (Adresse und Code stehen), succeeded, failed, cancelled, expired.</summary>
public sealed record CodexLoginState(string State, string? Url, string? Code, DateTimeOffset? ExpiresAt);

/// <summary>
/// Ein laufendes `codex login --device-auth`: JulOS zeigt Adresse und Code, die Person meldet sich im eigenen Browser an, JulOS sieht nur, wie der Prozess endet. Kein Passwort
/// geht durch JulOS. Die Ausgabe wird von Farbcodes befreit und nur Adresse und Code werden daraus genommen.
/// </summary>
internal sealed class CodexDeviceLogin : IDisposable
{
    /// <summary>Der Code des Programms gilt 15 Minuten.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(16);

    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;
    private volatile string _state = "starting";
    private volatile string? _url, _code;
    private int _cancelled;

    private CodexDeviceLogin(Process process, TimeProvider time, TimeSpan lifetime)
    {
        _process = process; _time = time; _started = time.GetUtcNow();
        _lifetime = new CancellationTokenSource(lifetime);
    }

    /// <summary>Endet mit true, wenn die Person sich angemeldet hat.</summary>
    public Task<bool> Completed { get; private set; } = Task.FromResult(false);

    public CodexLoginState Snapshot() => new(_state, _state == "waiting" ? _url : null, _state == "waiting" ? _code : null, _state == "waiting" ? _started + Lifetime : null);

    public static CodexDeviceLogin Start(ProcessStartInfo start, TimeProvider time, TimeSpan? lifetime = null)
    {
        Process process;
        try { process = Process.Start(start) ?? throw new Win32Exception(); }
        catch (Win32Exception) { throw new AiProviderException("provider_unavailable", "Das Codex-Programm lässt sich nicht starten."); }
        var login = new CodexDeviceLogin(process, time, lifetime ?? Lifetime);
        process.StandardInput.Close();
        var reading = Task.WhenAll(login.ReadAsync(process.StandardOutput), login.ReadAsync(process.StandardError));
        login.Completed = login.WatchAsync(reading);
        return login;
    }

    public void Cancel() { Interlocked.Exchange(ref _cancelled, 1); Stop(); }

    private void Stop()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
    }

    private async Task ReadAsync(StreamReader reader)
    {
        var lines = new LineReader(reader, 64 * 1024);
        try
        {
            while (await lines.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (_url is null) { if (CodexProtocol.LoginAddress(line) is { } u) _url = u.AbsoluteUri; }
                else if (_code is null && CodexProtocol.LoginCode(line) is { } code) { _code = code; _state = "waiting"; }
            }
        }
        catch (AiProviderException) { Stop(); }       // eine absurd lange Zeile: das Programm wird beendet, die Anmeldung gilt als fehlgeschlagen
    }

    private async Task<bool> WatchAsync(Task reading)
    {
        try
        {
            await _process.WaitForExitAsync(_lifetime.Token);
            await reading;
            var ok = _process.ExitCode == 0 && Volatile.Read(ref _cancelled) == 0;
            _state = ok ? "succeeded" : Volatile.Read(ref _cancelled) == 1 ? "cancelled" : "failed";
            return ok;
        }
        catch (OperationCanceledException) { Stop(); _state = Volatile.Read(ref _cancelled) == 1 ? "cancelled" : "expired"; return false; }
        catch (InvalidOperationException) { _state = "failed"; return false; }
    }

    public void Dispose() { Stop(); _lifetime.Dispose(); _process.Dispose(); }
}
