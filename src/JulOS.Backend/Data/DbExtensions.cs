using Npgsql;

namespace JulOS.Backend.Data;

public static class DbExtensions
{
    /// <summary>Befehl mit Platzhaltern $1, $2, ... Es werden immer Parameter benutzt, nie zusammengesetzter Text.</summary>
    public static NpgsqlCommand Cmd(this NpgsqlDataSource db, string sql, params object?[] args)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        return cmd;
    }

    public static NpgsqlCommand Cmd(this NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params object?[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        return cmd;
    }
}
