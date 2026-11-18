using System.Buffers.Binary;
using System.Text;
using JulOS.Integrations.Display.Rdp.Channels;
using JulOS.Integrations.Display.Rdp.Channels.Tests;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Bilder und Dateien durch die Zwischenablage-Bruecke, in beide Richtungen, gegen einen Server, der genau das sagt, was der Test vorgibt.</summary>
public sealed class ClipboardTransferTests
{
    private const uint Text = 13, Dib = 8;
    private const uint FileFormat = 0xC0BC;

    private sealed class Log
    {
        public List<string> Texts { get; } = [];
        public List<(byte Format, byte[] Data)> Images { get; } = [];
        public List<(uint Batch, RemoteFile[] Files)> Files { get; } = [];
        public List<(uint Batch, int Index, byte Flags, byte[] Data)> Chunks { get; } = [];
        public List<(uint Id, byte Code, string Message)> Results { get; } = [];
    }

    private static (ClipboardChannel Channel, Wire Wire, ClipboardBridge Bridge, Log Log) Started(uint serverFlags = 0x3E, TimeSpan? idle = null)
    {
        var channel = new ClipboardChannel();
        var wire = new Wire(channel);
        var log = new Log();
        var bridge = new ClipboardBridge(channel, new ClipboardSink
        {
            Text = log.Texts.Add,
            Image = (format, data) => log.Images.Add((format, data)),
            Files = (batch, files) => log.Files.Add((batch, files.ToArray())),
            FileChunk = (batch, index, flags, data) => log.Chunks.Add((batch, index, flags, data)),
            Result = (id, code, message) => log.Results.Add((id, code, message)),
        }, idle);
        channel.Receive(ClipServer.Capabilities(serverFlags));
        channel.Receive(ClipServer.MonitorReady());
        channel.Receive(ClipServer.FormatListResponse(true));
        wire.Take();
        return (channel, wire, bridge, log);
    }

