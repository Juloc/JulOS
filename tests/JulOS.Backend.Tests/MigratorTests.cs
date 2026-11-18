using JulOS.Backend.Data;
using Npgsql;

namespace JulOS.Backend.Tests;

/// <summary>Braucht eine PostgreSQL-Datenbank (Umgebungsvariable JULOS_TEST_DB, siehe deploy/dev). Ohne sie werden die Tests uebersprungen.</summary>
public sealed class MigratorTests : IAsyncLifetime
{
    private readonly string? _admin = Environment.GetEnvironmentVariable("JULOS_TEST_DB");
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource? _db;

    public async ValueTask InitializeAsync()
    {
        if (_admin is null) return;
        await using (var admin = NpgsqlDataSource.Create(_admin))
        await using (var cmd = admin.CreateCommand($"create schema {_schema}"))
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        _db = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_admin) { SearchPath = _schema }.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
        if (_admin is null) return;
        await using var admin = NpgsqlDataSource.Create(_admin);
        await using var cmd = admin.CreateCommand($"drop schema {_schema} cascade");
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Applies_all_scripts_once_and_is_repeatable()
    {
        Assert.SkipWhen(_admin is null, "JULOS_TEST_DB nicht gesetzt");
        var migrator = new Migrator(_db!);

        var first = await migrator.RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains("001_accounts_and_connections", first);

        var second = await migrator.RunAsync(TestContext.Current.CancellationToken);
        Assert.Empty(second);

        await using var cmd = _db!.CreateCommand("select count(*) from information_schema.tables where table_schema = current_schema() and table_name in ('users','recovery_codes','sessions','connections','schema_migrations')");
        Assert.Equal(5L, await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task User_names_are_unique_ignoring_case_and_sessions_follow_their_user()
    {
        Assert.SkipWhen(_admin is null, "JULOS_TEST_DB nicht gesetzt");
        await new Migrator(_db!).RunAsync(TestContext.Current.CancellationToken);

        await Exec("insert into users (name, password_hash, totp_secret) values ('Jul', 'x', 'y')");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => Exec("insert into users (name, password_hash, totp_secret) values ('JUL', 'x', 'y')"));
        Assert.Equal("23505", duplicate.SqlState);

        await Exec("insert into sessions (hash, user_id, created_at, last_seen, rotated_at) select 'h', id, now(), now(), now() from users");
        await Exec("delete from users");
        await using var cmd = _db!.CreateCommand("select count(*) from sessions");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private async Task Exec(string sql)
    {
        await using var cmd = _db!.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
