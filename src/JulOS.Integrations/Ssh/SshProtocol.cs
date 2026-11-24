using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace JulOS.Integrations.Ssh;

/// <summary>
/// WebSocket-Protokoll des Terminals (/api/remote/ssh), binaer, erstes Byte = Art. Browser -> Server:
/// 1 Daten (Bytes fuer die Shell), 2 Groesse (u16 Spalten, u16 Zeilen, big endian), 3 Lebenszeichen (Ping; ein neuer Browser haengt eine Nummer u32 an und schickt es nur nach "4 Lebenszeichen moeglich"),
/// 4 Pause und 5 Weiter (der Browser kommt mit dem Zeichnen nicht nach: der Server liest die Shell nicht weiter, das SSH-Fenster bremst das Ziel).
/// Server -> Browser: 1 Daten (Ausgabe der Shell), 2 Bereit (JSON: hostKey "new" | "known" | "changed-accepted", fingerprint; bei einer Programm-Sitzung
/// zusaetzlich program = was wirklich laeuft ("shell" | "claude" | "codex" | "custom"), keep = "tmux" wenn die Sitzung in tmux laeuft, notice = Hinweise, durch Leerzeichen
/// getrennt: program-missing, dir-missing, tmux-missing, probe-failed), 3 Ende (JSON: code, message,
/// bei Code 800 zusaetzlich fingerprint und previous), "Bereit" nennt zusaetzlich link = 1 (der Server antwortet auf 3 mit 5 und beendet eine Leitung,
/// von der mehr als <see cref="LinkWatch.DefaultDeadAfter"/> nichts kam), 4 Pong (die Nummer des Pings, u32). Nach "Ende" schliesst der Server.
/// </summary>
public static class SshProtocol
{
    public const byte ToShellData = 1, ToShellResize = 2, ToShellPing = 3, ToShellPause = 4, ToShellResume = 5;
    public const byte FromShellData = 1, FromReady = 2, FromEnd = 3, FromPong = 4;

    // Codes wie bei den anderen Verbindungen, dazu die des Terminals
    public const int ErrorBusy = 513, ErrorTimeout = 514, ErrorConnectFailed = 515, ErrorUnreachable = 520, ErrorLogonFailed = 769,
        ErrorHostKeyChanged = 800, EndShellClosed = 801, EndSessionExpired = 802,
        EndTaken = 777;                 // eine neuere Sitzung im selben Slot hat uebernommen

    public const int MaxClientMessage = 64 * 1024;
    public const int MinCols = 2, MaxCols = 500, MinRows = 1, MaxRows = 300;

    public static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    public static byte[] Ready(string hostKeyState, string fingerprint, SshStartPlan? plan = null)
    {
        var fields = new List<(string, object)> { ("hostKey", hostKeyState), ("fingerprint", fingerprint), ("link", 1) };
        if (plan is not null)
        {
            fields.Add(("program", plan.Program));
            if (plan.Tmux) fields.Add(("keep", "tmux"));
            if (plan.Notices.Count > 0) fields.Add(("notice", plan.NoticeText));
        }
        return Json(FromReady, [.. fields]);
    }

    public static byte[] Pong(ReadOnlySpan<byte> idBytes)
    {
        var m = new byte[5];
        m[0] = FromPong;
        idBytes[..Math.Min(4, idBytes.Length)].CopyTo(m.AsSpan(1));
        return m;
    }

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