    // Ein Bild von 2 x 1 Punkten als CF_DIB (24 Bit, Zeilen auf vier Bytes aufgefuellt)
    private static byte[] TinyDib()
    {
        var dib = new byte[40 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), 8);
        dib.AsSpan(40).Fill(0x7f);
        return dib;
    }

    private static byte[] Descriptor(params ClipFile[] files) => FileGroupDescriptor.Encode(files);

    private static uint U32(byte[] body, int word) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(word * 4));

    private static void Upload(ClipboardBridge bridge, uint id, byte kind, int index, int count, byte[] data, string name, int chunk = 1000)
    {
        bridge.BeginTransfer(id, kind, index, count, data.Length, name);
        for (var at = 0; at < data.Length; at += chunk)
        {
            bridge.TransferChunk(id, data.AsSpan(at, Math.Min(chunk, data.Length - at)));
        }

        bridge.EndTransfer(id);
    }

    // ---------------------------------------------------------------- ferner Rechner -> Browser

    [Fact]
    public void An_image_copied_on_the_remote_desktop_goes_to_the_browser_as_a_bmp()
    {
        var (channel, wire, _, log) = Started();

        channel.Receive(ClipServer.LongFormatList((Dib, null), (2, null)));
        var sent = wire.Take();
        Assert.Equal(Dib, U32(ClipServer.Parse(sent[1]).Body, 0));              // das Bild wird sofort geholt
        channel.Receive(ClipServer.DataResponse(TinyDib()));

        var (format, bmp) = Assert.Single(log.Images);
        Assert.Equal(ClipboardBridge.ImageBmp, format);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal(14 + 48, bmp.Length);
        Assert.Equal(14 + 40u, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10)));   // die Bildpunkte beginnen hinter dem Kopf
    }

    [Fact]
    public void A_png_the_remote_offers_by_name_wins_and_is_passed_on_unchanged_when_it_is_one()
    {
        var (channel, wire, _, log) = Started();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        channel.Receive(ClipServer.LongFormatList((Dib, null), (0xC123, "PNG")));
        Assert.Equal(0xC123u, U32(ClipServer.Parse(wire.Take()[1]).Body, 0));
        channel.Receive(ClipServer.DataResponse(png));
        Assert.Equal(ClipboardBridge.ImagePng, Assert.Single(log.Images).Format);

        // eine "PNG"-Antwort, die keins ist, geht nicht weiter
        channel.Receive(ClipServer.LongFormatList((0xC123, "PNG")));
        wire.Take();
        channel.Receive(ClipServer.DataResponse([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        Assert.Single(log.Images);
    }

    [Fact]
    public void Text_wins_over_an_image_and_a_broken_image_tells_nobody()
    {
        var (channel, wire, _, log) = Started();

        channel.Receive(ClipServer.LongFormatList((Text, null), (Dib, null)));
        Assert.Equal(Text, U32(ClipServer.Parse(wire.Take()[1]).Body, 0));
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("Zelle\0")));
        Assert.Equal(["Zelle"], log.Texts);
        Assert.Empty(log.Images);

        channel.Receive(ClipServer.LongFormatList((Dib, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse([1, 2, 3]));                    // kein Bild
        channel.Receive(ClipServer.LongFormatList((Dib, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(null));                         // verweigert
        Assert.Empty(log.Images);
    }

    [Fact]
    public void Files_copied_on_the_remote_desktop_are_listed_and_fetched_in_pieces_when_asked()
    {
        var (channel, wire, bridge, log) = Started();
        var size = ClipboardBridge.MaxFileBytes / 16 + 10;                      // 4 MiB und ein Rest: mehrere Stuecke
        var list = Descriptor(new ClipFile("Ordner", 0, true), new ClipFile("Ordner\\Bericht.pdf", size), new ClipFile("..\\..\\boese.txt", 5), new ClipFile("Notiz.txt", 3));

        channel.Receive(ClipServer.LongFormatList((FileFormat, "FileGroupDescriptorW"), (0xC0BD, "FileContents")));
        Assert.Equal(FileFormat, U32(ClipServer.Parse(wire.Take()[1]).Body, 0));
        channel.Receive(ClipServer.DataResponse(list));

        var (batch, files) = Assert.Single(log.Files);
        Assert.Equal([new RemoteFile("Bericht.pdf", size), new RemoteFile("Notiz.txt", 3)], files);   // Ordner weg, der Pfad mit ".." weg
        var locked = ClipServer.Parse(wire.One());
        Assert.Equal(ClipServer.LockType, locked.Type);

        bridge.RequestFile(batch, 0);
        var data = new byte[size];
        new Random(7).NextBytes(data);
        for (var step = 0; step < 10; step++)
        {
            var request = ClipServer.Parse(wire.One());
            Assert.Equal(ClipServer.FileRequestType, request.Type);
            var stream = U32(request.Body, 0);
            Assert.Equal(1u, U32(request.Body, 1));                            // Nummer in der Liste des fernen Rechners (hinter dem Ordner)
            var offset = (int)U32(request.Body, 3);
            var count = (int)U32(request.Body, 5);
            Assert.Equal(U32(locked.Body, 0), U32(request.Body, 6));           // mit der Sperre der Liste
            channel.Receive(ClipServer.FileResponse(stream, data[offset..Math.Min(offset + count, size)]));
            if (log.Chunks[^1].Flags == ClipboardBridge.ChunkLast)
            {
                break;
            }
        }

        Assert.Equal(ClipboardBridge.ChunkLast, log.Chunks[^1].Flags);
        Assert.Equal(data, log.Chunks.SelectMany(chunk => chunk.Data).ToArray());
        Assert.True(log.Chunks.Count >= 4);
        Assert.All(log.Chunks, chunk => Assert.Equal((batch, 0), (chunk.Batch, chunk.Index)));
    }

    [Fact]
    public void A_file_of_unknown_size_asks_for_the_size_first_and_one_over_the_limit_is_not_fetched()
    {
        var (channel, wire, bridge, log) = Started();
        channel.Receive(ClipServer.LongFormatList((FileFormat, "FileGroupDescriptorW")));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Descriptor(new ClipFile("a.bin", -1), new ClipFile("riesig.iso", ClipboardBridge.MaxFileBytes + 1L))));
        wire.Take();
        var batch = log.Files[0].Batch;

        bridge.RequestFile(batch, 0);
        var sizeRequest = ClipServer.Parse(wire.One());
        Assert.Equal(1u, U32(sizeRequest.Body, 2));                            // Art: Groesse
        channel.Receive(ClipServer.FileResponse(U32(sizeRequest.Body, 0), ClipServer.U64(3)));
        var range = ClipServer.Parse(wire.One());
        Assert.Equal(3u, U32(range.Body, 5));
        channel.Receive(ClipServer.FileResponse(U32(range.Body, 0), [1, 2, 3]));
        Assert.Equal(new byte[] { 1, 2, 3 }, log.Chunks[^1].Data);
        Assert.Equal(ClipboardBridge.ChunkLast, log.Chunks[^1].Flags);

        bridge.RequestFile(batch, 1);
        wire.Nothing();
        Assert.Equal(ClipboardBridge.ChunkFailed, log.Chunks[^1].Flags);
        Assert.Contains("zu groß", Encoding.UTF8.GetString(log.Chunks[^1].Data));
    }

    [Fact]
    public void A_stale_batch_a_refusal_by_the_remote_and_a_cancel_end_the_download_with_a_reason()
    {
        var (channel, wire, bridge, log) = Started();
        channel.Receive(ClipServer.LongFormatList((FileFormat, "FileGroupDescriptorW")));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Descriptor(new ClipFile("a.txt", 10))));
        wire.Take();
        var batch = log.Files[0].Batch;

        bridge.RequestFile(batch + 1, 0);                                       // eine Liste, die es nicht mehr gibt
        Assert.Contains("geändert", Encoding.UTF8.GetString(log.Chunks[^1].Data));

        bridge.RequestFile(batch, 0);
        var request = ClipServer.Parse(wire.One());
        bridge.RequestFile(batch, 0);                                           // zweiter Download gleichzeitig
        Assert.Contains("schon ein Download", Encoding.UTF8.GetString(log.Chunks[^1].Data));
        channel.Receive(ClipServer.FileResponse(U32(request.Body, 0), null));
        Assert.Equal(ClipboardBridge.ChunkFailed, log.Chunks[^1].Flags);

        bridge.RequestFile(batch, 0);
        var again = ClipServer.Parse(wire.One());
        bridge.CancelFile(batch);
        var count = log.Chunks.Count;
        channel.Receive(ClipServer.FileResponse(U32(again.Body, 0), new byte[10]));
        Assert.Equal(count, log.Chunks.Count);                                  // nach dem Abbruch kommt nichts mehr
    }

    [Fact]
    public void A_new_file_list_unlocks_the_old_one()
    {
        var (channel, wire, _, log) = Started();
        for (var round = 0; round < 2; round++)
        {
            channel.Receive(ClipServer.LongFormatList((FileFormat, "FileGroupDescriptorW")));
            wire.Take();
            channel.Receive(ClipServer.DataResponse(Descriptor(new ClipFile("a.txt", 10))));
        }

        var types = wire.Take().Select(m => ClipServer.Parse(m).Type).ToArray();
        Assert.Equal([ClipServer.LockType, ClipServer.UnlockType], types);        // die neue Liste wird gesperrt, danach die alte freigegeben
        Assert.Equal(2, log.Files.Count);
    }

    // ---------------------------------------------------------------- Browser -> ferner Rechner

    [Fact]
    public void An_image_from_the_browser_is_announced_as_a_dib_and_handed_over_when_the_remote_pastes()
    {
        var (channel, wire, bridge, log) = Started();
        var bmp = DeviceIndependentBitmap.ToBmp(TinyDib())!;

        Upload(bridge, 5, ClipboardBridge.KindImage, 0, 1, bmp, "", chunk: 20);
        Assert.Equal([(5u, ClipboardBridge.ResultOk, "")], log.Results);
        var announce = ClipServer.Parse(wire.One());
        Assert.Equal(ClipServer.FormatListType, announce.Type);
        Hex.Same("08 00 00 00 00 00", announce.Body);
        channel.Receive(ClipServer.FormatListResponse(true));

        channel.Receive(ClipServer.DataRequest(Dib));
        var (type, flags, data) = ClipServer.Parse(wire.One());
        Assert.Equal((ClipServer.DataResponseType, ClipServer.Ok), (type, flags));
        Assert.Equal(TinyDib(), data);

        channel.Receive(ClipServer.DataRequest(Text));                          // Text gibt es dazu nicht
        Assert.Equal(ClipServer.Fail, ClipServer.Parse(wire.One()).Flags);
    }

    [Fact]
    public void A_file_from_the_browser_is_offered_as_a_file_list_and_read_by_the_remote_in_ranges()
    {
        var (channel, wire, bridge, log) = Started();
        var content = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();

        Upload(bridge, 9, ClipboardBridge.KindFile, 0, 1, content, "..\\..\\Bericht: 1?.txt");
        Assert.Equal(ClipboardBridge.ResultOk, log.Results[^1].Code);
        var announce = ClipServer.Parse(wire.One());
        Assert.Equal(ClipServer.FormatListType, announce.Type);
        channel.Receive(ClipServer.FormatListResponse(true));

        channel.Receive(ClipServer.DataRequest(FileGroupDescriptor.DefaultFormatId));
        var list = FileGroupDescriptor.Decode(ClipServer.Parse(wire.One()).Body);
        var file = Assert.Single(list!);
        Assert.Equal(("Bericht_ 1_.txt", 5000), (file.Name, file.Size));          // der Pfad ist weg, verbotene Zeichen sind ersetzt

        channel.Receive(ClipServer.FileSizeRequest(1, 0));
        Assert.Equal(5000ul, BinaryPrimitives.ReadUInt64LittleEndian(ClipServer.Parse(wire.One()).Body.AsSpan(4)));
        channel.Receive(ClipServer.FileRangeRequest(2, 0, 4990, 65536));
        Assert.Equal(content[4990..], ClipServer.Parse(wire.One()).Body[4..]);   // am Ende kuerzer: Ende der Datei
        channel.Receive(ClipServer.FileRangeRequest(3, 0, 6000, 100));
        Assert.Equal(4, ClipServer.Parse(wire.One()).Body.Length);              // hinter dem Ende: leer
        channel.Receive(ClipServer.FileRangeRequest(4, 3, 0, 10));              // eine Datei, die es nicht gibt
        Assert.Equal(ClipServer.Fail, ClipServer.Parse(wire.One()).Flags);
    }

    [Fact]
    public void Several_files_become_one_list_when_the_last_one_has_arrived()
    {
        var (channel, wire, bridge, log) = Started();

        Upload(bridge, 1, ClipboardBridge.KindFile, 0, 2, [1, 2, 3], "a.txt");
        wire.Nothing();                                                         // erst die ganze Reihe
        Upload(bridge, 2, ClipboardBridge.KindFile, 1, 2, [4, 5], "b.txt");
        Assert.Equal(ClipServer.FormatListType, ClipServer.Parse(wire.One()).Type);
        channel.Receive(ClipServer.FormatListResponse(true));
        channel.Receive(ClipServer.DataRequest(FileGroupDescriptor.DefaultFormatId));
        Assert.Equal(["a.txt", "b.txt"], FileGroupDescriptor.Decode(ClipServer.Parse(wire.One()).Body)!.Select(f => f.Name));
        Assert.All(log.Results, r => Assert.Equal(ClipboardBridge.ResultOk, r.Code));
    }

    [Theory]
    [InlineData("x.exe", ".exe")]
    [InlineData("Setup.CMD", ".cmd")]
    [InlineData("start.desktop", ".desktop")]
    [InlineData("link.lnk", ".lnk")]
    public void Files_that_would_run_when_opened_are_refused_with_the_reason(string name, string extension)
    {
        var (_, wire, bridge, log) = Started();

        Upload(bridge, 3, ClipboardBridge.KindFile, 0, 1, [1, 2, 3], name);
        var result = Assert.Single(log.Results);
        Assert.Equal((3u, ClipboardBridge.ResultNotAllowed), (result.Id, result.Code));
        Assert.Contains(extension, result.Message);
        wire.Nothing();
    }

    [Fact]
    public void Limits_on_size_count_names_and_the_declared_length_are_enforced()
    {
        var (_, wire, bridge, log) = Started();

        bridge.BeginTransfer(1, ClipboardBridge.KindFile, 0, 1, ClipboardBridge.MaxFileBytes + 1L, "a.bin");
        Assert.Equal((ClipboardBridge.ResultTooBig, "Datei zu groß (höchstens 64 MB)."), (log.Results[^1].Code, log.Results[^1].Message));
        bridge.BeginTransfer(2, ClipboardBridge.KindFile, 0, ClipboardBridge.MaxFiles + 1, 10, "a.bin");
        Assert.Contains("Zu viele Dateien", log.Results[^1].Message);
        bridge.BeginTransfer(3, ClipboardBridge.KindFile, 0, 1, 10, "...");
        Assert.Equal(ClipboardBridge.ResultNotAllowed, log.Results[^1].Code);
        bridge.BeginTransfer(4, ClipboardBridge.KindFile, 0, 1, 0, "leer.txt");
        Assert.Contains("leer", log.Results[^1].Message);
        bridge.BeginTransfer(5, ClipboardBridge.KindImage, 0, 1, ClipboardBridge.MaxImageBytes + 1L, "");
        Assert.Equal(ClipboardBridge.ResultTooBig, log.Results[^1].Code);
        bridge.BeginTransfer(6, 9, 0, 1, 10, "");
        Assert.Equal(ClipboardBridge.ResultOther, log.Results[^1].Code);

        // zusammen mehr als 64 MB in einer Reihe
        bridge.BeginTransfer(7, ClipboardBridge.KindFile, 0, 2, ClipboardBridge.MaxFileBytes, "gross.bin");
        bridge.CancelTransfer(7);

        // mehr Daten als angekuendigt, ein zu grosses Stueck
        bridge.BeginTransfer(8, ClipboardBridge.KindFile, 0, 1, 10, "a.bin");
        bridge.TransferChunk(8, new byte[11]);
        Assert.Contains("Mehr Daten", log.Results[^1].Message);
        bridge.EndTransfer(8);                                                  // die Uebertragung ist verworfen: nichts wird abgelegt
        bridge.BeginTransfer(9, ClipboardBridge.KindFile, 0, 1, 1_000_000, "b.bin");
        bridge.TransferChunk(9, new byte[ClipboardBridge.MaxChunkBytes + 1]);
        Assert.Contains("Mehr Daten", log.Results[^1].Message);

        // zu kurz beendet; ein BMP, das keins ist
        bridge.BeginTransfer(10, ClipboardBridge.KindFile, 0, 1, 10, "c.bin");
        bridge.TransferChunk(10, new byte[4]);
        bridge.EndTransfer(10);
        Assert.Contains("unvollständig", log.Results[^1].Message);
        Upload(bridge, 11, ClipboardBridge.KindImage, 0, 1, new byte[100], "");
        Assert.Contains("Kein gültiges Bild", log.Results[^1].Message);
        wire.Nothing();

        // die zweite Datei einer Reihe, die nicht begonnen wurde
        bridge.BeginTransfer(12, ClipboardBridge.KindFile, 1, 2, 10, "d.bin");
        Assert.Contains("Reihenfolge", log.Results[^1].Message);
    }

    [Fact]
    public void A_second_file_that_pushes_the_batch_over_the_limit_is_refused()
    {
        var (_, wire, bridge, log) = Started();

        Upload(bridge, 1, ClipboardBridge.KindFile, 0, 2, new byte[100], "a.bin");
        bridge.BeginTransfer(2, ClipboardBridge.KindFile, 1, 2, ClipboardBridge.MaxBatchBytes, "b.bin");
        Assert.Equal(ClipboardBridge.ResultTooBig, log.Results[^1].Code);
        wire.Nothing();
    }

    [Fact]
    public void A_cancelled_transfer_leaves_the_clipboard_alone_and_a_stalled_one_ends_by_itself()
    {
        var (_, wire, bridge, log) = Started(idle: TimeSpan.FromMilliseconds(80));

        bridge.BeginTransfer(1, ClipboardBridge.KindFile, 0, 1, 10, "a.bin");
        bridge.TransferChunk(1, new byte[10]);
        bridge.CancelTransfer(1);
        bridge.EndTransfer(1);
        Assert.Empty(log.Results);
        wire.Nothing();

        bridge.BeginTransfer(2, ClipboardBridge.KindFile, 0, 1, 10, "b.bin");
        var until = Environment.TickCount64 + 3000;
        while (log.Results.Count == 0 && Environment.TickCount64 < until)
        {
            Thread.Sleep(20);
        }

        var result = Assert.Single(log.Results);
        Assert.Equal((2u, ClipboardBridge.ResultOther), (result.Id, result.Code));
        Assert.Contains("abgebrochen", result.Message);
        bridge.EndTransfer(2);
        Assert.Single(log.Results);
    }

    [Fact]
    public void Without_a_clipboard_channel_or_file_support_the_browser_is_told_why()
    {
        var channel = new ClipboardChannel();
        _ = new Wire(channel);
        var log = new Log();
        var bridge = new ClipboardBridge(channel, new ClipboardSink { Result = (id, code, message) => log.Results.Add((id, code, message)) });

        bridge.BeginTransfer(1, ClipboardBridge.KindFile, 0, 1, 10, "a.bin");     // der Server ist noch nicht bereit
        Assert.Equal((ClipboardBridge.ResultNoClipboard, "Die VM hat keine Zwischenablage (Kanal fehlt)."), (log.Results[^1].Code, log.Results[^1].Message));

        var (_, _, plain, plainLog) = Started(serverFlags: 0x2);                  // bereit, aber ohne Dateien
        plain.BeginTransfer(2, ClipboardBridge.KindFile, 0, 1, 10, "a.bin");
        Assert.Contains("keine Dateien", plainLog.Results[^1].Message);
        plain.BeginTransfer(3, ClipboardBridge.KindImage, 0, 1, 10, "");          // ein Bild geht auch dann
        Assert.Single(plainLog.Results);
    }

    [Fact]
    public void What_we_just_announced_coming_back_as_a_new_copy_is_not_sent_to_the_browser()
    {
        var (channel, wire, bridge, log) = Started();
        Upload(bridge, 1, ClipboardBridge.KindFile, 0, 1, [1, 2, 3], "a.txt");
        wire.Take();
        channel.Receive(ClipServer.FormatListResponse(true));

        channel.Receive(ClipServer.LongFormatList((FileFormat, "FileGroupDescriptorW")));
        Assert.Single(wire.Take());                                             // nur die Bestaetigung
        Assert.Empty(log.Files);
    }

    [Fact]
    public void Text_from_the_browser_replaces_an_image_the_browser_gave_before()
    {
        var (channel, wire, bridge, _) = Started();
        Upload(bridge, 1, ClipboardBridge.KindImage, 0, 1, DeviceIndependentBitmap.ToBmp(TinyDib())!, "");
        wire.Take();
        channel.Receive(ClipServer.FormatListResponse(true));

        bridge.SetLocalText("neu");
        wire.Take();
        channel.Receive(ClipServer.FormatListResponse(true));
        channel.Receive(ClipServer.DataRequest(Dib));
        Assert.Equal(ClipServer.Fail, ClipServer.Parse(wire.One()).Flags);
    }

    // ---------------------------------------------------------------- Namen

    [Theory]
    [InlineData("bild.png", "bild.png")]
    [InlineData("C:\\Users\\x\\Bericht.pdf", "Bericht.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("Bericht. ", "Bericht")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("rechnung\u202Efdp.exe", "rechnung_fdp.exe")]
    [InlineData("  .hidden", ".hidden")]
    public void Names_become_plain_file_names(string raw, string expected) => Assert.Equal(expected, ClipboardNames.Sanitize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("dir/")]
    [InlineData("\\\\server\\")]
    [InlineData("???")]
    public void Names_with_nothing_left_are_refused(string? raw) => Assert.Null(ClipboardNames.Sanitize(raw));

    [Fact]
    public void Long_names_keep_their_extension_and_fit_the_limit()
    {
        var name = ClipboardNames.Sanitize(new string('a', 400) + ".pdf")!;
        Assert.Equal(ClipboardNames.MaxLength, name.Length);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void Blocked_extensions_are_found_regardless_of_case()
    {
        Assert.Equal(".exe", ClipboardNames.BlockedExtension("Setup.EXE"));
        Assert.Equal(".desktop", ClipboardNames.BlockedExtension("a.b.desktop"));
        Assert.Null(ClipboardNames.BlockedExtension("foto.jpg"));
        Assert.Null(ClipboardNames.BlockedExtension("exe"));
    }
}
