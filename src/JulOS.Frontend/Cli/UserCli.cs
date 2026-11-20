using System.Text;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;
using Npgsql;

namespace JulOS.Frontend.Cli;

/// <summary>
/// Konten auf der Server-Konsole verwalten (Notweg, wenn das Passwort oder der Authenticator verloren ist):
///   dotnet JulOS.dll user list | add NAME | passwd NAME | totp NAME | logout NAME | remove NAME
/// Passwoerter werden im Terminal getippt (ohne Anzeige), nie als Argument.
/// </summary>
internal static class UserCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("JULOS_DB");
        if (string.IsNullOrEmpty(cs)) { Console.Error.WriteLine("JULOS_DB fehlt."); return 2; }
        var builder = new NpgsqlDataSourceBuilder(cs);
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        await using var db = builder.Build();
        var users = new UserStore(db, new SecretBox(SecretBox.ResolveKey(Environment.GetEnvironmentVariable("JULOS_SECRET_KEY"), Environment.GetEnvironmentVariable("JULOS_DATA"))));
        var sessions = new SessionStore(db);
        var hasher = new PasswordHasher();
        var cmd = args.Length > 0 ? args[0] : "";
        var name = args.Length > 1 ? args[1] : null;

        try
        {
            switch (cmd)
            {
                case "list":
                    var list = await users.ListAsync();
                    if (list.Count == 0) Console.WriteLine("Noch kein Konto. Die Einrichtung läuft im Browser (Setup-Code steht im Server-Log).");
                    foreach (var u in list) Console.WriteLine($"{u.Name}  angelegt {u.CreatedAt:yyyy-MM-dd}  Sitzungen {u.Sessions}  Wiederherstellungscodes übrig {u.RecoveryLeft}");
                    return 0;
                case "add":
                {
                    var n = RequireName(name);
                    var password = ReadNewPassword(n);
                    var secret = Totp.NewSecret();
                    var (plain, hashes) = RecoveryCodes.Generate();
                    Show(n, secret, plain);
                    Console.Write("Zur Kontrolle den aktuellen Code aus der App eingeben: ");
                    var check = Totp.Check(secret, (Console.ReadLine() ?? "").Trim(), 0, DateTimeOffset.UtcNow);
                    if (check is null) { Console.Error.WriteLine("Der Code stimmt nicht, das Konto wurde nicht angelegt."); return 1; }
                    var row = await users.CreateAsync(n, await hasher.HashAsync(password), secret, confirmed: true, hashes, replacePending: false);
                    await users.TryAdvanceTotpStepAsync(row.Id, check.Value);
                    Console.WriteLine($"Konto \"{n}\" angelegt.");
                    return 0;
                }
                case "passwd":
                {
                    var user = await Find(users, RequireName(name));
                    await users.SetPasswordAsync(user.Id, await hasher.HashAsync(ReadNewPassword(user.Name)), endSessions: true);
                    Console.WriteLine("Passwort geändert, alle Sitzungen beendet.");
                    return 0;
                }
                case "totp":
                {
                    var user = await Find(users, RequireName(name));
                    var secret = Totp.NewSecret();
                    var (plain, hashes) = RecoveryCodes.Generate();
                    await users.ReplaceTotpAsync(user.Id, secret, hashes);
                    Show(user.Name, secret, plain);
                    Console.WriteLine("Alle Sitzungen beendet.");
                    return 0;
                }
                case "logout":
                {
                    var user = await Find(users, RequireName(name));
                    Console.WriteLine($"{await sessions.DeleteAllAsync(user.Id)} Sitzung(en) beendet.");
                    return 0;
                }
                case "remove":
                {
                    var n = RequireName(name);
                    Console.Write($"Konto \"{n}\" wirklich löschen? (ja/nein) ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), "ja", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Abgebrochen."); return 1; }
                    Console.WriteLine(await users.DeleteAsync(n) ? "Konto gelöscht." : "Konto nicht gefunden.");
                    return 0;
                }
                default:
                    Console.WriteLine("Aufruf: dotnet JulOS.dll user list | add NAME | passwd NAME | totp NAME | logout NAME | remove NAME");
                    return 2;
            }
        }
        catch (InvalidOperationException e) { Console.Error.WriteLine("Fehler: " + e.Message); return 1; }
    }

    private static string RequireName(string? name) =>
        PasswordPolicy.NormalizeUserName(name) ?? throw new InvalidOperationException("Benutzername fehlt oder ist ungültig (a-z, 0-9, Punkt, Strich, Unterstrich).");

    private static async Task<UserRow> Find(UserStore users, string name) =>
        await users.FindByNameAsync(name) ?? throw new InvalidOperationException("Konto nicht gefunden.");

    private static void Show(string user, string secret, string[] recovery)
    {
        Console.WriteLine();
        Console.WriteLine("== Zweiter Faktor (TOTP) ==");
        Console.WriteLine("In der Authenticator-App oder im Passwortmanager als Eintrag anlegen:");
        Console.WriteLine("  Schlüssel (zeitbasiert, 6 Stellen, 30 s): " + string.Join(' ', Enumerable.Range(0, secret.Length / 4).Select(i => secret.Substring(i * 4, 4))));
        Console.WriteLine("  Oder als Link: " + Totp.Uri(user, secret));
        Console.WriteLine();
        Console.WriteLine("== Wiederherstellungscodes (jeder gilt nur einmal, jetzt sicher aufbewahren) ==");
        for (var i = 0; i < recovery.Length; i += 2) Console.WriteLine("  " + string.Join("   ", recovery.Skip(i).Take(2)));
        Console.WriteLine();
    }

    private static string ReadNewPassword(string user)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Dafür ist ein Terminal nötig (docker compose exec -it ...).");
        for (;;)
        {
            var a = ReadSecret("Neues Passwort (mindestens 12 Zeichen): ");
            var problem = PasswordPolicy.Problem(a, user);
            if (problem.Length > 0) { Console.WriteLine(problem); continue; }
            if (a != ReadSecret("Noch einmal: ")) { Console.WriteLine("Die Eingaben sind nicht gleich."); continue; }
            return a;
        }
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        for (ConsoleKeyInfo k; (k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter;)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
