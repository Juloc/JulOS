using System.Security.Cryptography;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using JulOS.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace JulOS.Frontend.Cli;

/// <summary>
/// Sicherung auf der Server-Konsole (siehe docs/BACKUP.md):
///   dotnet JulOS.dll backup create
///   dotnet JulOS.dll backup restore DATEI [--key SCHLUESSELDATEI] [--force]
/// restore: der Server sollte dabei nicht laufen. Ohne --force muss die Datenbank leer sein; mit --force wird sie ersetzt.
/// </summary>
internal static class BackupCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("JULOS_DB");
        if (string.IsNullOrEmpty(cs)) { Console.Error.WriteLine("JULOS_DB fehlt."); return 2; }
        var builder = new NpgsqlDataSourceBuilder(cs);
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        await using var db = builder.Build();
        try
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "create": return await CreateAsync(db);
                case "restore": return await RestoreAsync(db, args[1..]);
                default:
                    Console.WriteLine("Aufruf: dotnet JulOS.dll backup create | restore DATEI [--key SCHLÜSSELDATEI] [--force]");
                    return 2;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or PostgresException or NpgsqlException)
        {
            Console.Error.WriteLine("Fehler: " + e.Message);
            return 1;
        }
    }

    private static async Task<int> CreateAsync(NpgsqlDataSource db)
    {
        var o = BackupOptions.FromSettings(Env);
        if (o.Off) { Console.Error.WriteLine("JULOS_BACKUP=off: die Sicherung ist abgeschaltet."); return 1; }
        var svc = new BackupService(db, o, TimeProvider.System, NullLogger<BackupService>.Instance, BackupTargets.Factory(o, NullLogger.Instance));
        svc.Initialize();
        var path = await svc.RunAsync();
        Console.WriteLine($"Backup geschrieben: {path} ({new FileInfo(path).Length} Byte)");
        switch (svc.Status.Copy)
        {
            case { State: "ok" } c: Console.WriteLine($"Zweites Ziel {c.Target}: kopiert."); break;
            case { State: "failed" } c: Console.Error.WriteLine($"WARNUNG: Zweites Ziel {c.Target}: {c.Message} (das lokale Backup ist in Ordnung)."); break;
        }
        return 0;
    }

    private static async Task<int> RestoreAsync(NpgsqlDataSource db, string[] args)
    {
        string? file = null, keyFile = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--force") force = true;
            else if (args[i] == "--key" && i + 1 < args.Length) keyFile = args[++i];
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && file is null) file = args[i];
            else { Console.Error.WriteLine("Unbekannte Angabe: " + args[i]); return 2; }
        }
        if (file is null) { Console.Error.WriteLine("Aufruf: dotnet JulOS.dll backup restore DATEI [--key SCHLÜSSELDATEI] [--force]"); return 2; }
        if (!File.Exists(file)) { Console.Error.WriteLine("Datei nicht gefunden: " + file); return 1; }
        var dataDir = Env("JULOS_DATA");
        var envKey = Env("JULOS_SECRET_KEY");

        // Schluessel zuerst (und vor jeder Aenderung der Datenbank geprueft), damit eine falsche Schluesseldatei nichts halb Fertiges hinterlaesst
        if (keyFile is not null)
        {
            if (envKey is not null) { Console.Error.WriteLine("JULOS_SECRET_KEY ist gesetzt und gilt vor der Schlüsseldatei: --key hätte keine Wirkung. Die Variable auf den Schlüssel aus dem Backup stellen."); return 1; }
            if (dataDir is null) { Console.Error.WriteLine("JULOS_DATA fehlt: unklar, wohin der Schlüssel soll."); return 1; }
            InstallKey(keyFile, Path.Combine(dataDir, BackupService.KeyName), force);
        }

        using (var reader = DbDump.OpenForReading(file))
        {
            var summary = await DbDump.RestoreAsync(db, reader, force);
            Console.WriteLine($"Wiederhergestellt: {summary.TotalRows} Zeilen in {summary.Rows.Count} Tabellen.");
        }

        // Kann der Server die gespeicherten Passwoerter danach lesen? Nur mit vorhandenem Schluessel pruefen (ResolveKey wuerde sonst einen neuen anlegen).
        var keyExists = envKey is not null || (dataDir is not null && File.Exists(Path.Combine(dataDir, BackupService.KeyName)));
        var box = new SecretBox(keyExists ? SecretBox.ResolveKey(envKey, dataDir) : null);
        var (total, bad) = await DbDump.CheckSecretsAsync(db, box);
        if (total == 0) Console.WriteLine("Es sind keine Verbindungs-Passwörter gespeichert.");
        else if (bad == 0) Console.WriteLine($"Gespeicherte Passwörter: alle {total} lassen sich mit dem Schlüssel entschlüsseln.");
        else
        {
            Console.Error.WriteLine($"ACHTUNG: {bad} von {total} gespeicherten Passwörtern lassen sich mit dem vorhandenen Schlüssel NICHT entschlüsseln. " +
                                    "Es fehlt der Schlüssel aus dem Backup: backup restore ... --key <Ordner>/secret.key (oder JULOS_SECRET_KEY setzen).");
            return 3;
        }
        return 0;
    }

    /// <summary>Kopiert die gesicherte Schluesseldatei in den Datenordner (Rechte 0600). Ein anderer vorhandener Schluessel wird nur mit --force ersetzt.</summary>
    private static void InstallKey(string source, string dest, bool force)
    {
        var key = File.ReadAllText(source).Trim();
        _ = new SecretBox(key);                                                          // wirft, wenn es kein gueltiger Schluessel ist
        if (File.Exists(dest))
        {
            if (File.ReadAllText(dest).Trim() == key) { Console.WriteLine("Schlüssel ist schon an Ort und Stelle."); return; }
            if (!force) throw new InvalidOperationException($"{dest} enthält einen anderen Schlüssel. Wenn der alte nicht mehr gebraucht wird: mit --force ersetzen (die dazu passenden Passwörter sind dann nicht mehr lesbar).");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".tmp";
        using (var fs = PrivateFile.Create(tmp))
            fs.Write(System.Text.Encoding.ASCII.GetBytes(key + "\n"));
        File.Move(tmp, dest, overwrite: true);
        Console.WriteLine("Schlüssel wiederhergestellt: " + dest);
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);
}
