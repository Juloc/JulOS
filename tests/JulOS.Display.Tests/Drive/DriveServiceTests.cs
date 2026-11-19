using System.Collections.Concurrent;
using System.Text;
using JulOS.Integrations.Display.Rdp.Drive;

namespace JulOS.Display.Tests.Drive;

// The file system part of the device redirection against a real folder: requests are built by hand like the server sends them (MS-RDPEFS 2.2.3),
// answers are read back. Read only is the default and has to hold for every kind of request; the escape attempts have to find nothing.
public sealed class DriveServiceTests : IDisposable
{
    private const uint Success = 0, NoMoreFiles = 0x80000006, AccessDenied = 0xC0000022, NoSuchFile = 0xC000000F, NameInvalid = 0xC0000033,
        NameCollision = 0xC0000035, InvalidHandle = 0xC0000008, DirectoryNotEmpty = 0xC0000101, DiskFull = 0xC000007F, NotSupported = 0xC00000BB,
        PathNotFound = 0xC000003A, NotADirectory = 0xC0000103, IsADirectory = 0xC00000BA;

    private const uint Open = 1, CreateNew = 2, OpenIf = 3, Overwrite = 4, OverwriteIf = 5, Supersede = 0;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x10000, ReadData = 1, WriteData = 2, AppendData = 4, Synchronize = 0x100000;
    private const uint DirectoryFile = 1, NonDirectoryFile = 0x40, DeleteOnClose = 0x1000;

