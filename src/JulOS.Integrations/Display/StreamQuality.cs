namespace JulOS.Integrations.Display;

/// <summary>
/// Was der Benutzer je Sitzung fuer Bildrate und Bitrate waehlt (Nachricht 31 des Browsers, siehe <see cref="ScreenProtocol"/>).
/// <see cref="Auto"/> ist das bisherige Verhalten: kein festes Intervall, die Leitungsmessung (LinkPacer) und der Kodierer bestimmen Bitrate und Tempo.
/// Die anderen Stufen setzen ein kleinstes Intervall zwischen zwei Bildern und eine Obergrenze der Bitrate; die Leitungsmessung darf die Bitrate
/// weiterhin darunter senken (die Stufe ist eine Obergrenze, kein Zwang).
/// </summary>
public enum StreamQuality : byte
{
    /// <summary>Wie bisher: LinkPacer und Kodierer entscheiden.</summary>
    Auto = 0,

    /// <summary>Bis zu 60 Bilder je Sekunde, volle Bitrate.</summary>
    Fps60 = 1,

    /// <summary>Bis zu 30 Bilder je Sekunde, volle Bitrate.</summary>
    Fps30 = 2,

    /// <summary>Sparsam: bis zu 15 Bilder je Sekunde und ein gutes Drittel der Bitrate (Mobilfunk, schwacher Akku).</summary>
    Economy = 3,
}

/// <summary>Die Zahlen hinter den Stufen von <see cref="StreamQuality"/>; reine Rechnung (getestet in StreamQualityTests).</summary>
public static class StreamQualityPolicy
{
    /// <summary>Anteil der konfigurierten Bitrate, den "Sparsam" hoechstens braucht.</summary>
    public const double EconomyShare = 0.35;

    /// <summary>Die Stufe aus dem Byte der Nachricht; unbekannte Werte sind keine Stufe (null).</summary>
    public static StreamQuality? Parse(byte value) => Enum.IsDefined((StreamQuality)value) ? (StreamQuality)value : null;

    /// <summary>Wie viele Millisekunden mindestens zwischen zwei Bildern liegen (0: kein Mindestabstand).</summary>
    public static int MinIntervalMs(StreamQuality quality) => quality switch
    {
        StreamQuality.Fps60 => 16,
        StreamQuality.Fps30 => 33,
        StreamQuality.Economy => 66,
        _ => 0,
    };

    /// <summary>
    /// Die Bitrate (kbit/s), die gelten soll: die von der Leitungsmessung erlaubte (<paramref name="paced"/>), hoechstens die der Stufe
    /// (ein Anteil der konfigurierten <paramref name="max"/>), nie unter <see cref="LinkPacer.MinKilobits"/> und nie ueber der erlaubten.
    /// </summary>
    public static int Kilobits(StreamQuality quality, int max, int paced)
    {
        if (quality != StreamQuality.Economy)
        {
            return paced;
        }

        var cap = Math.Max(LinkPacer.MinKilobits, (int)(max * EconomyShare));
        return Math.Min(paced, cap);
    }
}
