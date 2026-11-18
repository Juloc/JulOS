using System.Text.RegularExpressions;

namespace JulOS.Backend.Logic;

public static partial class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,31}$")]
    private static partial Regex UserNameRegex();

    /// <summary>Gibt den bereinigten Benutzernamen zurueck (klein geschrieben) oder null, wenn er nicht erlaubt ist.</summary>
    public static string? NormalizeUserName(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return UserNameRegex().IsMatch(n) ? n : null;
    }

    /// <summary>Leerer Text = in Ordnung, sonst die Erklaerung fuer den Benutzer.</summary>
    public static string Problem(string? password, string userName)
    {
        if (password is null || password.Length < MinLength) return $"Das Passwort braucht mindestens {MinLength} Zeichen.";
        if (password.Length > MaxLength) return "Das Passwort ist zu lang.";
        if (password.Contains(userName, StringComparison.OrdinalIgnoreCase)) return "Das Passwort darf den Benutzernamen nicht enthalten.";
        if (password.Distinct().Count() < 4) return "Das Passwort ist zu einfach.";
        return "";
    }
}
