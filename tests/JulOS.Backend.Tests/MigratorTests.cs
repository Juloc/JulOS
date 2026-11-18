namespace JulOS.Backend.Tests;

public sealed class MigratorTests
{
    [Fact]
    public async Task Applies_all_scripts_once_and_is_repeatable()
    {
        await using var db = await TestDb.CreateAsync();
        var second = await new JulOS.Backend.Data.Migrator(db.Data).RunAsync(TestContext.Current.CancellationToken);
        Assert.Empty(second);
        Assert.Equal(5L, await db.ScalarAsync<long>("select count(*) from information_schema.tables where table_schema = current_schema() and table_name in ('users','recovery_codes','sessions','connections','schema_migrations')"));
    }

    [Fact]
    public async Task User_names_are_unique_ignoring_case_and_sessions_follow_their_user()
    {
        await using var db = await TestDb.CreateAsync();
        await db.ExecAsync("insert into users (name, password_hash, totp_secret) values ('Jul', 'x', 'y')");
        var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.ExecAsync("insert into users (name, password_hash, totp_secret) values ('JUL', 'x', 'y')"));
        Assert.Equal("23505", duplicate.SqlState);
        await db.ExecAsync("insert into sessions (hash, user_id, created_at, last_seen, rotated_at) select 'h', id, now(), now(), now() from users");
        await db.ExecAsync("delete from users");
        Assert.Equal(0L, await db.ScalarAsync<long>("select count(*) from sessions"));
    }
}
