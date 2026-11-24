using JulOS.Backend.Logic;
using Npgsql;

namespace JulOS.Backend.Data;

public sealed record AiUsageRow(DateOnly Day, Guid AppId, string Provider, string Model, int Requests, int Ok, int Failed, int Aborted, long TokensIn, long TokensOut, long Millis);

/// <summary>
/// Der Verbrauch der Apps (Tabelle ai_usage): nur Zaehler je Tag (UTC), App, Anbieter und Modell. Kein Text einer Anfrage oder Antwort kommt je hierher. Die Budgets rechnen mit
/// Tag und Monat in UTC.
/// </summary>
public sealed class AiUsageStore(NpgsqlDataSource db, TimeProvider time)
{
    private DateOnly _pruned;
    private const int KeepDays = 400;

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>Verbucht eine Anfrage. result: ok, failed oder aborted.</summary>
    public async Task RecordAsync(Guid app, string provider, string model, string result, long tokensIn, long tokensOut, long millis, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd(@"insert into ai_usage (day, app_id, provider, model, requests, ok, failed, aborted, tokens_in, tokens_out, millis)
            values ($1, $2, $3, $4, 1, $5, $6, $7, $8, $9, $10)
            on conflict (day, app_id, provider, model) do update set requests = ai_usage.requests + 1, ok = ai_usage.ok + excluded.ok, failed = ai_usage.failed + excluded.failed,
              aborted = ai_usage.aborted + excluded.aborted, tokens_in = ai_usage.tokens_in + excluded.tokens_in, tokens_out = ai_usage.tokens_out + excluded.tokens_out, millis = ai_usage.millis + excluded.millis",
            Today, app, provider, model.Length > 100 ? model[..100] : model, result == "ok" ? 1 : 0, result == "failed" ? 1 : 0, result == "aborted" ? 1 : 0, Math.Max(0, tokensIn), Math.Max(0, tokensOut), Math.Max(0, millis));
        await cmd.ExecuteNonQueryAsync(ct);
        if (_pruned != Today)
        {
            _pruned = Today;
            await using var prune = db.Cmd("delete from ai_usage where day < $1", Today.AddDays(-KeepDays));
            await prune.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<AiBudget> BudgetAsync(AiApp app, CancellationToken ct = default)
    {
        var today = Today;
        await using var cmd = db.Cmd("select coalesce(sum(tokens_in + tokens_out) filter (where day = $2), 0), coalesce(sum(tokens_in + tokens_out), 0) from ai_usage where app_id = $1 and day >= $3 and day <= $2",
            app.Id, today, new DateOnly(today.Year, today.Month, 1));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new AiBudget(r.GetInt64(0), app.DailyTokens, r.GetInt64(1), app.MonthlyTokens);
    }

    /// <summary>Die Zeilen der letzten Tage (neueste zuerst), fuer eine App oder alle.</summary>
    public async Task<IReadOnlyList<AiUsageRow>> ListAsync(Guid? app, int days, CancellationToken ct = default)
    {
        await using var cmd = db.Cmd("select day, app_id, provider, model, requests, ok, failed, aborted, tokens_in, tokens_out, millis from ai_usage where day > $1 and ($2::uuid is null or app_id = $2) order by day desc, app_id, provider, model",
            Today.AddDays(-Math.Clamp(days, 1, KeepDays)), app);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AiUsageRow>();
        while (await r.ReadAsync(ct)) list.Add(new AiUsageRow(r.GetFieldValue<DateOnly>(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10)));
        return list;
    }
}
