namespace JulOS.Backend.Logic;

/// <summary>Der Login eines Hosts, so wie die Auswahl ihn braucht (ohne das Passwort): Benutzer, ob ein Passwort gespeichert ist, und die Adressen, fuer die es gilt.</summary>
public sealed record HostLoginInfo(Guid HostId, string? User, bool HasPassword, IReadOnlyList<string> Targets);

public enum LoginSource { Own, Host }

/// <summary>Womit sich eine Verbindung anmeldet: der Benutzer, die Quelle und ob dort ein Passwort liegt (Host-Login nur fuer die Adressen, an die es gebunden ist).</summary>
public sealed record LoginChoice(LoginSource Source, string Username, bool PasswordStored, bool AddressNotTrusted = false);

/// <summary>
/// Welche Zugangsdaten eine Verbindung benutzt (docs/HOSTS.md): erst ihre ausdrueckliche Ueberschreibung (own_login), sonst der Login ihres Hosts, sonst wie vor dem Host-Login
/// (ihr Benutzer und ihr gespeichertes Passwort, sonst wird gefragt). Reine Logik ohne Datenbank, damit Terminal, Dateien, Desktop und die Listen dieselbe Antwort geben.
/// </summary>
public static class LoginResolver
{
    /// <summary>Die Adresse so, wie sie fuer den Vergleich gilt (klein geschrieben, ohne Leerraum).</summary>
    public static string Address(string host) => host.Trim().ToLowerInvariant();

    public static LoginChoice Choose(string username, string address, bool ownLogin, bool ownPassword, HostLoginInfo? host)
    {
        var own = new LoginChoice(LoginSource.Own, username, ownPassword);
        if (ownLogin || host?.User is null) return own;
        if (!host.HasPassword) return new LoginChoice(LoginSource.Host, host.User, false);              // nur der Benutzer: das Passwort wird gefragt
        // Das Passwort des Hosts geht nur an die Adressen, fuer die es gesetzt wurde. Eine andere Adresse faellt auf die eigenen Zugangsdaten zurueck.
        return host.Targets.Contains(Address(address)) ? new LoginChoice(LoginSource.Host, host.User, true) : own with { AddressNotTrusted = true };
    }
}
