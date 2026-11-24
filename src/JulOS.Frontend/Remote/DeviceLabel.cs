namespace JulOS.Frontend.Remote;

/// <summary>Ein kurzer Name fuer das Geraet aus dem User-Agent ("Firefox, Linux"), nur zur Anzeige in der Liste der erhaltenen Sitzungen und im Verlauf. Geraten, nie ein Nachweis.</summary>
internal static class DeviceLabel
{
    public static string From(string? userAgent)
    {
        var ua = userAgent ?? "";
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") || ua.Contains("FxiOS/") ? "Firefox"
            : ua.Contains("Chrome/") || ua.Contains("CriOS/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "Browser";
        var os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS X") || ua.Contains("Macintosh") ? "macOS" : ua.Contains("Linux") ? "Linux" : "";
        return os.Length == 0 ? browser : browser + ", " + os;
    }
}
