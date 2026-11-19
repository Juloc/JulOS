using System.Buffers.Binary;
using System.IO.Compression;
using JulOS.Integrations.Display.Rdp.Channels;
using JulOS.Integrations.Display.Rdp.Channels.Tests;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Ordner, viele und grosse Dateien in der Zwischenablage: Ablage auf der Platte, Pfade, Grenzen, ZIP, Einstellung.</summary>
public sealed class ClipboardFilesTests
{
    private sealed class Log
    {
        public List<(uint Id, byte Code, string Message)> Results { get; } = [];
    }

    private static (ClipboardChannel Channel, Wire Wire, ClipboardBridge Bridge, Log Log, ClipboardOptions Options) Started(long megabytes = 8)
    {
        var channel = new ClipboardChannel();
        var wire = new Wire(channel);
        var log = new Log();
        var options = ClipboardTransferTests.SmallLimits(megabytes);
        var bridge = new ClipboardBridge(channel, new ClipboardSink { Result = (id, code, message) => log.Results.Add((id, code, message)) }, null, options);
        channel.Receive(ClipServer.Capabilities(0x3E));
        channel.Receive(ClipServer.MonitorReady());
        channel.Receive(ClipServer.FormatListResponse(true));
        wire.Take();
        return (channel, wire, bridge, log, options);
    }

    private static uint Id;

    // Ein Eintrag: Datei (kind 2) oder leerer Ordner (kind 3); Rueckgabe: das Ergebnis des Servers
    private static (byte Code, string Message) Send(ClipboardBridge bridge, Log log, byte kind, int index, int count, byte[] data, string path, int chunk = 4096)
    {
        var id = ++Id;
        var before = log.Results.Count;
        bridge.BeginTransfer(id, kind, index, count, data.Length, path);
        if (log.Results.Count > before)
        {
            return (log.Results[^1].Code, log.Results[^1].Message);
        }

        for (var at = 0; at < data.Length; at += chunk)
        {
            bridge.TransferChunk(id, data.AsSpan(at, Math.Min(chunk, data.Length - at)));
            if (log.Results.Count > before)
            {
                return (log.Results[^1].Code, log.Results[^1].Message);
            }
        }

        bridge.EndTransfer(id);
        return (log.Results[^1].Code, log.Results[^1].Message);
    }

    private static IReadOnlyList<ClipFile> PasteList(ClipboardChannel channel, Wire wire)
    {
        channel.Receive(ClipServer.FormatListResponse(true));
        wire.Take();
        channel.Receive(ClipServer.DataRequest(FileGroupDescriptor.DefaultFormatId));
        return FileGroupDescriptor.Decode(ClipServer.Parse(wire.One()).Body)!;
    }

