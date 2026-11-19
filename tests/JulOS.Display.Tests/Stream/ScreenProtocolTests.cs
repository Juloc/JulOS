using System.Buffers.Binary;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Video;

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

    [Fact]
    public void Image_file_list_file_state_limits_and_result_messages_have_the_documented_layout()
    {
        Assert.Equal([7, 2, 9, 8], ScreenProtocol.Image(2, [9, 8]));

        var files = ScreenProtocol.Files(0x01020304, [new RemoteFile("a.txt", 5), new RemoteFile("Ordner/Größe.pdf", -1), new RemoteFile("Ordner", 0, true)]);
        Assert.Equal([8, 1, 2, 3, 4, 0, 3], files[..7]);                         // Nummer, Stapel, Anzahl (2 Bytes)
        Assert.Equal(0, files[7]);                                              // flags: kein Ordner
        Assert.Equal(5, BinaryPrimitives.ReadInt64BigEndian(files.AsSpan(8)));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(files.AsSpan(16)));
        Assert.Equal("a.txt", System.Text.Encoding.UTF8.GetString(files, 18, 5));
        Assert.Equal(0, files[23]);
        Assert.Equal(-1, BinaryPrimitives.ReadInt64BigEndian(files.AsSpan(24)));
        var second = BinaryPrimitives.ReadUInt16BigEndian(files.AsSpan(32));
        Assert.Equal("Ordner/Größe.pdf", System.Text.Encoding.UTF8.GetString(files, 34, second));
        Assert.Equal(1, files[34 + second]);                                    // der Ordner: Bit 0
        var third = BinaryPrimitives.ReadUInt16BigEndian(files.AsSpan(34 + second + 9));
        Assert.Equal("Ordner", System.Text.Encoding.UTF8.GetString(files, 34 + second + 11, third));
        Assert.Equal(34 + second + 11 + third, files.Length);
        Assert.Equal([8, 0, 0, 0, 7, 0, 0], ScreenProtocol.Files(7, []));

        var state = ScreenProtocol.FileState(7, ClipboardBridge.StateRunning, 100, -1, "x");
        Assert.Equal([9, 0, 0, 0, 7, 1], state[..6]);
        Assert.Equal(100, BinaryPrimitives.ReadInt64BigEndian(state.AsSpan(6)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt64BigEndian(state.AsSpan(14)));
        Assert.Equal("x", System.Text.Encoding.UTF8.GetString(state, ScreenProtocol.FileStateHeader, state.Length - ScreenProtocol.FileStateHeader));

        var limits = ScreenProtocol.Limits(2L * 1024 * 1024 * 1024, 200, 255);
        Assert.Equal(14, limits[0]);
        Assert.Equal(2L * 1024 * 1024 * 1024, BinaryPrimitives.ReadInt64BigEndian(limits.AsSpan(1)));
        Assert.Equal((200, 255), (BinaryPrimitives.ReadUInt16BigEndian(limits.AsSpan(9)), BinaryPrimitives.ReadUInt16BigEndian(limits.AsSpan(11))));

        var result = ScreenProtocol.Result(300, ClipboardBridge.ResultTooBig, "zu groß");
        Assert.Equal([10, 0, 0, 1, 44, 1], result[..6]);
        Assert.Equal("zu groß", System.Text.Encoding.UTF8.GetString(result, 6, result.Length - 6));
    }

    [Fact]
    public void The_clipboard_diagnosis_message_carries_state_kind_and_a_count_and_never_text()
    {
        var note = ScreenProtocol.ClipboardNote(3, ClipNote.TextFetchedByVm, 70000);
        Assert.Equal([15, 3, (byte)ClipNote.TextFetchedByVm, 0, 1, 17, 112], note);
        Assert.Equal(ScreenProtocol.ClipNoteLength, note.Length);
    }

    [Fact]
    public void Only_the_text_the_file_path_and_the_file_pieces_may_be_longer_than_an_input()
    {
        foreach (var type in new[] { ScreenProtocol.FromBrowserClipboard, ScreenProtocol.FromBrowserSendBegin, ScreenProtocol.FromBrowserSendChunk, ScreenProtocol.FromBrowserFileRequest })
        {
            Assert.True(ScreenProtocol.MayBeLong(type));
        }

        foreach (var type in new[] { ScreenProtocol.FromBrowserAck, ScreenProtocol.FromBrowserMove, ScreenProtocol.FromBrowserKey, ScreenProtocol.FromBrowserSendEnd, ScreenProtocol.FromBrowserFileCancel })
        {
            Assert.False(ScreenProtocol.MayBeLong(type));
        }
    }

    [Fact]
    public void A_picture_says_in_its_flags_whether_it_is_hevc_and_the_old_bits_stay()
    {
        var buffer = new byte[16];
        ScreenProtocol.WritePicture(buffer, 1, true, [1], VideoCodec.Hevc);
        Assert.Equal(3, buffer[1]);                                                // Bit0 Schluesselbild, Bit1 H.265
        ScreenProtocol.WritePicture(buffer, 1, false, [1], VideoCodec.Hevc);
        Assert.Equal(2, buffer[1]);
        ScreenProtocol.WritePicture(buffer, 1, true, [1]);
        Assert.Equal(1, buffer[1]);                                                // wie bisher: H.264
    }

    [Fact]
    public void The_old_message_numbers_did_not_move_and_the_new_ones_start_at_30()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], new[]
        {
            ScreenProtocol.ToBrowserPicture, ScreenProtocol.ToBrowserCursor, ScreenProtocol.ToBrowserSize, ScreenProtocol.ToBrowserError, ScreenProtocol.ToBrowserTaken,
            ScreenProtocol.ToBrowserClipboard, ScreenProtocol.ToBrowserImage, ScreenProtocol.ToBrowserFiles, ScreenProtocol.ToBrowserFileState, ScreenProtocol.ToBrowserResult,
        }.Select(b => (int)b));
        Assert.Equal(16, ScreenProtocol.FromBrowserPause);
        Assert.Equal(30, ScreenProtocol.ToBrowserEncoder);
        Assert.Equal(30, ScreenProtocol.FromBrowserCodecs);
    }

    [Fact]
    public void The_encoder_message_names_codec_kind_and_encoder()
    {
        var message = ScreenProtocol.Encoder(VideoCodec.Hevc, true, "VAAPI Intel iHD");
        Assert.Equal([30, 2, 1], message[..3]);
        Assert.Equal("VAAPI Intel iHD", System.Text.Encoding.UTF8.GetString(message, 3, message.Length - 3));
        Assert.Equal([30, 1, 0], ScreenProtocol.Encoder(VideoCodec.H264, false, "")[..3]);
    }

    [Theory]
    [InlineData(null, VideoCodecs.H264)]
    [InlineData("", VideoCodecs.H264)]
    [InlineData("x", VideoCodecs.H264)]
    [InlineData("0", VideoCodecs.H264)]
    [InlineData("1", VideoCodecs.H264)]
    [InlineData("3", VideoCodecs.H264 | VideoCodecs.Hevc)]
    [InlineData("2", VideoCodecs.Hevc)]
    [InlineData("255", VideoCodecs.H264 | VideoCodecs.Hevc)]
    [InlineData("4", VideoCodecs.H264)]                 // nothing we know: H.264
    public void The_codecs_in_the_address_are_read_leniently(string? text, VideoCodecs expected)
    {
        Assert.Equal(expected, ScreenProtocol.ParseCodecs(text));
    }

    [Fact]
    public async Task The_codecs_message_from_the_browser_changes_what_the_stream_may_send()
    {
        await using var stream = new ScreenStream(new RdpOptions { Host = "unused", UserName = "u", Password = "p" });
        Assert.Equal(VideoCodecs.H264, stream.ClientCodecs);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserCodecs, 3]);
        Assert.Equal(VideoCodecs.H264 | VideoCodecs.Hevc, stream.ClientCodecs);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserCodecs, 1]);          // the HEVC decoder failed: back to H.264
        Assert.Equal(VideoCodecs.H264, stream.ClientCodecs);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserCodecs, 0]);          // nonsense means H.264
        Assert.Equal(VideoCodecs.H264, stream.ClientCodecs);
        ScreenRelay.Handle(stream, [ScreenProtocol.FromBrowserCodecs]);             // too short: ignored
        Assert.Equal(VideoCodecs.H264, stream.ClientCodecs);
    }
}
