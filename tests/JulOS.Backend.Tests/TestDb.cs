using JulOS.Backend.Data;
using Npgsql;

namespace JulOS.Backend.Tests;

/// <summary>Eigenes Schema pro Test in der Datenbank der Test-Instanz (JULOS_TEST_DB, siehe deploy/dev). Ohne sie werden Datenbank-Tests uebersprungen.</summary>
internal sealed class TestDb : IAsyncDisposable
{
    public static string? Admin => Environment.GetEnvironmentVariable("JULOS_TEST_DB");

    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N");
    public NpgsqlDataSource Data { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public static async Task<TestDb> CreateAsync()
    {
        Assert.SkipWhen(Admin is null, "JULOS_TEST_DB nicht gesetzt (deploy/dev/test.sh benutzen)");
        var t = new TestDb();
        await using (var admin = NpgsqlDataSource.Create(Admin!))
        await using (var cmd = admin.CreateCommand($"create schema {t._schema}"))
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        t.ConnectionString = new NpgsqlConnectionStringBuilder(Admin!) { SearchPath = t._schema }.ConnectionString;
        t.Data = NpgsqlDataSource.Create(t.ConnectionString);
        await new Migrator(t.Data).RunAsync(TestContext.Current.CancellationToken);
        return t;
    }

    public async Task ExecAsync(string sql, params object?[] args)
    {
        await using var cmd = Data.Cmd(sql, args);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql, params object?[] args)
    {
        await using var cmd = Data.Cmd(sql, args);
        return (T)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        await Data.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Admin!);
        await using var cmd = admin.CreateCommand($"drop schema {_schema} cascade");
        await cmd.ExecuteNonQueryAsync();
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
