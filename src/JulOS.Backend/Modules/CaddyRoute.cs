namespace JulOS.Backend.Modules;

/// <summary>
/// Eine Zeile der Liste "Domain -> Ziel". Kind: proxy (Ziele = Upstreams), files, redirect (ein Ziel), respond (Detail = Statuscode), other.
/// Mehr als das wird nie aus der Konfiguration uebernommen: keine Kopfzeilen, Zugangsdaten, TLS-Einstellungen oder Antworttexte.
/// </summary>
public sealed record CaddyRoute(string Domain, string? Path, string Kind, string[] Targets, string? Detail = null);

public static class CaddyRedact
{
    /// <summary>Ein Ziel zur Anzeige: ohne Benutzer:Passwort@, ohne Query und Fragment, begrenzt in der Laenge.</summary>
    public static string Target(string text)
    {
        var at = text.LastIndexOf('@');
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (at >= 0 && (scheme < 0 || at > scheme))
            text = (scheme >= 0 ? text[..(scheme + 3)] : "") + text[(at + 1)..];
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0) text = text[..cut];
        return Clip(text);
    }

    public static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
