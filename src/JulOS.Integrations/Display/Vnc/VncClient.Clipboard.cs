using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace JulOS.Integrations.Display.Vnc;

// Text in both directions. The classic message carries Latin-1 only; a server that knows the extended clipboard (it answers the pseudo-encoding with a
// message of capabilities) is spoken to in UTF-8: "notify" says there is text, "request" asks for it, "provide" carries it zlib-compressed.
public sealed partial class VncClient
{
    private const uint FormatText = 1, ActionCaps = 1u << 24, ActionRequest = 1u << 25, ActionPeek = 1u << 26, ActionNotify = 1u << 27, ActionProvide = 1u << 28;
    private const int MaxClipboardBytes = 1024 * 1024;

    private bool _extendedClipboard;
    private string? _localText;

    /// <summary>Whether the server speaks the extended clipboard (UTF-8 text); without it text goes as Latin-1 and anything else is lost.</summary>
    public bool HasExtendedClipboard => Volatile.Read(ref _extendedClipboard);

    private void ReadCutText(RfbReader reader)
    {
        reader.Skip(3);
        var length = reader.I32();
        if (length >= 0)
        {
            // Latin-1: every byte is the character of that number
            if (length > MaxClipboardBytes)
            {
                reader.Skip(length);
                return;
            }

            ClipboardReceived?.Invoke(Encoding.Latin1.GetString(reader.Bytes(length)));
            return;
        }

        // A negative length: the message is -length bytes, a word of flags first
        var size = -(long)length;
        if (size < 4 || size > MaxClipboardBytes * 2L)
        {
            reader.Skip(size);
            return;
        }

        var body = reader.Bytes((int)size);
        ExtendedClipboard(BinaryPrimitives.ReadUInt32BigEndian(body), body.AsSpan(4));
    }

    private void ExtendedClipboard(uint flags, ReadOnlySpan<byte> rest)
    {
        if ((flags & ActionCaps) != 0)
        {
            // Capabilities: one size for each format that is set. We answer with ours (text only) once.
            if ((flags & FormatText) != 0 && !HasExtendedClipboard)
            {
                Volatile.Write(ref _extendedClipboard, true);
                SendCutText(ActionCaps | FormatText | ActionRequest | ActionPeek | ActionNotify | ActionProvide, MaxClipboardBytes);
            }

            return;
        }

        if ((flags & ActionNotify) != 0)
        {
            // The server has text: ask for it
            if ((flags & FormatText) != 0)
            {
                SendCutText(ActionRequest | FormatText, null);
            }

            return;
        }

        if ((flags & ActionPeek) != 0)
        {
            SendCutText(ActionNotify | (_localText is null ? 0 : FormatText), null);
            return;
        }

        if ((flags & ActionRequest) != 0)
        {
            if (_localText is not null && (flags & FormatText) != 0)
            {
                ProvideText(_localText);
            }

            return;
        }

        if ((flags & ActionProvide) != 0 && (flags & FormatText) != 0)
        {
            // One zlib stream; for each format in the flags a length and the data. Only text is asked for, so it is the only one.
            using var input = new MemoryStream(rest.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var header = new byte[4];
            zlib.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length > MaxClipboardBytes)
            {
                return;
            }

            var data = new byte[length];
            zlib.ReadExactly(data);
            var end = Array.IndexOf(data, (byte)0);
            var text = Encoding.UTF8.GetString(data, 0, end < 0 ? data.Length : end).Replace("\r\n", "\n");
            ClipboardReceived?.Invoke(text);
        }
    }

    // A message with flags (and for capabilities the largest size we take for text).
    private void SendCutText(uint flags, int? textLimit)
    {
        var length = 4 + (textLimit is null ? 0 : 4);
        var message = new byte[8 + length];
        message[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), -length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), flags);
        if (textLimit is { } limit)
        {
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), limit);
        }

        Send(message);
    }

    private void ProvideText(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\0");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)utf8.Length);
            zlib.Write(length);
            zlib.Write(utf8);
        }

        var body = compressed.ToArray();
        var message = new byte[8 + 4 + body.Length];
        message[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), -(4 + body.Length));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), ActionProvide | FormatText);
        body.CopyTo(message, 12);
        Send(message);
    }

    /// <summary>The browser put text on the clipboard: the server is told, and fetches it when something is pasted there.</summary>
    public void SendClipboard(string text)
    {
        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > MaxClipboardBytes)
        {
            return;
        }

        _localText = text;
        if (HasExtendedClipboard)
        {
            SendCutText(ActionNotify | FormatText, null);
            return;
        }

        // Latin-1: a character that is not in it becomes a question mark
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i] = text[i] <= 0xFF ? (byte)text[i] : (byte)'?';
        }

        var message = new byte[8 + bytes.Length];
        message[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), bytes.Length);
        bytes.CopyTo(message, 8);
        Send(message);
    }
}
