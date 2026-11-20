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
///   dotnet JulOS.dll backup restore DATEI [--key SCHLUESSELDATEI | --recovery-key [--recovery-key-file DATEI]] [--force]
/// --recovery-key: der Schluessel der Passwoerter kommt aus der umhuellten Datei neben dem Auszug (secret.key.wrapped) und dem Wiederherstellungsschluessel (Eingabe oder Datei).
/// Fehlt jeder Schluessel und liegt die umhuellte Datei neben dem Auszug, fragt der Befehl in einem Terminal von selbst danach.
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
                case "restore": return await RestoreAsync(db, args[1..], Env);
                default:
                    Console.WriteLine("Aufruf: dotnet JulOS.dll backup create | restore DATEI [--key SCHLÜSSELDATEI | --recovery-key [--recovery-key-file DATEI]] [--force]");
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
        // dieselben Einstellungen wie der Server (App-Einstellung zum Schluessel, umhuellter Schluessel), damit eine Sicherung von Hand nichts anderes ablegt
        var settings = new InstanceSettings(db);
        await settings.LoadAsync();
        var keys = new BackupKeys(settings, o, new SecretBox(SecretBox.ResolveKey(Env("JULOS_SECRET_KEY"), o.DataDir)), TimeProvider.System);
        var svc = new BackupService(db, o, TimeProvider.System, NullLogger<BackupService>.Instance, BackupTargets.Factory(o, NullLogger.Instance), keys.Plan);
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

    /// <param name="env">Liest Einstellungen (JULOS_DATA, JULOS_SECRET_KEY); ein Parameter, damit Tests nicht die Umgebung des ganzen Prozesses aendern muessen.</param>
    internal static async Task<int> RestoreAsync(NpgsqlDataSource db, string[] args, Func<string, string?> env)
    {
        string? file = null, keyFile = null, recoveryFile = null;
        var force = false; var recovery = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--force") force = true;
            else if (args[i] == "--key" && i + 1 < args.Length) keyFile = args[++i];
            else if (args[i] == "--recovery-key") recovery = true;
            else if (args[i] == "--recovery-key-file" && i + 1 < args.Length) { recovery = true; recoveryFile = args[++i]; }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && file is null) file = args[i];
            else { Console.Error.WriteLine("Unbekannte Angabe: " + args[i]); return 2; }
        }
        if (file is null) { Console.Error.WriteLine("Aufruf: dotnet JulOS.dll backup restore DATEI [--key SCHLÜSSELDATEI | --recovery-key [--recovery-key-file DATEI]] [--force]"); return 2; }
        if (!File.Exists(file)) { Console.Error.WriteLine("Datei nicht gefunden: " + file); return 1; }
        var dataDir = env("JULOS_DATA");
        var envKey = env("JULOS_SECRET_KEY");

        var wrappedPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, BackupService.WrappedName);
        var keyInPlace = envKey is not null || (dataDir is not null && File.Exists(Path.Combine(dataDir, BackupService.KeyName)));
        // Ohne Schluessel und ohne Angabe: gibt es die umhuellte Datei neben dem Auszug und ein Terminal, wird nach dem Wiederherstellungsschluessel gefragt
        if (keyFile is null && !recovery && !keyInPlace && File.Exists(wrappedPath) && !Console.IsInputRedirected) recovery = true;
        if (recovery && keyFile is not null) { Console.Error.WriteLine("--key und --recovery-key schließen sich aus."); return 2; }
        if (recovery)
        {
            if (envKey is not null) { Console.Error.WriteLine("JULOS_SECRET_KEY ist gesetzt und gilt vor dem Schlüssel aus dem Backup: --recovery-key hätte keine Wirkung."); return 1; }
            if (dataDir is null) { Console.Error.WriteLine("JULOS_DATA fehlt: unklar, wohin der Schlüssel soll."); return 1; }
            if (!File.Exists(wrappedPath)) { Console.Error.WriteLine($"{BackupService.WrappedName} liegt nicht neben dem Auszug ({wrappedPath}). Aus dem Backup-Ordner oder vom zweiten Ziel mit dem Auszug zusammen holen."); return 1; }
            var text = recoveryFile is not null ? File.ReadAllText(recoveryFile) : ReadRecoveryKey();
            var rk = RecoveryKey.Parse(text);
            if (rk is null) { Console.Error.WriteLine("Das ist kein Wiederherstellungsschlüssel (52 Zeichen in Vierergruppen, wie in den Einstellungen angezeigt)."); return 1; }
            var wrapped = RecoveryKey.FromFile(File.ReadAllBytes(wrappedPath));
            var secret = wrapped is null ? null : RecoveryKey.Unwrap(wrapped, rk);
            if (secret is null) { Console.Error.WriteLine("Der Wiederherstellungsschlüssel passt nicht zu dieser Datei (Tippfehler, oder er gehört zu einem anderen Backup). Es wurde nichts geändert."); return 1; }
            InstallKeyValue(Convert.ToBase64String(secret), Path.Combine(dataDir, BackupService.KeyName), force);
            keyInPlace = true;
        }

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
        var keyExists = keyInPlace || envKey is not null || (dataDir is not null && File.Exists(Path.Combine(dataDir, BackupService.KeyName)));
        var box = new SecretBox(keyExists ? SecretBox.ResolveKey(envKey, dataDir) : null);
        var (total, bad) = await DbDump.CheckSecretsAsync(db, box);
        if (total == 0) Console.WriteLine("Es sind keine Verbindungs-Passwörter gespeichert.");
        else if (bad == 0) Console.WriteLine($"Gespeicherte Passwörter: alle {total} lassen sich mit dem Schlüssel entschlüsseln.");
        else
        {
            Console.Error.WriteLine($"ACHTUNG: {bad} von {total} gespeicherten Passwörtern lassen sich mit dem vorhandenen Schlüssel NICHT entschlüsseln. " +
                                    "Es fehlt der Schlüssel: backup restore ... --recovery-key (Wiederherstellungsschlüssel aus dem Passwortmanager), oder --key <Datei>/secret.key, oder JULOS_SECRET_KEY setzen.");
            return 3;
        }
        return 0;
    }

    /// <summary>Kopiert die gesicherte Schluesseldatei in den Datenordner (Rechte 0600). Ein anderer vorhandener Schluessel wird nur mit --force ersetzt.</summary>
    private static void InstallKey(string source, string dest, bool force) => InstallKeyValue(File.ReadAllText(source).Trim(), dest, force);

    private static void InstallKeyValue(string key, string dest, bool force)
    {
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

    /// <summary>Liest den Wiederherstellungsschluessel von der Eingabe, ohne ihn anzuzeigen (in einem Terminal); sonst eine Zeile.</summary>
    private static string ReadRecoveryKey()
    {
        Console.Error.Write("Wiederherstellungsschlüssel: ");
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new System.Text.StringBuilder();
        for (ConsoleKeyInfo k; (k = Console.ReadKey(true)).Key != ConsoleKey.Enter;)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);
}
