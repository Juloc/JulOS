using System.Text;

namespace JulOS.Integrations.Display.Rdp.Channels;

// CF_UNICODETEXT as Windows keeps it on the clipboard: UTF-16LE, lines ending in CR LF, a terminating NUL.
public static class ClipboardText
{
    public const uint FormatId = ClipboardFormats.UnicodeText;

    // A .NET string to clipboard data. With crlf (the default, as Windows keeps text) line feeds become CR LF (those
    // that already have their CR stay); without it the line ends stay as they are, which is what a Linux desktop
    // behind xrdp wants (xrdp passes the bytes on, and a CR ends up in the terminal and the editor). The text ends
    // at its first NUL - the other side would not read past it anyway.
    public static byte[] Encode(string text, bool crlf = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        var end = text.IndexOf('\0');
        if (end >= 0)
        {
            text = text[..end];
        }

        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            if (crlf && text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
            {
                builder.Append('\r');
            }

            builder.Append(text[i]);
        }

        // Two bytes more than the text: the terminating NUL.
        var windows = builder.ToString();
        var data = new byte[Encoding.Unicode.GetByteCount(windows) + 2];
        Encoding.Unicode.GetBytes(windows, data);
        return data;
    }

    // Clipboard data to a .NET string: up to the first NUL (or the end, when the terminator is missing),
    // CR LF as a plain line feed. Half a surrogate pair comes out as U+FFFD, so the result is always text.
    public static string Decode(ReadOnlySpan<byte> data) => Decode(data, out _, out _);

    // The same, and how many line ends the text had in the two forms: crlf for CR LF, lf for a lone LF.
    public static string Decode(ReadOnlySpan<byte> data, out int crlf, out int lf)
    {
        crlf = 0;
        lf = 0;
        var length = data.Length & ~1;
        var end = length;
        for (var i = 0; i < length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                end = i;
                break;
            }
        }

        var text = Encoding.Unicode.GetString(data[..end]);
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return text.Contains('\r') ? text.Replace("\r\n", "\n") : text;
    }
}
