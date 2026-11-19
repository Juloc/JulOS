namespace JulOS.Backend.Logic;

/// <summary>
/// Tastaturlayouts, die eine Desktop-Verbindung dem Ziel melden kann. Der Code ist der gespeicherte Wert; dazu gehoert die Layout-Kennung von Windows
/// (die der RDP-Server fuer die Tastenbelegung der Sitzung nimmt) und die Art, wie der Browser Tasten fuer Kombinationen wie Strg+C waehlt
/// (stream.js: "de" = deutsche Belegung samt Umlauten ueber ihre Tasten, "qwertz", "qwerty", "azerty" = Lage der Buchstaben; Zeichen gehen sonst als Unicode).
/// </summary>
public static class KeyboardLayouts
{
    public const string Default = "de";

    private static readonly Dictionary<string, (uint WindowsId, string Client)> Known = new(StringComparer.Ordinal)
    {
        ["de"] = (0x0407, "de"),         // Deutsch
        ["de-at"] = (0x0C07, "de"),      // Deutsch (Oesterreich): dieselbe Belegung
        ["de-ch"] = (0x0807, "qwertz"),  // Deutsch (Schweiz)
        ["en-us"] = (0x0409, "qwerty"),  // Englisch (USA)
        ["en-gb"] = (0x0809, "qwerty"),  // Englisch (Vereinigtes Koenigreich)
        ["fr"] = (0x040C, "azerty"),     // Franzoesisch
        ["es"] = (0x040A, "qwerty"),     // Spanisch
        ["it"] = (0x0410, "qwerty"),     // Italienisch
    };

    public static IEnumerable<string> Codes => Known.Keys;

    public static bool IsKnown(string? code) => code is not null && Known.ContainsKey(code);

    /// <summary>Layout-Kennung fuer RDP; ein unbekannter Code (zum Beispiel aus einer spaeteren Version) ist Deutsch.</summary>
    public static uint WindowsId(string? code) => code is not null && Known.TryGetValue(code, out var k) ? k.WindowsId : Known[Default].WindowsId;

    /// <summary>Wie der Browser die Tasten waehlt: "de", "qwertz", "qwerty" oder "azerty".</summary>
    public static string ClientKey(string? code) => code is not null && Known.TryGetValue(code, out var k) ? k.Client : Known[Default].Client;
}
