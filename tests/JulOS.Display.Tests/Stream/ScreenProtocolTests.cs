using System.Buffers.Binary;

namespace JulOS.Integrations.Display.Tests;

public sealed class ScreenProtocolTests
{
    [Fact]
    public void A_picture_message_has_kind_flags_sequence_and_the_data()
    {
        var buffer = new byte[64];
        var length = ScreenProtocol.WritePicture(buffer, 0x01020304, true, [9, 8, 7]);
        Assert.Equal(ScreenProtocol.PictureHeader + 3, length);
        Assert.Equal(ScreenProtocol.ToBrowserPicture, buffer[0]);
        Assert.Equal(1, buffer[1]);                                               // Schluesselbild
        Assert.Equal(0x01020304, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(2)));
        Assert.Equal([9, 8, 7], buffer[6..9]);
        Assert.Equal(0, WriteNonKey(buffer)[1]);

        static byte[] WriteNonKey(byte[] b) { ScreenProtocol.WritePicture(b, 1, false, []); return b; }
    }

    [Fact]
    public void A_cursor_message_carries_the_hotspot_the_size_and_the_pixels()
    {
        var buffer = new byte[ScreenProtocol.CursorHeader + 16];
        var cursor = new ScreenCursor(CursorKind.Shape, 3, 4, 2, 2, new byte[16]);
        var length = ScreenProtocol.WriteCursor(buffer, cursor);
        Assert.Equal(ScreenProtocol.CursorHeader + 16, length);
        Assert.Equal((byte)0, buffer[1]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8)));

        var hidden = ScreenProtocol.WriteCursor(buffer, new ScreenCursor(CursorKind.Hidden, 0, 0, 0, 0, null));
        Assert.Equal(ScreenProtocol.CursorHeader, hidden);
        Assert.Equal((byte)1, buffer[1]);
    }

    [Fact]
    public void Size_error_and_taken_messages_are_short_and_exact()
    {
        var buffer = new byte[8];
        Assert.Equal(5, ScreenProtocol.WriteSize(buffer, 1280, 720));
        Assert.Equal([3, 0x05, 0x00, 0x02, 0xD0], buffer[..5]);

        var error = ScreenProtocol.Error(769, "Anmeldung");
        Assert.Equal(ScreenProtocol.ToBrowserError, error[0]);
        Assert.Equal(769, BinaryPrimitives.ReadUInt16BigEndian(error.AsSpan(1)));
        Assert.Equal("Anmeldung", System.Text.Encoding.UTF8.GetString(error, 3, error.Length - 3));
        Assert.Equal([ScreenProtocol.ToBrowserTaken], ScreenProtocol.Taken());
    }

    [Fact]
    public void A_clipboard_message_is_the_kind_byte_and_utf8_text()
    {
        var message = ScreenProtocol.Clipboard("Grüße €");
        Assert.Equal(ScreenProtocol.ToBrowserClipboard, message[0]);
        Assert.Equal("Grüße €", System.Text.Encoding.UTF8.GetString(message, 1, message.Length - 1));
        Assert.Equal(6, ScreenProtocol.ToBrowserClipboard);
        Assert.Equal(9, ScreenProtocol.FromBrowserClipboard);
    }
}
