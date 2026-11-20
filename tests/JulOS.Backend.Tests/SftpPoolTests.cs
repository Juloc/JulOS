using JulOS.Frontend.Files;
using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;

namespace JulOS.Backend.Tests;

/// <summary>Der Pool der SFTP-Sitzungen: gemeinsam benutzt, kurzlebig, begrenzt, ohne Leck.</summary>
public sealed class SftpPoolTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan t) => _now += t;
    }

    private static readonly SftpConnectOptions Options = new("h", 22, "u", "p", null, null);
    private static readonly Guid U1 = Guid.NewGuid(), U2 = Guid.NewGuid();

    /// <summary>Eine Werkbank: jede Verbindung erzeugt eine neue Fake-Sitzung und merkt sie sich.</summary>
    private sealed class Bench
    {
        public readonly List<FakeFileSession> Sessions = [];
        public Exception? FailWith;
        public TaskCompletionSource? Gate;
        public Func<CancellationToken, Task<ISftpFileSession>> Connect => async _ =>
        {
            var s = new FakeFileSession { Connected = false };
            Sessions.Add(s);
            if (Gate is not null) await Gate.Task;
            if (FailWith is not null) { await s.DisposeAsync(); throw FailWith; }
            await s.ConnectAsync(Options, _ => true, CancellationToken.None);
            return s;
        };
    }

    private static (SftpPool Pool, ManualTime Time) Make(int max = 4) { var t = new ManualTime(); return (new SftpPool(t, max, TimeSpan.FromSeconds(60)), t); }

    [Fact]
    public async Task One_session_per_user_and_connection_is_shared_and_opened_once()
    {
        var (pool, _) = Make(); var b = new Bench(); var c = Guid.NewGuid();
        Assert.False(pool.IsOpen(U1, c));
        Assert.Null(pool.TryAcquire(U1, c));
        await using var a = await pool.AcquireAsync(U1, c, b.Connect, default);
        await using var second = await pool.AcquireAsync(U1, c, b.Connect, default);
        await using var third = pool.TryAcquire(U1, c)!;
        Assert.Same(a.Session, second.Session);
        Assert.Same(a.Session, third.Session);
        Assert.Single(b.Sessions);
        Assert.True(pool.IsOpen(U1, c));
        await using var other = await pool.AcquireAsync(U2, c, b.Connect, default);        // ein anderer Benutzer teilt nie
        Assert.NotSame(a.Session, other.Session);
        Assert.Equal(2, b.Sessions.Count);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_connect()
    {
        var (pool, _) = Make(); var b = new Bench { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var c = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 6).Select(_ => pool.AcquireAsync(U1, c, b.Connect, default)).ToArray();
        await Task.Delay(50);
        b.Gate.SetResult();
        var leases = await Task.WhenAll(tasks);
        Assert.Single(b.Sessions);
        Assert.All(leases, l => Assert.Same(leases[0].Session, l.Session));
        foreach (var l in leases) await l.DisposeAsync();
        Assert.Equal(1, pool.CountFor(U1));
    }

    [Fact]
    public async Task A_failed_connect_leaves_nothing_behind_and_the_next_try_connects_again()
    {
        var (pool, _) = Make(); var b = new Bench { FailWith = new SshException(SshFailure.LogonFailed, "Anmeldung abgelehnt.") }; var c = Guid.NewGuid();
        await Assert.ThrowsAsync<SshException>(() => pool.AcquireAsync(U1, c, b.Connect, default));
        Assert.Equal(0, pool.CountFor(U1));
        Assert.Equal(1, b.Sessions[0].Disposed);
        b.FailWith = null;
        await using var ok = await pool.AcquireAsync(U1, c, b.Connect, default);
        Assert.Equal(2, b.Sessions.Count);
        Assert.Equal(1, pool.CountFor(U1));
    }

    [Fact]
    public async Task Idle_sessions_close_after_sixty_seconds_but_never_while_leased()
    {
        var (pool, time) = Make(); var b = new Bench(); var c1 = Guid.NewGuid(); var c2 = Guid.NewGuid();
        var busy = await pool.AcquireAsync(U1, c1, b.Connect, default);
        await (await pool.AcquireAsync(U1, c2, b.Connect, default)).DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await pool.SweepAsync());
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await pool.SweepAsync());                                       // nur die ohne Leihe
        Assert.Equal(1, b.Sessions[1].Disposed);
        Assert.Equal(0, b.Sessions[0].Disposed);
        Assert.True(pool.IsOpen(U1, c1));
        await busy.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await pool.SweepAsync());
        Assert.Equal((0, 1), (pool.Count, b.Sessions[0].Disposed));
    }

    [Fact]
    public async Task Using_a_session_restarts_its_idle_time()
    {
        var (pool, time) = Make(); var b = new Bench(); var c = Guid.NewGuid();
        await (await pool.AcquireAsync(U1, c, b.Connect, default)).DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(50));
        await (await pool.AcquireAsync(U1, c, b.Connect, default)).DisposeAsync();      // zum Beispiel das Offenhalten der Seite
        time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(0, await pool.SweepAsync());
        Assert.Single(b.Sessions);
    }

    [Fact]
    public async Task The_limit_per_user_closes_the_oldest_idle_session_or_refuses_when_all_are_in_use()
    {
        var (pool, time) = Make(max: 2); var b = new Bench();
        var c1 = Guid.NewGuid(); var c2 = Guid.NewGuid(); var c3 = Guid.NewGuid();
        await (await pool.AcquireAsync(U1, c1, b.Connect, default)).DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        var l2 = await pool.AcquireAsync(U1, c2, b.Connect, default);
        var l3 = await pool.AcquireAsync(U1, c3, b.Connect, default);                    // c1 war unbenutzt und aelter: es muss weichen
        Assert.Equal(1, b.Sessions[0].Disposed);
        Assert.Equal(2, pool.CountFor(U1));
        await Assert.ThrowsAsync<PoolFullException>(() => pool.AcquireAsync(U1, c1, b.Connect, default));
        await using var other = await pool.AcquireAsync(U2, c1, b.Connect, default);     // die Grenze gilt je Benutzer
        await l2.DisposeAsync(); await l3.DisposeAsync();
        Assert.Equal(2, pool.CountFor(U1));
    }

    [Fact]
    public async Task A_dropped_session_is_replaced_and_never_handed_out_again()
    {
        var (pool, _) = Make(); var b = new Bench(); var c = Guid.NewGuid();
        var first = await pool.AcquireAsync(U1, c, b.Connect, default);
        b.Sessions[0].Drop();
        Assert.False(pool.IsOpen(U1, c));
        Assert.Null(pool.TryAcquire(U1, c));
        await first.DisposeAsync();                                                       // beim Zurueckgeben: abgerissen, also weg
        Assert.Equal(1, b.Sessions[0].Disposed);
        Assert.Equal(0, pool.CountFor(U1));
        await using var again = await pool.AcquireAsync(U1, c, b.Connect, default);
        Assert.Equal(2, b.Sessions.Count);
        Assert.NotSame(first.Session, again.Session);
    }

    [Fact]
    public async Task Sweep_also_removes_dropped_sessions_without_waiting_for_the_idle_time()
    {
        var (pool, _) = Make(); var b = new Bench(); var c = Guid.NewGuid();
        await (await pool.AcquireAsync(U1, c, b.Connect, default)).DisposeAsync();
        b.Sessions[0].Drop();
        Assert.Equal(1, await pool.SweepAsync());
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public async Task Close_ends_an_unused_session_and_waits_for_a_leased_one()
    {
        var (pool, _) = Make(); var b = new Bench(); var c = Guid.NewGuid();
        var lease = await pool.AcquireAsync(U1, c, b.Connect, default);
        await pool.CloseAsync(U1, c);
        Assert.True(pool.IsOpen(U1, c));                                                  // in Gebrauch: bleibt
        await lease.DisposeAsync();
        await pool.CloseAsync(U1, c);
        Assert.False(pool.IsOpen(U1, c));
        Assert.Equal(1, b.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Returning_a_lease_twice_counts_once_and_disposing_the_pool_closes_everything()
    {
        var (pool, _) = Make(); var b = new Bench(); var c = Guid.NewGuid();
        var lease = await pool.AcquireAsync(U1, c, b.Connect, default);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Assert.Equal(0, await pool.SweepAsync());                                         // nicht negativ gezaehlt, aber auch nicht zu frueh weg
        await using var again = await pool.AcquireAsync(U2, c, b.Connect, default);
        await pool.DisposeAsync();
        Assert.All(b.Sessions, s => Assert.Equal(1, s.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync(U1, c, b.Connect, default));
    }

    [Fact]
    public async Task A_caller_that_gives_up_while_connecting_does_not_cancel_the_connect_for_the_others()
    {
        var (pool, _) = Make(); var b = new Bench { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var c = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var impatient = pool.AcquireAsync(U1, c, b.Connect, cts.Token);
        var patient = pool.AcquireAsync(U1, c, b.Connect, default);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        b.Gate.SetResult();
        await using var ok = await patient;
        Assert.True(pool.IsOpen(U1, c));
        Assert.Single(b.Sessions);
    }
}