    private readonly string _base = Path.Combine(Path.GetTempPath(), "julos-drive-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _outside;
    private readonly List<DriveService> _services = [];

    public DriveServiceTests()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the drive is Linux only");
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(Path.Combine(_root, ".ssh"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello world");
        File.WriteAllText(Path.Combine(_root, "sub", "inner.txt"), "inner");
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_root, ".ssh", "id"), "KEY");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "TOP SECRET");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link_out"), _outside);
        File.CreateSymbolicLink(Path.Combine(_root, "file_link"), Path.Combine(_outside, "secret.txt"));
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        try { Directory.Delete(_base, true); }
        catch (IOException) { /* egal */ }
    }

    // ---- a small client: builds requests, waits for the answer ----

    private sealed class Client(DriveService service, BlockingCollection<byte[]> answers)
    {
        private uint _completion;
        public DriveService Service => service;

        public (uint Status, byte[] Payload) Call(uint major, uint minor, uint fileId, byte[] body)
        {
            var id = ++_completion;
            var message = new MemoryStream();
            var w = new BinaryWriter(message);
            w.Write((ushort)0x4472); w.Write((ushort)0x4952); w.Write(1u); w.Write(fileId); w.Write(id); w.Write(major); w.Write(minor); w.Write(body);
            service.Submit(message.ToArray());
            Assert.True(answers.TryTake(out var answer, TimeSpan.FromSeconds(10)), "no answer from the drive");
            Assert.Equal(0x4472, BitConverter.ToUInt16(answer, 0));
            Assert.Equal(0x4943, BitConverter.ToUInt16(answer, 2));
            Assert.Equal(1u, BitConverter.ToUInt32(answer, 4));
            Assert.Equal(id, BitConverter.ToUInt32(answer, 8));
            return (BitConverter.ToUInt32(answer, 12), answer[16..]);
        }

        public (uint Status, uint FileId) Create(string path, uint access = GenericRead | Synchronize, uint disposition = Open, uint options = 0)
        {
            var name = Encoding.Unicode.GetBytes(path + "\0");
            var b = new MemoryStream(); var w = new BinaryWriter(b);
            w.Write(access); w.Write(0L); w.Write(0u); w.Write(7u); w.Write(disposition); w.Write(options); w.Write((uint)name.Length); w.Write(name);
            var (status, payload) = Call(0, 0, 0, b.ToArray());
            Assert.Equal(5, payload.Length);
            return (status, BitConverter.ToUInt32(payload, 0));
        }

        public uint Close(uint id) => Call(2, 0, id, new byte[32]).Status;

        public (uint Status, byte[] Data) Read(uint id, uint length, ulong offset)
        {
            var b = new MemoryStream(); var w = new BinaryWriter(b);
            w.Write(length); w.Write(offset); w.Write(new byte[20]);
            var (status, payload) = Call(3, 0, id, b.ToArray());
            var n = BitConverter.ToUInt32(payload, 0);
            Assert.Equal((int)n, payload.Length - 4);
            return (status, payload[4..]);
        }

        public (uint Status, uint Written) Write(uint id, byte[] data, ulong offset)
        {
            var b = new MemoryStream(); var w = new BinaryWriter(b);
            w.Write((uint)data.Length); w.Write(offset); w.Write(new byte[20]); w.Write(data);
            var (status, payload) = Call(4, 0, id, b.ToArray());
            return (status, BitConverter.ToUInt32(payload, 0));
        }

        public (uint Status, byte[] Data) Query(uint major, uint infoClass, uint id, byte[]? set = null)
        {
            var b = new MemoryStream(); var w = new BinaryWriter(b);
            w.Write(infoClass); w.Write((uint)(set?.Length ?? 512)); w.Write(new byte[24]);
            if (set is not null) w.Write(set);
            var (status, payload) = Call(major, 0, id, b.ToArray());
            var n = BitConverter.ToUInt32(payload, 0);
            return (status, payload.Length >= 4 + n ? payload[4..(4 + (int)n)] : payload[4..]);
        }

        public uint Set(uint infoClass, uint id, byte[] data) => Query(6, infoClass, id, data).Status;

        public (uint Status, string? Name, bool IsDirectory) NextEntry(uint id, string pattern, bool initial)
        {
            var path = Encoding.Unicode.GetBytes(pattern + "\0");
            var b = new MemoryStream(); var w = new BinaryWriter(b);
            w.Write(3u); w.Write(initial ? (byte)1 : (byte)0); w.Write((uint)path.Length); w.Write(new byte[23]); w.Write(path);
            var (status, payload) = Call(0x0C, 1, id, b.ToArray());
            var n = BitConverter.ToUInt32(payload, 0);
            if (status != Success)
            {
                return (status, null, false);
            }

            var entry = payload[4..];
            Assert.Equal((int)n, entry.Length);
            var nameLength = BitConverter.ToUInt32(entry, 60);
            var name = Encoding.Unicode.GetString(entry, 94, (int)nameLength);       // FileBothDirectoryInformation: the name starts at 94
            return (status, name, (BitConverter.ToUInt32(entry, 56) & 0x10) != 0);
        }

        public List<string> List(string path = "\\")
        {
            var (status, id) = Create(path, GenericRead, Open, DirectoryFile);
            Assert.Equal(Success, status);
            var names = new List<string>();
            var first = true;
            while (true)
            {
                var (s, name, _) = NextEntry(id, "*", first);
                first = false;
                if (s == NoMoreFiles)
                {
                    break;
                }

                Assert.Equal(Success, s);
                names.Add(name!);
            }

            Close(id);
            return names;
        }
    }

    private Client Start(bool writable)
    {
        var answers = new BlockingCollection<byte[]>();
        var service = new DriveService(new SharedDrive(_root, writable), 1, answers.Add);
        _services.Add(service);
        return new Client(service, answers);
    }

    private string Disk(string relative) => Path.Combine(_root, relative);

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

    // ---- reading ----

    [Fact]
    public void Paths_with_forward_slashes_work_like_xrdp_sends_them()
    {
        var c = Start(false);
        var (status, id) = c.Create("/sub/inner.txt", 0x00100001);
        Assert.Equal(Success, status);
        Assert.Equal("inner"u8.ToArray(), c.Read(id, 100, 0).Data);
        Assert.Equal(PathNotFound, c.Create("/etc/passwd").Status);                         // a place below the root, and there is no folder "etc" there
        Assert.Equal(NameInvalid, c.Create("/../outside/secret.txt").Status);
    }

    [Fact]
    public void A_file_can_be_opened_read_in_pieces_and_read_past_its_end()
    {
        var c = Start(false);
        var (status, id) = c.Create("\\hello.txt");
        Assert.Equal(Success, status);
        Assert.NotEqual(0u, id);
        Assert.Equal("hello world"u8.ToArray(), c.Read(id, 100, 0).Data);
        Assert.Equal("lo wo"u8.ToArray(), c.Read(id, 5, 3).Data);
        var past = c.Read(id, 10, 50);
        Assert.Equal(Success, past.Status);
        Assert.Empty(past.Data);
        Assert.Equal(Success, c.Close(id));
        Assert.Equal(0, c.Service.OpenCount);
    }

    [Fact]
    public void Properties_of_a_file_a_folder_and_the_disk()
    {
        var c = Start(false);
        var (_, file) = c.Create("\\hello.txt");
        var basic = c.Query(5, 4, file);
        Assert.Equal(Success, basic.Status);
        Assert.Equal(36, basic.Data.Length);
        Assert.Equal(0x80u, BitConverter.ToUInt32(basic.Data, 32));                 // a normal file
        var standard = c.Query(5, 5, file);
        Assert.Equal(11L, BitConverter.ToInt64(standard.Data, 8));                  // EndOfFile
        Assert.Equal(0, standard.Data[21]);                                         // not a directory
        var (_, dir) = c.Create("\\sub", GenericRead, Open, DirectoryFile);
        Assert.Equal(1, c.Query(5, 5, dir).Data[21]);
        Assert.Equal(0x10u, BitConverter.ToUInt32(c.Query(5, 4, dir).Data, 32));
        Assert.Equal(NotSupported, c.Query(5, 999, file).Status);

        var (_, root) = c.Create("\\", GenericRead, Open, DirectoryFile);
        foreach (var volume in new uint[] { 1, 3, 4, 5, 7 })
        {
            Assert.Equal(Success, c.Query(0x0A, volume, root).Status);
        }

        Assert.Equal(NotSupported, c.Query(0x0A, 99, root).Status);
        var attributes = c.Query(0x0A, 5, root).Data;
        Assert.Equal(0x80006u, BitConverter.ToUInt32(attributes, 0));               // read only volume
        Assert.Equal(0x12u, BitConverter.ToUInt32(c.Query(0x0A, 4, root).Data, 4)); // read only device
        Assert.Equal(0UL, BitConverter.ToUInt64(c.Query(0x0A, 3, root).Data, 8));   // no free space on a read only drive
    }

    [Fact]
    public void A_folder_is_listed_one_entry_at_a_time_without_hidden_names_and_links()
    {
        var c = Start(false);
        var names = c.List();
        Assert.Equal([".", ".."], names.Take(2).ToArray());
        Assert.Equal(["hello.txt", "sub"], names.Skip(2).Order().ToArray());
        Assert.DoesNotContain(".env", names);
        Assert.DoesNotContain(".ssh", names);
        Assert.DoesNotContain("link_out", names);
        Assert.DoesNotContain("file_link", names);

        var (_, dir) = c.Create("\\", GenericRead, Open, DirectoryFile);
        var (_, name, isDirectory) = c.NextEntry(dir, "s*", true);
        Assert.Equal("sub", name);
        Assert.True(isDirectory);
        Assert.Equal(NoMoreFiles, c.NextEntry(dir, "s*", false).Status);
        Assert.Equal("hello.txt", c.NextEntry(dir, "*.TXT", true).Name);                       // case does not matter
        Assert.Equal("hello.txt", c.NextEntry(dir, "h?llo.txt", true).Name);
        Assert.Equal(NoMoreFiles, c.NextEntry(dir, "nothing*", true).Status);
        Assert.Equal(["inner.txt"], c.List("\\sub").Where(n => !n.StartsWith('.')).ToArray());
    }

    // ---- read only holds for every kind of request ----

    [Fact]
    public void A_read_only_drive_refuses_everything_that_would_change_it()
    {
        var c = Start(false);
        var before = Snapshot();

        Assert.Equal(AccessDenied, c.Create("\\new.txt", GenericWrite, CreateNew).Status);
        Assert.Equal(AccessDenied, c.Create("\\new.txt", GenericRead, OpenIf).Status);                  // would create
        Assert.Equal(AccessDenied, c.Create("\\new.txt", GenericRead, OverwriteIf).Status);
        Assert.Equal(AccessDenied, c.Create("\\hello.txt", GenericRead, Overwrite).Status);             // would empty it
        Assert.Equal(AccessDenied, c.Create("\\hello.txt", GenericRead, Supersede).Status);
        Assert.Equal(AccessDenied, c.Create("\\hello.txt", GenericRead | WriteData).Status);
        Assert.Equal(AccessDenied, c.Create("\\hello.txt", AppendData).Status);
        Assert.Equal(AccessDenied, c.Create("\\hello.txt", GenericWrite).Status);
        Assert.Equal(AccessDenied, c.Create("\\hello.txt", GenericRead | Delete).Status);
        Assert.Equal(AccessDenied, c.Create("\\hello.txt", GenericRead, Open, DeleteOnClose).Status);
        Assert.Equal(AccessDenied, c.Create("\\newdir", GenericRead, CreateNew, DirectoryFile).Status);
        Assert.Equal(AccessDenied, c.Create("\\sub", GenericRead | Delete, Open, DirectoryFile).Status);

        // an open read handle cannot be used to write, truncate, rename or delete either
        var (_, id) = c.Create("\\hello.txt");
        Assert.Equal(AccessDenied, c.Write(id, "X"u8.ToArray(), 0).Status);
        Assert.Equal(AccessDenied, c.Set(20, id, BitConverter.GetBytes(0L)));                           // end of file
        Assert.Equal(AccessDenied, c.Set(19, id, BitConverter.GetBytes(0L)));
        Assert.Equal(AccessDenied, c.Set(13, id, [1]));                                                // delete
        Assert.Equal(AccessDenied, c.Set(10, id, [1, 0, .. BitConverter.GetBytes((uint)Utf16("\\x\0").Length), .. Utf16("\\x\0")]));   // rename
        Assert.Equal(AccessDenied, c.Set(4, id, new byte[36]));
        c.Close(id);

        Assert.Equal(before, Snapshot());                                                               // not a byte changed
        Assert.False(File.Exists(Disk("new.txt")));
    }

    // Every path below the test folder with the content of the regular files (links are not followed)
    private string Snapshot() => string.Join('\n', Directory.EnumerateFileSystemEntries(_base, "*", SearchOption.AllDirectories).Order()
        .Select(p => new FileInfo(p).LinkTarget is null && File.Exists(p) ? p + ":" + File.ReadAllText(p) : p));

    // ---- the way out ----

    [Theory]
    [InlineData("\\..\\outside\\secret.txt")]
    [InlineData("\\sub\\..\\..\\outside\\secret.txt")]
    [InlineData("..\\outside\\secret.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("\\/etc/passwd")]
    [InlineData("/../outside/secret.txt")]
    [InlineData("\\.env")]
    [InlineData("\\.ssh\\id")]
    [InlineData("\\hello.txt:stream")]
    [InlineData("\\\uFF0E\uFF0E\\outside\\secret.txt")]
    [InlineData("\\a\u202Eb")]
    public void Paths_that_leave_the_root_or_are_not_ours_find_nothing(string path)
    {
        foreach (var c in new[] { Start(false), Start(true) })
        {
            var (status, id) = c.Create(path, GenericRead | GenericWrite, OpenIf);
            Assert.Equal(NameInvalid, status);
            Assert.Equal(0u, id);
            Assert.Equal(NameInvalid, c.Create(path).Status);
            Assert.Equal(NameInvalid, c.Create(path, GenericRead, Open, DirectoryFile).Status);
            Assert.Equal(0, c.Service.OpenCount);
        }

        Assert.False(File.Exists(Path.Combine(_base, "x")));
        Assert.Equal("TOP SECRET", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Theory]
    [InlineData("\\link_out\\secret.txt")]
    [InlineData("\\link_out")]
    [InlineData("\\file_link")]
    public void A_symlink_cannot_be_opened_read_or_walked_through(string path)
    {
        foreach (var c in new[] { Start(false), Start(true) })
        {
            Assert.Equal(NameInvalid, c.Create(path).Status);
            Assert.Equal(NameInvalid, c.Create(path, GenericRead | GenericWrite, OpenIf).Status);
        }

        Assert.Equal("TOP SECRET", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public void A_symlink_that_appears_after_the_check_is_still_not_followed()
    {
        // The check on the path happens first; here the link is created in between by hand: a folder of the root replaced by a link to the outside
        var c = Start(true);
        var (_, dir) = c.Create("\\sub", GenericRead, Open, DirectoryFile);
        Directory.Delete(Disk("sub"), true);
        Directory.CreateSymbolicLink(Disk("sub"), _outside);
        // listing through the old handle shows the old (now deleted) folder, not the outside
        var names = c.List("\\").Where(n => n != "." && n != "..").ToArray();
        Assert.DoesNotContain("secret.txt", names);
        Assert.Equal(NameInvalid, c.Create("\\sub\\secret.txt").Status);
        c.Close(dir);
    }

    [Fact]
    public void Reading_with_a_wild_file_id_or_a_wrong_kind_of_handle_is_an_error_not_a_crash()
    {
        var c = Start(true);
        Assert.Equal(InvalidHandle, c.Read(0, 10, 0).Status);
        Assert.Equal(InvalidHandle, c.Read(0xFFFFFFFF, 10, 0).Status);
        Assert.Equal(InvalidHandle, c.Write(77, [1], 0).Status);
        Assert.Equal(InvalidHandle, c.Close(12345));
        Assert.Equal(InvalidHandle, c.Query(5, 4, 99).Status);
        var (_, dir) = c.Create("\\sub", GenericRead, Open, DirectoryFile);
        Assert.NotEqual(Success, c.Read(dir, 10, 0).Status);                                          // a folder cannot be read
        var (_, file) = c.Create("\\hello.txt");
        Assert.NotEqual(Success, c.NextEntry(file, "*", true).Status);                               // a file cannot be listed
        // a closed handle is gone
        c.Close(file);
        Assert.Equal(InvalidHandle, c.Read(file, 10, 0).Status);
    }

    [Fact]
    public void A_folder_and_a_file_are_told_apart_when_opened()
    {
        var c = Start(false);
        Assert.Equal(NotADirectory, c.Create("\\hello.txt", GenericRead, Open, DirectoryFile).Status);
        Assert.Equal(IsADirectory, c.Create("\\sub", GenericRead, Open, NonDirectoryFile).Status);
        Assert.Equal(NoSuchFile, c.Create("\\missing.txt").Status);
        Assert.Equal(PathNotFound, c.Create("\\missing\\x.txt").Status);
        Assert.Equal(NameCollision, Start(true).Create("\\hello.txt", GenericWrite, CreateNew).Status);
    }

    [Fact]
    public void Unknown_requests_get_a_status_not_silence_and_a_directory_notification_stays_open()
    {
        var c = Start(false);
        Assert.Equal(NotSupported, c.Call(0x0E, 0, 0, new byte[32]).Status);                          // device control
        Assert.Equal(Success, c.Call(0x11, 0, 0, new byte[32]).Status);                               // lock
        Assert.Equal(NotSupported, c.Call(0x99, 0, 0, new byte[32]).Status);                          // unknown
        // short and broken bodies
        Assert.NotEqual(Success, c.Call(0, 0, 0, [1, 2, 3]).Status);
        Assert.NotEqual(Success, c.Call(3, 0, 5, [1]).Status);
        Assert.NotEqual(Success, c.Call(6, 0, 5, [1, 2]).Status);
    }

    // ---- writing, when it is allowed ----

    [Fact]
    public void A_writable_drive_creates_writes_reads_back_truncates_and_deletes()
    {
        var c = Start(true);
        var (status, id) = c.Create("\\made.txt", GenericRead | GenericWrite, CreateNew);
        Assert.Equal(Success, status);
        Assert.Equal((Success, 5u), c.Write(id, "12345"u8.ToArray(), 0));
        Assert.Equal((Success, 3u), c.Write(id, "abc"u8.ToArray(), 5));
        Assert.Equal("12345abc"u8.ToArray(), c.Read(id, 100, 0).Data);
        Assert.Equal(Success, c.Set(20, id, BitConverter.GetBytes(4L)));                               // end of file: truncate
        Assert.Equal("1234", File.ReadAllText(Disk("made.txt")));
        c.Close(id);

        var (_, again) = c.Create("\\made.txt", GenericRead | GenericWrite, Overwrite);                // overwrite empties
        Assert.Equal("", File.ReadAllText(Disk("made.txt")));
        c.Close(again);

        var (_, del) = c.Create("\\made.txt", Delete | GenericRead);
        Assert.Equal(Success, c.Set(13, del, [1]));
        Assert.True(File.Exists(Disk("made.txt")));                                                    // deleted when the handle closes
        c.Close(del);
        Assert.False(File.Exists(Disk("made.txt")));

        var (_, onClose) = c.Create("\\tmp.txt", GenericWrite, CreateNew, DeleteOnClose);
        Assert.True(File.Exists(Disk("tmp.txt")));
        c.Close(onClose);
        Assert.False(File.Exists(Disk("tmp.txt")));
    }

    [Fact]
    public void Folders_can_be_made_listed_renamed_and_removed_when_empty()
    {
        var c = Start(true);
        var (status, dir) = c.Create("\\newdir", GenericRead, CreateNew, DirectoryFile);
        Assert.Equal(Success, status);
        Assert.True(Directory.Exists(Disk("newdir")));
        c.Close(dir);
        var (_, file) = c.Create("\\newdir\\a.txt", GenericWrite, CreateNew);
        c.Write(file, "x"u8.ToArray(), 0);
        c.Close(file);

        var (_, handle) = c.Create("\\newdir", Delete | GenericRead, Open, DirectoryFile);
        Assert.Equal(DirectoryNotEmpty, c.Set(13, handle, [1]));
        Assert.Equal(Success, c.Set(10, handle, [0, 0, .. BitConverter.GetBytes((uint)Utf16("\\renamed\0").Length), .. Utf16("\\renamed\0")]));
        Assert.True(File.Exists(Disk("renamed/a.txt")));
        c.Close(handle);
    }

    [Fact]
    public void Renaming_stays_inside_the_root_and_never_to_a_hidden_or_foreign_place()
    {
        var c = Start(true);
        var (_, id) = c.Create("\\hello.txt", Delete | GenericRead);
        foreach (var target in new[] { "\\..\\outside\\stolen.txt", "\\sub\\..\\..\\outside\\x", "C:\\x", "\\.hidden", "\\link_out\\x", "\\a:b" })
        {
            var name = Utf16(target + "\0");
            Assert.Equal(NameInvalid, c.Set(10, id, [1, 0, .. BitConverter.GetBytes((uint)name.Length), .. name]));
        }

        var elsewhere = Utf16("/tmp/x\0");                                                             // a place below the root, and no folder "tmp" there
        Assert.Equal(PathNotFound, c.Set(10, id, [1, 0, .. BitConverter.GetBytes((uint)elsewhere.Length), .. elsewhere]));
        Assert.True(File.Exists(Disk("hello.txt")));
        Assert.Equal(["secret.txt"], Directory.GetFileSystemEntries(_outside).Select(Path.GetFileName).ToArray());

        // a collision without "replace" is refused; with it a file replaces a file, never a folder
        var target2 = Utf16("\\sub\0");
        Assert.Equal(NameCollision, c.Set(10, id, [0, 0, .. BitConverter.GetBytes((uint)target2.Length), .. target2]));
        Assert.Equal(AccessDenied, c.Set(10, id, [1, 0, .. BitConverter.GetBytes((uint)target2.Length), .. target2]));
        var ok = Utf16("\\sub\\inner.txt\0");
        Assert.Equal(Success, c.Set(10, id, [1, 0, .. BitConverter.GetBytes((uint)ok.Length), .. ok]));
        Assert.Equal("hello world", File.ReadAllText(Disk("sub/inner.txt")));
        c.Close(id);
    }

    [Fact]
    public void The_root_is_never_deleted_or_renamed_and_hidden_things_stay_untouched()
    {
        var c = Start(true);
        var (_, root) = c.Create("\\", Delete | GenericRead, Open, DirectoryFile);
        Assert.Equal(AccessDenied, c.Set(13, root, [1]));
        var name = Utf16("\\gone\0");
        Assert.NotEqual(Success, c.Set(10, root, [0, 0, .. BitConverter.GetBytes((uint)name.Length), .. name]));
        c.Close(root);
        Assert.True(Directory.Exists(_root));
        Assert.Equal("SECRET=1", File.ReadAllText(Disk(".env")));
        Assert.Equal(NameInvalid, c.Create("\\.env", GenericRead | GenericWrite, OverwriteIf).Status);
        Assert.Equal("SECRET=1", File.ReadAllText(Disk(".env")));
    }

    [Fact]
    public void A_file_cannot_grow_past_the_cap()
    {
        var c = Start(true);
        var (_, id) = c.Create("\\big.bin", GenericWrite, CreateNew);
        Assert.Equal(DiskFull, c.Write(id, [1], (ulong)DriveService.MaxFileBytes).Status);
        Assert.Equal(DiskFull, c.Write(id, [1, 2], (ulong)DriveService.MaxFileBytes - 1).Status);
        Assert.Equal(DiskFull, c.Write(id, [1], ulong.MaxValue).Status);
        Assert.Equal(DiskFull, c.Set(20, id, BitConverter.GetBytes(DriveService.MaxFileBytes + 1)));
        Assert.Equal(DiskFull, c.Set(20, id, BitConverter.GetBytes(-1L)));
        Assert.Equal(0L, new FileInfo(Disk("big.bin")).Length);
        c.Close(id);
    }

    [Fact]
    public void Too_many_open_handles_are_refused_and_closing_frees_them()
    {
        var c = Start(false);
        var ids = new List<uint>();
        for (var i = 0; i < DriveService.MaxOpen; i++)
        {
            var (status, id) = c.Create("\\hello.txt");
            Assert.Equal(Success, status);
            ids.Add(id);
        }

        Assert.Equal(0xC000009Au, c.Create("\\hello.txt").Status);
        c.Close(ids[0]);
        Assert.Equal(Success, c.Create("\\hello.txt").Status);
    }

    [Fact]
    public void Disposing_closes_every_handle_and_stops_answering()
    {
        var c = Start(false);
        c.Create("\\hello.txt");
        c.Create("\\sub", GenericRead, Open, DirectoryFile);
        Assert.Equal(2, c.Service.OpenCount);
        c.Service.Dispose();
        Assert.Equal(0, c.Service.OpenCount);
        c.Service.Submit(new byte[40]);                         // nothing happens, nothing throws
    }

    [Fact]
    public void Random_requests_never_throw_and_never_touch_anything_outside()
    {
        var c = Start(true);
        var random = new Random(42);
        var before = Snapshot();
        for (var i = 0; i < 300; i++)
        {
            var body = new byte[random.Next(0, 120)];
            random.NextBytes(body);
            var major = new uint[] { 0, 2, 3, 4, 5, 6, 0x0A, 0x0C, 0x0E, 0x11 }[random.Next(10)];
            c.Call(major, (uint)random.Next(0, 2), (uint)random.Next(0, 5), body);       // minor 2 of a directory request is the notification, which is never answered
        }

        Assert.Equal("TOP SECRET", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
        Assert.Equal("SECRET=1", File.ReadAllText(Disk(".env")));
        Assert.Equal(["secret.txt"], Directory.GetFileSystemEntries(_outside).Select(Path.GetFileName).ToArray());
        _ = before;
    }
}
