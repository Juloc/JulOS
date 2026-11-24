using System.Net;
using System.Net.Sockets;

namespace JulOS.Backend.Logic;

/// <summary>Wohin JulOS ein Logout-Token schicken darf. Dev: der Aussteller ist selbst http-localhost (Entwicklung), dann sind auch Loopback und lokales Netz erlaubt.</summary>
public readonly record struct OidcNetPolicy(bool AllowPrivate, bool Dev)
{
    public static OidcNetPolicy For(bool appAllowsPrivate, string? issuer) => new(appAllowsPrivate, OidcNet.IsDevIssuer(issuer));
}

/// <summary>
/// Welche Zieladressen die Zustellung der Logout-Token erreichen darf (Schutz vor SSRF, docs/SIGN-IN.md). Die Adresse einer App ist vom Eigentuemer eingetragen, aber sie wird
/// bei jedem Verbinden nach der Namensaufloesung geprueft (ein Name, der erst spaeter auf eine innere Adresse zeigt, hilft nicht): nie Loopback (JulOS selbst und alles, was nur lokal lauscht),
/// nie Link-Local (169.254.0.0/16 mit den Metadaten der Cloud, fe80::/10), nie "unbestimmt", Multicast oder Broadcast; Adressen des lokalen Netzes nur mit dem Haken der App.
/// Nur in der Entwicklung (<see cref="OidcNetPolicy.Dev"/>) ist auch Loopback erlaubt.
/// </summary>
public static class OidcNet
{
    public static bool IsDevIssuer(string? issuer) =>
        issuer is not null && Uri.TryCreate(issuer, UriKind.Absolute, out var u) && u.Scheme == "http" && OidcRules.IsLoopbackName(u.Host);

    public static bool Allowed(IPAddress address, OidcNetPolicy policy)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)) return false;
        if (IPAddress.IsLoopback(address)) return policy.Dev;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 || b[0] >= 224) return false;                                    // 0.0.0.0/8, Multicast und reserviert
            if (b[0] == 169 && b[1] == 254) return false;                                  // Link-Local, darunter 169.254.169.254 (Metadaten)
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;                       // 192.0.0.0/24 (unter anderem Metadaten einiger Anbieter)
            if (IsPrivate4(b)) return policy.AllowPrivate || policy.Dev;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0xfd && b[1] == 0x00 && b[2] == 0x0e && b[3] == 0xc2) return false;   // fd00:ec2::/32 (Metadaten bei AWS)
            if ((b[0] & 0xfe) == 0xfc) return policy.AllowPrivate || policy.Dev;           // fc00::/7 (lokales Netz)
        }
        return true;
    }

    private static bool IsPrivate4(byte[] b) =>
        b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);   // zuletzt: 100.64.0.0/10 (CGNAT, zum Beispiel VPN-Netze)

    /// <summary>Ein Fehler, wenn die Adresse (ein IP-Wert in der Adresse) schon beim Eintragen nicht erlaubt ist. Namen lassen sich erst beim Verbinden pruefen.</summary>
    public static string? LiteralProblem(string uri, OidcNetPolicy policy)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        var host = u.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (Allowed(ip, policy)) return null;
            return IPAddress.IsLoopback(ip) ? "Loopback ist nicht erlaubt." : IsLocalNetwork(ip) ? "Adresse im lokalen Netz: den Haken „App im lokalen Netz“ setzen." : "Diese Adresse ist nicht erlaubt (Link-Local, Metadaten, reserviert).";
        }
        // ein Name, der Loopback bedeutet, ist ohne Aufloesung erkennbar
        if (OidcRules.IsLoopbackName(u.Host) && !policy.Dev) return "localhost ist nicht erlaubt.";
        return null;
    }

    private static bool IsLocalNetwork(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork ? IsPrivate4(b) : (b[0] & 0xfe) == 0xfc;
    }
}
