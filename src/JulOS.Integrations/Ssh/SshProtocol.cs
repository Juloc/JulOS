using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace JulOS.Integrations.Ssh;

/// <summary>
/// WebSocket-Protokoll des Terminals (/api/remote/ssh), binaer, erstes Byte = Art. Browser -> Server:
/// 1 Daten (Bytes fuer die Shell), 2 Groesse (u16 Spalten, u16 Zeilen, big endian), 3 Lebenszeichen (leer),
/// 4 Pause und 5 Weiter (der Browser kommt mit dem Zeichnen nicht nach: der Server liest die Shell nicht weiter, das SSH-Fenster bremst das Ziel).
/// Server -> Browser: 1 Daten (Ausgabe der Shell), 2 Bereit (JSON: hostKey "new" | "known" | "changed-accepted", fingerprint), 3 Ende (JSON: code, message,
/// bei Code 800 zusaetzlich fingerprint und previous). Nach "Ende" schliesst der Server.
/// </summary>
public static class SshProtocol
{
    public const byte ToShellData = 1, ToShellResize = 2, ToShellPing = 3, ToShellPause = 4, ToShellResume = 5;
    public const byte FromShellData = 1, FromReady = 2, FromEnd = 3;

    // Codes wie bei den anderen Verbindungen, dazu die des Terminals
    public const int ErrorBusy = 513, ErrorTimeout = 514, ErrorConnectFailed = 515, ErrorUnreachable = 520, ErrorLogonFailed = 769,
        ErrorHostKeyChanged = 800, EndShellClosed = 801, EndSessionExpired = 802,
        EndTaken = 777;                 // eine neuere Sitzung im selben Slot hat uebernommen

    public const int MaxClientMessage = 64 * 1024;
    public const int MinCols = 2, MaxCols = 500, MinRows = 1, MaxRows = 300;

    public static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    public static byte[] Ready(string hostKeyState, string fingerprint) => Json(FromReady, ("hostKey", hostKeyState), ("fingerprint", fingerprint));

    public static byte[] End(int code, string message, string? fingerprint = null, string? previous = null)
    {
        var fields = new List<(string, object)> { ("code", code), ("message", message) };
        if (fingerprint is not null) fields.Add(("fingerprint", fingerprint));
        if (previous is not null) fields.Add(("previous", previous));
        return Json(FromEnd, [.. fields]);
    }

    // Von Hand geschrieben: die Veroeffentlichung schaltet die Reflexion von System.Text.Json ab (nur die Typen in AppJson sind erlaubt)
    private static byte[] Json(byte kind, params (string Name, object Value)[] fields)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(kind);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                if (value is int n) w.WriteNumber(name, n);
                else w.WriteString(name, (string)value);
            }
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Liest eine Groessen-Nachricht; false bei falscher Laenge.</summary>
    public static bool TryReadResize(ReadOnlySpan<byte> m, out int cols, out int rows)
    {
        cols = rows = 0;
        if (m.Length != 5) return false;
        cols = Clamp(BinaryPrimitives.ReadUInt16BigEndian(m[1..]), MinCols, MaxCols);
        rows = Clamp(BinaryPrimitives.ReadUInt16BigEndian(m[3..]), MinRows, MaxRows);
        return true;
    }

    public static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);
}