    private static string[] Files(string root) => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];

    // ---------------------------------------------------------------- Browser -> ferner Rechner

    [Fact]
    public void A_folder_with_subfolders_and_an_empty_folder_becomes_a_list_with_every_folder_before_its_contents()
    {
        var (channel, wire, bridge, log, _) = Started();
        var entries = new (byte Kind, string Path, byte[] Data)[]
        {
            (2, "Projekt/src/main.txt", [1, 2, 3]),
            (2, "Projekt/readme.txt", [4, 5]),
            (3, "Projekt/leer", []),
            (2, "lose.txt", [6]),
            (2, "Projekt/src/leer.txt", []),             // eine leere Datei in einem Ordner ist erlaubt
        };
        for (var i = 0; i < entries.Length; i++)
        {
            var (code, message) = Send(bridge, log, entries[i].Kind, i, entries.Length, entries[i].Data, entries[i].Path);
            Assert.True(code == ClipboardBridge.ResultOk, message);
        }

        Assert.Equal(ClipServer.FormatListType, ClipServer.Parse(wire.One()).Type);
        var list = PasteList(channel, wire);
        Assert.Equal(["lose.txt", "Projekt", "Projekt\\leer", "Projekt\\readme.txt", "Projekt\\src", "Projekt\\src\\leer.txt", "Projekt\\src\\main.txt"], list.Select(f => f.Name));
        Assert.Equal([false, true, true, false, true, false, false], list.Select(f => f.IsDirectory));

        // jede Datei ist ueber die Nummer in der Liste lesbar, auch in Stuecken
        channel.Receive(ClipServer.FileRangeRequest(1, 6, 1, 100));
        Assert.Equal(new byte[] { 2, 3 }, ClipServer.Parse(wire.One()).Body[4..]);
        channel.Receive(ClipServer.FileSizeRequest(2, 5, null));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(ClipServer.Parse(wire.One()).Body.AsSpan(4)));
        channel.Receive(ClipServer.FileRangeRequest(3, 1, 0, 10));              // ein Ordner hat keinen Inhalt
        Assert.Equal(ClipServer.Fail, ClipServer.Parse(wire.One()).Flags);
    }

    [Theory]
    [InlineData("../boese.txt")]
    [InlineData("a/../../boese.txt")]
    [InlineData("..\\boese.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x.txt")]
    [InlineData("C:\\Windows\\x.txt")]
    [InlineData("C:x.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a/")]
    [InlineData("a/b/..")]
    [InlineData("a/ ./b.txt")]
    [InlineData("")]
    public void Path_tricks_are_refused_outright_and_nothing_is_written(string path)
    {
        var (_, wire, bridge, log, options) = Started();

        var (code, message) = Send(bridge, log, 2, 0, 1, [1, 2, 3], path);

        Assert.Equal(ClipboardBridge.ResultNotAllowed, code);
        Assert.Contains("Pfad", message);
        Assert.Empty(Files(options.TempRoot));
        wire.Nothing();
    }

    [Fact]
    public void A_path_trick_in_the_middle_of_a_batch_stops_it_and_a_name_that_is_too_deep_or_too_long_is_refused()
    {
        var (_, wire, bridge, log, _) = Started();

        Assert.Equal(ClipboardBridge.ResultOk, Send(bridge, log, 2, 0, 2, [1], "ok/a.txt").Code);
        Assert.Equal(ClipboardBridge.ResultNotAllowed, Send(bridge, log, 2, 1, 2, [2], "ok/../../b.txt").Code);
        wire.Nothing();                                                         // die Reihe ist nicht komplett: nichts angekuendigt

        Assert.Equal(ClipboardBridge.ResultNotAllowed, Send(bridge, log, 2, 0, 1, [1], string.Join('/', Enumerable.Repeat("d", ClipboardNames.MaxDepth + 1)) + "/x.txt").Code);
        Assert.Equal(ClipboardBridge.ResultNotAllowed, Send(bridge, log, 2, 0, 1, [1], new string('a', ClipboardNames.MaxPathLength + 1) + ".txt").Code);
        Assert.Equal(ClipboardBridge.ResultNotAllowed, Send(bridge, log, 2, 0, 2, [1], "d/x.exe").Code);       // auch im Ordner gilt die Sperre fuer Programme
    }

    [Fact]
    public void Two_hundred_files_are_taken_and_the_two_hundred_and_first_is_refused()
    {
        var (channel, wire, bridge, log, _) = Started();
        const int count = ClipboardOptions.MaxFiles + 1;

        for (var i = 0; i < ClipboardOptions.MaxFiles; i++)
        {
            var (code, message) = Send(bridge, log, 2, i, count, [(byte)i], $"viele/datei{i:D3}.txt");
            Assert.True(code == ClipboardBridge.ResultOk, message);
        }

        var (tooMany, text) = Send(bridge, log, 2, ClipboardOptions.MaxFiles, count, [1], "viele/eine-zu-viel.txt");
        Assert.Equal(ClipboardBridge.ResultTooBig, tooMany);
        Assert.Contains("200", text);
        wire.Nothing();

        // genau 200 gehen durch
        for (var i = 0; i < ClipboardOptions.MaxFiles; i++)
        {
            Assert.Equal(ClipboardBridge.ResultOk, Send(bridge, log, 2, i, ClipboardOptions.MaxFiles, [(byte)i], $"viele/datei{i:D3}.txt").Code);
        }

        Assert.Equal(ClipServer.FormatListType, ClipServer.Parse(wire.One()).Type);
        Assert.Equal(ClipboardOptions.MaxFiles + 1, PasteList(channel, wire).Count);                    // dazu der Ordner
    }

    [Fact]
    public void A_big_file_goes_to_disk_in_chunks_not_into_memory_and_is_read_from_there_in_ranges()
    {
        var (channel, wire, bridge, log, options) = Started(megabytes: 8);
        var content = new byte[6 * 1024 * 1024 + 123];                          // mehrere Stuecke, ueber dem, was ein Stueck fasst
        new Random(3).NextBytes(content);

        var id = ++Id;
        bridge.BeginTransfer(id, ClipboardBridge.KindFile, 0, 1, content.Length, "gross.bin");
        var onDisk = Files(options.TempRoot);
        Assert.Single(onDisk);                                                  // die Datei liegt schon beim Anfang im Ablageordner
        for (var at = 0; at < content.Length; at += ClipboardBridge.MaxChunkBytes)
        {
            bridge.TransferChunk(id, content.AsSpan(at, Math.Min(ClipboardBridge.MaxChunkBytes, content.Length - at)));
        }

        Assert.True(new FileInfo(onDisk[0]).Length > 0);                        // was angekommen ist, steht auf der Platte
        bridge.EndTransfer(id);
        Assert.Equal(ClipboardBridge.ResultOk, log.Results[^1].Code);
        Assert.Equal(content.Length, new FileInfo(Files(options.TempRoot).Single()).Length);

        wire.Take();
        var list = PasteList(channel, wire);
        Assert.Equal(content.Length, Assert.Single(list).Size);
        channel.Receive(ClipServer.FileRangeRequest(1, 0, (ulong)content.Length - 100, 1024 * 1024));
        Assert.Equal(content[^100..], ClipServer.Parse(wire.One()).Body[4..]);
        channel.Receive(ClipServer.FileRangeRequest(2, 0, 1_000_000, 70_000));
        Assert.Equal(content[1_000_000..1_070_000], ClipServer.Parse(wire.One()).Body[4..]);
    }

    [Fact]
    public void The_limit_per_file_and_for_the_whole_paste_follows_the_setting_and_a_new_paste_clears_the_old_files()
    {
        var (_, wire, bridge, log, options) = Started(megabytes: 1);

        var id = ++Id;
        bridge.BeginTransfer(id, ClipboardBridge.KindFile, 0, 1, 1024 * 1024 + 1, "zu-gross.bin");
        Assert.Equal((ClipboardBridge.ResultTooBig, "Datei zu groß (höchstens 1 MB)."), (log.Results[^1].Code, log.Results[^1].Message));
        Assert.Empty(Files(options.TempRoot));

        // zusammen mehr als die Grenze
        Assert.Equal(ClipboardBridge.ResultOk, Send(bridge, log, 2, 0, 2, new byte[600 * 1024], "a.bin", 100_000).Code);
        Assert.Equal(ClipboardBridge.ResultTooBig, Send(bridge, log, 2, 1, 2, new byte[500 * 1024], "b.bin", 100_000).Code);
        wire.Nothing();

        // eine Ablage, die vollstaendig ist, ersetzt die vorige und raeumt deren Dateien auf
        Assert.Equal(ClipboardBridge.ResultOk, Send(bridge, log, 2, 0, 1, [1, 2, 3], "neu.txt").Code);
        Assert.Single(Files(options.TempRoot));
        Assert.Equal(ClipboardBridge.ResultOk, Send(bridge, log, 2, 0, 1, [4, 5], "neuer.txt").Code);
        Assert.Equal(2, new FileInfo(Files(options.TempRoot).Single()).Length);

        bridge.SetLocalText("Text ersetzt Dateien");
        Assert.Empty(Files(options.TempRoot));
    }

    [Fact]
    public void A_cancelled_transfer_a_stalled_one_and_the_end_of_the_session_leave_no_files_behind()
    {
        var (_, _, bridge, log, options) = Started();

        var id = ++Id;
        bridge.BeginTransfer(id, ClipboardBridge.KindFile, 0, 1, 20000, "a.bin");
        bridge.TransferChunk(id, new byte[5000]);
        bridge.CancelTransfer(id);
        Assert.Empty(Files(options.TempRoot));

        Assert.Equal(ClipboardBridge.ResultOk, Send(bridge, log, 2, 0, 2, [1, 2, 3], "x/ganz.txt").Code);       // Reihe unvollstaendig
        id = ++Id;
        bridge.BeginTransfer(id, ClipboardBridge.KindFile, 1, 2, 10, "x/zweite.txt");
        Assert.NotEmpty(Files(options.TempRoot));
        bridge.Dispose();
        Assert.Empty(Files(options.TempRoot));
    }

    [Fact]
    public void A_name_twice_in_one_paste_is_refused()
    {
        var (_, _, bridge, log, _) = Started();

        Assert.Equal(ClipboardBridge.ResultOk, Send(bridge, log, 2, 0, 3, [1], "a/b.txt").Code);
        Assert.Equal(ClipboardBridge.ResultNotAllowed, Send(bridge, log, 2, 1, 3, [2], "A/B.txt").Code);        // Windows kennt keinen Unterschied
    }

    [Fact]
    public void Old_stash_folders_of_a_crashed_server_are_swept_but_new_ones_stay()
    {
        var options = ClipboardTransferTests.SmallLimits();
        var old = Path.Combine(options.TempRoot, "alt");
        var recent = Path.Combine(options.TempRoot, "neu");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-13));

        ClipboardBridge.SweepOld(options.TempRoot);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        ClipboardBridge.SweepOld(Path.Combine(options.TempRoot, "gibt-es-nicht"));       // kein Fehler
    }

    // ---------------------------------------------------------------- Einstellung und Namen

    [Theory]
    [InlineData(null, 2048L)]
    [InlineData("", 2048L)]
    [InlineData("Unsinn", 2048L)]
    [InlineData("0", 2048L)]
    [InlineData("-5", 2048L)]
    [InlineData("512", 512L)]
    [InlineData(" 4096 ", 4096L)]
    [InlineData("999999999", 65536L)]
    public void The_limit_comes_from_JULOS_CLIP_MAX_MB_with_2_GiB_as_the_default(string? value, long megabytes)
    {
        Assert.Equal(megabytes * 1024 * 1024, ClipboardOptions.Parse(value, null).MaxFileBytes);
        Assert.Equal(ClipboardOptions.DefaultRoot, ClipboardOptions.Parse(value, "  ").TempRoot);
        Assert.Equal("/var/clip", ClipboardOptions.Parse(value, " /var/clip ").TempRoot);
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("Ordner/Unter/a.txt", "Ordner|Unter|a.txt")]
    [InlineData("Ordner\\Unter\\a.txt", "Ordner|Unter|a.txt")]
    [InlineData("a:b/c?.txt", null)]                      // "a:" sieht aus wie ein Laufwerk
    [InlineData("Ordner/c?.txt", "Ordner|c_.txt")]
    [InlineData("CON/nul.txt", "_CON|_nul.txt")]
    [InlineData("Ordner /x", "Ordner|x")]
    public void Paths_become_clean_parts_or_nothing(string raw, string? expected) =>
        Assert.Equal(expected, ClipboardNames.SafePath(raw) is { } parts ? string.Join('|', parts) : null);

    // ---------------------------------------------------------------- ZIP

    private static async Task<byte[]> ZipAsync(Func<ClipboardZipWriter, Task> fill)
    {
        var output = new MemoryStream();
        var zip = new ClipboardZipWriter(output);
        await fill(zip);
        await zip.FinishAsync();
        return output.ToArray();
    }

    [Fact]
    public async Task The_streamed_zip_reads_back_with_names_folders_and_contents_and_unknown_sizes_become_zip64()
    {
        var big = new byte[300_000];
        new Random(5).NextBytes(big);
        var bytes = await ZipAsync(async zip =>
        {
            await zip.AddDirectoryAsync("Ordner", new DateTime(2024, 5, 17, 10, 20, 30, DateTimeKind.Utc));
            await zip.BeginFileAsync("Ordner/Größe ü.txt", 6, new DateTime(2024, 5, 17, 10, 20, 30, DateTimeKind.Utc));
            await zip.WriteFileAsync("abc"u8.ToArray());
            await zip.WriteFileAsync("def"u8.ToArray());
            await zip.EndFileAsync();
            await zip.BeginFileAsync("gross.bin", -1);                          // Groesse unbekannt: ZIP64
            for (var at = 0; at < big.Length; at += 70_000)
            {
                await zip.WriteFileAsync(big.AsMemory(at, Math.Min(70_000, big.Length - at)));
            }

            await zip.EndFileAsync();
            await zip.BeginFileAsync("leer.txt", 0);
            await zip.EndFileAsync();
        });

        using var archive = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(["Ordner/", "Ordner/Größe ü.txt", "gross.bin", "leer.txt"], archive.Entries.Select(e => e.FullName));
        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        Assert.Equal("abcdef", Read("Ordner/Größe ü.txt"));
        using (var stream = archive.GetEntry("gross.bin")!.Open())
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(big, copy.ToArray());
        }

        Assert.Equal(300_000, archive.GetEntry("gross.bin")!.Length);
        Assert.Equal(0, archive.GetEntry("leer.txt")!.Length);
        Assert.Equal(new DateTime(2024, 5, 17, 10, 20, 30), archive.GetEntry("Ordner/Größe ü.txt")!.LastWriteTime.DateTime);       // die Uhrzeit steht wie in der Datei (ZIP kennt keine Zeitzone)
    }

    [Fact]
    public void The_checksum_is_the_standard_crc32_and_works_in_pieces()
    {
        Assert.Equal(0xCBF43926u, ClipboardZipWriter.Crc32.Update(0, "123456789"u8));
        var first = ClipboardZipWriter.Crc32.Update(0, "12345"u8);
        Assert.Equal(0xCBF43926u, ClipboardZipWriter.Crc32.Update(first, "6789"u8));
        Assert.Equal(0u, ClipboardZipWriter.Crc32.Update(0, []));
    }

    [Fact]
    public async Task A_zip_with_more_than_65535_entries_gets_the_zip64_end_and_still_reads()
    {
        var bytes = await ZipAsync(async zip =>
        {
            for (var i = 0; i < 70_000; i++)
            {
                await zip.AddDirectoryAsync("d" + i);
            }
        });

        using var archive = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(70_000, archive.Entries.Count);
    }

    [Fact]
    public async Task The_zip_writer_refuses_a_second_file_before_the_first_is_ended_and_a_file_that_outgrows_its_announced_size_class()
    {
        var zip = new ClipboardZipWriter(new MemoryStream());
        await zip.BeginFileAsync("a", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => zip.BeginFileAsync("b", 1));
        await zip.EndFileAsync();
        await zip.FinishAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => zip.AddDirectoryAsync("c"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => zip.EndFileAsync());
    }
}
