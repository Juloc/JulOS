using System.Net;
using System.Net.Sockets;

namespace JulOS.Backend.Modules;

/// <summary>
/// Ein Ziel aus der Caddy-Konfiguration ("localhost:3000", "https://dienst:8443", ":9000"), zerlegt in Host und Port. Nur Ziele, die in der
/// Konfiguration stehen, werden je geprueft (die Oberflaeche kann kein Ziel nennen); nicht pruefbar sind Unix-Sockets, Platzhalter
/// und Adressen, die nie ein Dienst im eigenen Netz sind (Link-Local mit dem Metadaten-Dienst der Cloud, "0.0.0.0", Multicast).
/// </summary>
public sealed record UpstreamTarget(string Host, int Port)
{
    public static UpstreamTarget? Parse(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 300 || text.Contains('{') || text.Contains(' ')) return null;
        var port = 80;
        var rest = text;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var s = text[..scheme].ToLowerInvariant();
            if (s is "unix" or "unix+h2c") return null;
            if (s == "https") port = 443;
            rest = text[(scheme + 3)..];
        }
        else if (text.StartsWith("unix", StringComparison.OrdinalIgnoreCase) && text.Length > 4 && text[4] is '/' or '+') return null;
        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];
        var at = rest.LastIndexOf('@');
        if (at >= 0) rest = rest[(at + 1)..];

        string host;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0) return null;
            host = rest[1..close];
            if (rest.Length > close + 1)
            {
                if (rest[close + 1] != ':' || !int.TryParse(rest[(close + 2)..], out port)) return null;
            }
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            if (colon >= 0)
            {
                host = rest[..colon];
                var p = rest[(colon + 1)..];
                // "host:8080-8090" (Portbereich) und andere Formen: nicht pruefbar
                if (!int.TryParse(p, out port)) return null;
            }
            else host = rest;
        }
        if (host.Length == 0) host = "localhost";   // ":3000" heisst bei Caddy der eigene Rechner
        if (port is < 1 or > 65535) return null;
        return new UpstreamTarget(host, port);
    }

    /// <summary>Adressen, zu denen der Server nie verbindet, auch wenn sie in der Konfiguration stehen.</summary>
    public static bool IsForbidden(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.Any.Equals(a) || IPAddress.IPv6Any.Equals(a) || IPAddress.Broadcast.Equals(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 169 && b[1] == 254 || b[0] >= 224;   // Link-Local (Metadaten-Dienste), Multicast und reserviert
        }
        return a.IsIPv6LinkLocal || a.IsIPv6Multicast;
    }
}
