using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using JulOS.Integrations.Display.Rdp.Channels;

namespace JulOS.Integrations.Display.Rdp.Drive;

/// <summary>
/// Der Dateisystem-Teil der Geraeteumleitung (MS-RDPEFS 2.2.3 und 3.2.5.2): beantwortet die Anfragen des fernen Rechners an das Laufwerk
/// (oeffnen, lesen, schreiben, auflisten, Eigenschaften, Umbenennen, Loeschen, Groesse des Datentraegers) aus einem <see cref="SharedDrive"/>.
/// Alles, was der ferne Rechner schickt, ist feindlich: jeder Pfad geht durch den <see cref="DriveJail"/>, jeder Wert wird begrenzt, nichts
/// wirft aus <see cref="Submit"/> heraus. Nur Lesen, solange <see cref="SharedDrive.Writable"/> nicht gesetzt ist: dann scheitert jede aendernde
/// Anfrage mit STATUS_ACCESS_DENIED, schon beim Oeffnen.
///
/// Die Arbeit mit der Platte geschieht auf einem eigenen Faden (eine begrenzte Warteschlange, der Reihe nach), damit eine langsame Platte die
/// Leseschleife der RDP-Verbindung nie aufhaelt. Zu viele wartende Anfragen werden mit STATUS_INSUFFICIENT_RESOURCES beantwortet.
/// </summary>
public sealed class DriveService : IDisposable
{
    public const int MaxQueued = 256, MaxOpen = 256, MaxReadBytes = 1 << 20, MaxEntries = 20_000;

    /// <summary>Hoechste Dateigroesse im Schreibmodus (Schreiben darueber hinaus: STATUS_DISK_FULL).</summary>
    public const long MaxFileBytes = 1L << 30;

    // NTSTATUS
    public const uint StatusSuccess = 0, StatusNoMoreFiles = 0x80000006, StatusBufferOverflow = 0x80000005, StatusUnsuccessful = 0xC0000001,
        StatusNotSupported = 0xC00000BB, StatusAccessDenied = 0xC0000022, StatusNameNotFound = 0xC0000034, StatusPathNotFound = 0xC000003A,
        StatusNameCollision = 0xC0000035, StatusNameInvalid = 0xC0000033, StatusNotADirectory = 0xC0000103, StatusIsADirectory = 0xC00000BA,
        StatusDirectoryNotEmpty = 0xC0000101, StatusInvalidParameter = 0xC000000D, StatusInvalidHandle = 0xC0000008, StatusDiskFull = 0xC000007F,
        StatusInsufficientResources = 0xC000009A, StatusNoSuchFile = 0xC000000F;

    // IRP_MJ_*
    private const uint MajorCreate = 0, MajorClose = 2, MajorRead = 3, MajorWrite = 4, MajorQueryInformation = 5, MajorSetInformation = 6,
        MajorFlush = 9, MajorQueryVolume = 0x0A, MajorDirectory = 0x0C, MajorDeviceControl = 0x0E, MajorLockControl = 0x11;

    private const uint MinorQueryDirectory = 1, MinorNotifyChange = 2;

    // FileInformationClass
    private const uint FileBasic = 4, FileStandard = 5, FileRename = 10, FileDisposition = 13, FileAllocation = 19, FileEndOfFile = 20, FileAttributeTag = 35;
    private const uint DirDirectory = 1, DirFull = 2, DirBoth = 3, DirNames = 12;
    private const uint VolumeInfo = 1, VolumeSize = 3, VolumeDevice = 4, VolumeAttribute = 5, VolumeFullSize = 7;

    // CreateDisposition und -Options
    private const uint Supersede = 0, Open = 1, CreateNew = 2, OpenIf = 3, Overwrite = 4, OverwriteIf = 5;
    private const uint DirectoryFile = 1, NonDirectoryFile = 0x40, DeleteOnClose = 0x1000;

    // Zugriffsrechte, die etwas aendern: FILE_WRITE_DATA, FILE_APPEND_DATA, DELETE, GENERIC_ALL, GENERIC_WRITE
    private const uint WriteAccess = 0x2 | 0x4 | 0x10000 | 0x10000000 | 0x40000000;

    private const uint AttrDirectory = 0x10, AttrNormal = 0x80;
    private const int BlockSize = 4096, SectorSize = 512;

    private readonly SharedDrive _drive;
    private readonly DriveJail _jail;
    private readonly JailFs _fs;
    private readonly uint _deviceId;
    private readonly Action<byte[]> _send;
    private readonly Action<string>? _log;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueued) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, Item> _items = [];
    private Task? _worker;
    private uint _nextId;
    private int _disposed;

    public DriveService(SharedDrive drive, uint deviceId, Action<byte[]> send, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(drive);
        _drive = drive;
        _jail = DriveJail.TryCreate(drive.Root, out var error) ?? throw new ArgumentException(error.Length == 0 ? "Keine Wurzel fuer das Laufwerk." : error, nameof(drive));
        _fs = new JailFs(_jail);
        _deviceId = deviceId;
        _send = send;
        _log = log;
    }

    public bool Writable => _drive.Writable;

    /// <summary>Wie viele Dateien und Ordner gerade offen sind (Tests).</summary>
    public int OpenCount
    {
        get
        {
            lock (_items)
            {
                return _items.Count;
            }
        }
    }

    private sealed class Item
    {
        public required string[] Segments;
        public required bool IsDirectory;
        public SafeFileHandle? Handle;
        public bool CanWrite;
        public bool DeleteOnClose;
        public bool DeletePending;
        public List<Entry>? Listing;
        public int Next;

        public string Remote => "\\" + string.Join('\\', Segments);
    }

    private sealed record Entry(string Name, bool IsDirectory, long Size, DateTime Created, DateTime Accessed, DateTime Written);

    /// <summary>
    /// Eine Anfrage des fernen Rechners (die ganze Nachricht, mit Komponente und Art), schon als gueltig erkannt: Geraet ist das des Laufwerks. Kehrt sofort
    /// zurueck; die Antwort geht spaeter ueber <c>send</c>.
    /// </summary>
    public void Submit(ReadOnlySpan<byte> message)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var copy = message.ToArray();
        if (Interlocked.CompareExchange(ref _worker, null, null) is null)
        {
            lock (_queue)
            {
                _worker ??= Task.Run(() => RunAsync(_stop.Token));
            }
        }

        if (!_queue.Writer.TryWrite(copy) && Header(copy) is { } header)
        {
            _send(Complete(header.Completion, StatusInsufficientResources, []));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (Header(message) is not { } header)
                {
                    continue;
                }

                try
                {
                    Handle(header, message);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _log?.Invoke($"drive: request {header.Major:X} failed: {e.GetType().Name}");
                    Reply(header.Completion, StatusUnsuccessful, []);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // zu Ende
        }
    }

    private readonly record struct Request(uint DeviceId, uint FileId, uint Completion, uint Major, uint Minor);

    private static Request? Header(byte[] message)
    {
        var reader = new ByteReader(message);
        return reader.TrySkip(4) && reader.TryU32(out var device) && reader.TryU32(out var file) && reader.TryU32(out var completion)
            && reader.TryU32(out var major) && reader.TryU32(out var minor)
            ? new Request(device, file, completion, major, minor)
            : null;
    }

    private byte[] Complete(uint completion, uint status, ReadOnlySpan<byte> payload) =>
        new ByteWriter(16 + payload.Length).U16(0x4472).U16(0x4943).U32(_deviceId).U32(completion).U32(status).Bytes(payload).ToArray();

    private void Reply(uint completion, uint status, ReadOnlySpan<byte> payload) => _send(Complete(completion, status, payload));

    // Antwort mit einer Laenge davor (Lesen, Eigenschaften ...)
    private void ReplyData(uint completion, uint status, ReadOnlySpan<byte> data)
    {
        var body = new ByteWriter(4 + data.Length).U32((uint)data.Length).Bytes(data).ToArray();
        Reply(completion, status, body);
    }

    private void Handle(Request request, byte[] message)
    {
        var reader = new ByteReader(message);
        reader.TrySkip(24);
        switch (request.Major)
        {
            case MajorCreate:
                Create(request, reader);
                break;
            case MajorClose:
                Close(request);
                break;
            case MajorRead:
                Read(request, reader);
                break;
            case MajorWrite:
                Write(request, reader);
                break;
            case MajorQueryInformation:
                QueryInformation(request, reader);
                break;
            case MajorSetInformation:
                SetInformation(request, reader);
                break;
            case MajorQueryVolume:
                QueryVolume(request, reader);
                break;
            case MajorDirectory when request.Minor == MinorQueryDirectory:
                QueryDirectory(request, reader);
                break;
            case MajorDirectory when request.Minor == MinorNotifyChange:
                break;                                              // wird nie beantwortet: der ferne Rechner wartet darauf, bis er das Handle schliesst
            case MajorDeviceControl:
                Reply(request.Completion, StatusNotSupported, new ByteWriter(4).U32(0).ToArray());
                break;
            case MajorLockControl:
                Reply(request.Completion, StatusSuccess, new ByteWriter(5).Zeros(5).ToArray());
                break;
            case MajorFlush:
                Reply(request.Completion, StatusSuccess, []);
                break;
            default:
                Reply(request.Completion, StatusNotSupported, []);
                break;
        }
    }

    // ---- Pfade ----

    private static string? ReadPath(ref ByteReader reader, uint byteLength)
    {
        if (byteLength > DriveJail.MaxPathChars * 2 + 2 || (byteLength & 1) != 0 || !reader.TryBytes(byteLength, out var bytes))
        {
            return null;
        }

        string text;
        try { text = new UnicodeEncoding(false, false, true).GetString(bytes); }
        catch (ArgumentException) { return null; }                  // ungueltige UTF-16-Folge (einzelnes Surrogat)

        // Mit abschliessendem NUL gesendet; mitten im Pfad hat ein NUL nichts zu suchen
        if (text.EndsWith('\0'))
        {
            text = text[..^1];
        }

        return text.Contains('\0') ? null : text;
    }

    private Item? Find(uint fileId)
    {
        lock (_items)
        {
            return _items.GetValueOrDefault(fileId);
        }
    }

    // ---- Anfragen ----

    private void Create(Request request, ByteReader reader)
    {
        if (!reader.TryU32(out var desired) || !reader.TrySkip(8) || !reader.TryU32(out _) || !reader.TryU32(out _)
            || !reader.TryU32(out var disposition) || !reader.TryU32(out var options) || !reader.TryU32(out var pathLength))
        {
            Reply(request.Completion, StatusInvalidParameter, []);
            return;
        }

        if (ReadPath(ref reader, pathLength) is not { } remote || _jail.Resolve(remote, out var segments) is not { } full)
        {
            Reply(request.Completion, StatusNameInvalid, new ByteWriter(5).Zeros(5).ToArray());      // Pfad abgelehnt: so tun, als gaebe es ihn nicht
            return;
        }

        var exists = File.Exists(full) || Directory.Exists(full);
        var isDirectory = exists && Directory.Exists(full);
        var creates = disposition is CreateNew || (disposition is OpenIf && !exists);
        var wantsWrite = (desired & WriteAccess) != 0 || disposition is Supersede or CreateNew or Overwrite or OverwriteIf || (options & DeleteOnClose) != 0 || creates;

        // Nur Lesen: alles, was ein Schreiben, Anlegen oder Loeschen waere, scheitert hier, nicht erst beim Schreiben
        if (wantsWrite && !_drive.Writable)
        {
            Fail(request, StatusAccessDenied);
            return;
        }

        if (exists)
        {
            if ((options & DirectoryFile) != 0 && !isDirectory)
            {
                Fail(request, StatusNotADirectory);
                return;
            }

            if ((options & NonDirectoryFile) != 0 && isDirectory)
            {
                Fail(request, StatusIsADirectory);
                return;
            }

            if (disposition is CreateNew)
            {
                Fail(request, StatusNameCollision);
                return;
            }

            if (isDirectory && disposition is Supersede or Overwrite or OverwriteIf)
            {
                Fail(request, StatusIsADirectory);
                return;
            }
        }
        else
        {
            if (disposition is Open or Overwrite)
            {
                Fail(request, ParentExists(segments) ? StatusNameNotFound : StatusPathNotFound);
                return;
            }

            if (!ParentExists(segments) || segments.Length == 0)
            {
                Fail(request, StatusPathNotFound);
                return;
            }
        }

        var write = _drive.Writable && wantsWrite && !isDirectory;
        SafeFileHandle? handle;
        byte information;
        if (!exists && (options & DirectoryFile) != 0)
        {
            // Ordner anlegen (der Elternordner ist durch die Pruefung oben ein echter Ordner in der Wurzel, ohne Symlink)
            try { Directory.CreateDirectory(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(request, StatusAccessDenied); return; }
            handle = _fs.Open(segments, false, false, out var dirError);
            if (handle is null)
            {
                TryRemoveDirectory(full);
                Fail(request, Map(dirError));
                return;
            }

            information = 2;
        }
        else
        {
            handle = _fs.Open(segments, write, !exists, out var error);
            if (handle is null)
            {
                Fail(request, Map(error));
                return;
            }

            // Nur gewoehnliche Dateien und Ordner: ein Pipe oder Geraet laesst sich nicht positionieren
            if (!isDirectory)
            {
                try { RandomAccess.GetLength(handle); }
                catch (Exception e) when (e is NotSupportedException or IOException)
                {
                    handle.Dispose();
                    Fail(request, StatusAccessDenied);
                    return;
                }
            }

            information = !exists ? (byte)2 : (byte)1;
            if (exists && !isDirectory && disposition is Supersede or Overwrite or OverwriteIf)
            {
                // Erst jetzt, nach der Pruefung des geoeffneten Deskriptors, wird etwas verkuerzt
                JailFs.Truncate(handle, 0);
                information = disposition == Supersede ? (byte)0 : (byte)3;
            }
        }

        var item = new Item { Segments = segments, IsDirectory = isDirectory || (!exists && (options & DirectoryFile) != 0), Handle = handle, CanWrite = write, DeleteOnClose = (options & DeleteOnClose) != 0 };
        uint id;
        lock (_items)
        {
            if (_items.Count >= MaxOpen)
            {
                handle.Dispose();
                Fail(request, StatusInsufficientResources);
                return;
            }

            do
            {
                id = ++_nextId;
            }
            while (id == 0 || _items.ContainsKey(id));

            _items[id] = item;
        }

        Reply(request.Completion, StatusSuccess, new ByteWriter(5).U32(id).U8(information).ToArray());
    }

    private void Fail(Request request, uint status) =>
        Reply(request.Completion, status, new ByteWriter(5).Zeros(5).ToArray());       // FileId 0, Information 0

    private static uint Map(int errno) => JailFs.Classify(errno) switch
    {
        JailFs.Failure.NotFound => StatusNameNotFound,
        JailFs.Failure.Denied => StatusAccessDenied,
        _ => StatusUnsuccessful,
    };

    private bool ParentExists(string[] segments)
    {
        if (segments.Length <= 1)
        {
            return true;                // die Wurzel gibt es
        }

        return _jail.TryResolve(segments[..^1], out var parent) && Directory.Exists(parent);
    }

    private static void TryRemoveDirectory(string full)
    {
        try { Directory.Delete(full, false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* egal */ }
    }

    private void Close(Request request)
    {
        Item? item;
        lock (_items)
        {
            if (_items.Remove(request.FileId, out item))
            {
                // ok
            }
        }

        if (item is null)
        {
            Reply(request.Completion, StatusInvalidHandle, new ByteWriter(4).Zeros(4).ToArray());
            return;
        }

        item.Handle?.Dispose();
        item.Handle = null;
        if ((item.DeleteOnClose || item.DeletePending) && _drive.Writable)
        {
            Delete(item);
        }

        Reply(request.Completion, StatusSuccess, new ByteWriter(4).Zeros(4).ToArray());
    }

    // Loescht den Eintrag eines geschlossenen Handles. Der Pfad geht noch einmal durch den Zaun (ein Symlink dazwischen waere abgelehnt).
    private void Delete(Item item)
    {
        if (item.Segments.Length == 0 || !_jail.TryResolve(item.Segments, out var full))
        {
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                Directory.Delete(full, false);
            }
            else
            {
                File.Delete(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("drive: delete failed: " + e.GetType().Name);
        }
    }

    private void Read(Request request, ByteReader reader)
    {
        if (!reader.TryU32(out var length) || !reader.TryU64(out var offset) || Find(request.FileId) is not { } item || item.IsDirectory || item.Handle is null)
        {
            Reply(request.Completion, Missing(request.FileId) ? StatusInvalidHandle : StatusInvalidParameter, new ByteWriter(4).Zeros(4).ToArray());
            return;
        }

        length = Math.Min(length, MaxReadBytes);
        var buffer = new byte[length];
        int read;
        try
        {
            read = offset > long.MaxValue ? 0 : RandomAccess.Read(item.Handle, buffer, (long)offset);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Reply(request.Completion, StatusUnsuccessful, new ByteWriter(4).Zeros(4).ToArray());
            return;
        }

        // Am Ende der Datei kommt Laenge 0 mit Erfolg, so wie es read(2) tut
        ReplyData(request.Completion, StatusSuccess, buffer.AsSpan(0, read));
    }

    private bool Missing(uint fileId) => Find(fileId) is null;

    private void Write(Request request, ByteReader reader)
    {
        if (!reader.TryU32(out var length) || !reader.TryU64(out var offset) || !reader.TrySkip(20) || !reader.TryBytes(length, out var data))
        {
            Reply(request.Completion, StatusInvalidParameter, new ByteWriter(5).Zeros(5).ToArray());
            return;
        }

        if (Find(request.FileId) is not { } item || item.IsDirectory || item.Handle is null)
        {
            Reply(request.Completion, StatusInvalidHandle, new ByteWriter(5).Zeros(5).ToArray());
            return;
        }

        if (!_drive.Writable || !item.CanWrite)
        {
            Reply(request.Completion, StatusAccessDenied, new ByteWriter(5).Zeros(5).ToArray());
            return;
        }

        if (offset > MaxFileBytes || offset + length > MaxFileBytes)
        {
            Reply(request.Completion, StatusDiskFull, new ByteWriter(5).Zeros(5).ToArray());
            return;
        }

        try
        {
            RandomAccess.Write(item.Handle, data, (long)offset);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Reply(request.Completion, e is IOException { HResult: 28 } ? StatusDiskFull : StatusUnsuccessful, new ByteWriter(5).Zeros(5).ToArray());
            return;
        }

        Reply(request.Completion, StatusSuccess, new ByteWriter(5).U32(length).U8(0).ToArray());
    }

    // ---- Eigenschaften ----

    private static ulong FileTime(DateTime utc) => utc.Year < 1601 ? 0UL : (ulong)new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToFileTime();

    private Entry? Describe(Item item)
    {
        if (!_jail.TryResolve(item.Segments, out var full))
        {
            return null;
        }

        try
        {
            if (item.IsDirectory)
            {
                var d = new DirectoryInfo(full);
                return d.Exists ? new Entry(item.Segments.Length == 0 ? "" : item.Segments[^1], true, 0, d.CreationTimeUtc, d.LastAccessTimeUtc, d.LastWriteTimeUtc) : null;
            }

            var f = new FileInfo(full);
            return f.Exists ? new Entry(f.Name, false, f.Length, f.CreationTimeUtc, f.LastAccessTimeUtc, f.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void QueryInformation(Request request, ByteReader reader)
    {
        if (!reader.TryU32(out var infoClass) || !reader.TryU32(out var length) || Find(request.FileId) is not { } item)
        {
            ReplyData(request.Completion, StatusInvalidHandle, []);
            return;
        }

        var entry = Describe(item);
        if (entry is null)
        {
            ReplyData(request.Completion, StatusNoSuchFile, []);
            return;
        }

        var attributes = entry.IsDirectory ? AttrDirectory : AttrNormal;
        byte[] data;
        switch (infoClass)
        {
            case FileBasic:
                data = new ByteWriter(36).U64(FileTime(entry.Created)).U64(FileTime(entry.Accessed)).U64(FileTime(entry.Written)).U64(FileTime(entry.Written)).U32(attributes).ToArray();
                break;
            case FileStandard:
                data = new ByteWriter(22).U64(Allocation(entry.Size)).U64((ulong)entry.Size).U32(1).U8(item.DeletePending ? (byte)1 : (byte)0).U8(entry.IsDirectory ? (byte)1 : (byte)0).ToArray();
                break;
            case FileAttributeTag:
                data = new ByteWriter(8).U32(attributes).U32(0).ToArray();
                break;
            default:
                ReplyData(request.Completion, StatusNotSupported, []);
                return;
        }

        ReplyData(request.Completion, length < data.Length ? StatusBufferOverflow : StatusSuccess, length < data.Length ? data.AsSpan(0, (int)length) : data);
    }

    private static ulong Allocation(long size) => (ulong)((size + BlockSize - 1) / BlockSize * BlockSize);

    private void SetInformation(Request request, ByteReader reader)
    {
        static byte[] Done(uint length) => new ByteWriter(5).U32(length).U8(0).ToArray();

        if (!reader.TryU32(out var infoClass) || !reader.TryU32(out var length) || !reader.TrySkip(24) || !reader.TryBytes(length, out var buffer)
            || Find(request.FileId) is not { } item)
        {
            Reply(request.Completion, StatusInvalidParameter, Done(0));
            return;
        }

        // Nur Lesen (oder ein Handle ohne Schreibrecht): nichts davon darf etwas aendern
        if (!_drive.Writable)
        {
            Reply(request.Completion, StatusAccessDenied, Done(0));
            return;
        }

        switch (infoClass)
        {
            case FileBasic:
            {
                // Zeiten setzen (0 = nicht aendern); Attribute werden angenommen und ignoriert
                if (buffer.Length >= 36 && _jail.TryResolve(item.Segments, out var full) && item.Segments.Length > 0)
                {
                    var written = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..]);
                    if (written > 0)
                    {
                        try
                        {
                            var time = DateTime.FromFileTimeUtc(written);
                            if (item.IsDirectory)
                            {
                                Directory.SetLastWriteTimeUtc(full, time);
                            }
                            else
                            {
                                File.SetLastWriteTimeUtc(full, time);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                        {
                            // eine Zeit, die es nicht gibt: nicht gesetzt
                        }
                    }
                }

                Reply(request.Completion, StatusSuccess, Done(length));
                return;
            }

            case FileEndOfFile or FileAllocation:
            {
                if (item.IsDirectory || item.Handle is null || !item.CanWrite || buffer.Length < 8)
                {
                    Reply(request.Completion, item.IsDirectory ? StatusIsADirectory : StatusAccessDenied, Done(0));
                    return;
                }

                var size = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                if (size < 0 || size > MaxFileBytes)
                {
                    Reply(request.Completion, StatusDiskFull, Done(0));
                    return;
                }

                // Die Zuweisung (Platz reservieren) ist nur ein Hinweis; die Dateigroesse aendert nur EndOfFile
                if (infoClass == FileEndOfFile)
                {
                    try { JailFs.Truncate(item.Handle, size); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Reply(request.Completion, StatusUnsuccessful, Done(0));
                        return;
                    }
                }

                Reply(request.Completion, StatusSuccess, Done(length));
                return;
            }

            case FileDisposition:
            {
                var pending = buffer.Length == 0 || buffer[0] != 0;
                if (pending && item.Segments.Length == 0)
                {
                    Reply(request.Completion, StatusAccessDenied, Done(0));         // die Wurzel selbst wird nie geloescht
                    return;
                }

                if (pending && item.IsDirectory && _jail.TryResolve(item.Segments, out var dir) && HasChildren(dir))
                {
                    Reply(request.Completion, StatusDirectoryNotEmpty, Done(0));
                    return;
                }

                item.DeletePending = pending;
                Reply(request.Completion, StatusSuccess, Done(length));
                return;
            }

            case FileRename:
                Rename(request, item, buffer, length);
                return;

            default:
                Reply(request.Completion, StatusNotSupported, Done(0));
                return;
        }
    }

    private static bool HasChildren(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;                // unlesbar: lieber nicht loeschen
        }
    }

    private void Rename(Request request, Item item, ReadOnlySpan<byte> buffer, uint length)
    {
        static byte[] Done(uint length) => new ByteWriter(5).U32(length).U8(0).ToArray();

        // ReplaceIfExists(1) RootDirectory(1) FileNameLength(4) FileName
        var reader = new ByteReader(buffer);
        if (!reader.TryU8(out var replace) || !reader.TryU8(out _) || !reader.TryU32(out var nameLength) || item.Segments.Length == 0)
        {
            Reply(request.Completion, StatusInvalidParameter, Done(0));
            return;
        }

        if (ReadPath(ref reader, nameLength) is not { } remote || _jail.Resolve(remote, out var target) is not { } to || target.Length == 0)
        {
            Reply(request.Completion, StatusNameInvalid, Done(0));
            return;
        }

        if (!_jail.TryResolve(item.Segments, out var from) || !ParentExists(target))
        {
            Reply(request.Completion, StatusPathNotFound, Done(0));
            return;
        }

        try
        {
            var targetIsDirectory = Directory.Exists(to);
            if (File.Exists(to) || targetIsDirectory)
            {
                if (replace == 0)
                {
                    Reply(request.Completion, StatusNameCollision, Done(0));
                    return;
                }

                if (targetIsDirectory || item.IsDirectory)
                {
                    Reply(request.Completion, StatusAccessDenied, Done(0));         // ersetzt wird nur eine Datei durch eine Datei
                    return;
                }
            }

            if (item.IsDirectory)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite: replace != 0);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Reply(request.Completion, StatusAccessDenied, Done(0));
            return;
        }

        item.Segments = target;
        Reply(request.Completion, StatusSuccess, Done(length));
    }

    private void QueryDirectory(Request request, ByteReader reader)
    {
        if (!reader.TryU32(out var infoClass) || !reader.TryU8(out var initial) || !reader.TryU32(out var pathLength) || !reader.TrySkip(23)
            || Find(request.FileId) is not { } item || !item.IsDirectory || item.Handle is null)
        {
            ReplyData(request.Completion, Missing(request.FileId) ? StatusInvalidHandle : StatusNotADirectory, []);
            return;
        }

        if (initial != 0 || item.Listing is null)
        {
            // Das Suchmuster ist der Teil nach dem letzten Rueckstrich ("\*" oder "*"); es wird nie als Pfad benutzt
            var pattern = "*";
            if (pathLength > 0)
            {
                if (ReadPath(ref reader, pathLength) is not { } text)
                {
                    ReplyData(request.Completion, StatusInvalidParameter, []);
                    return;
                }

                pattern = text[(text.LastIndexOf('\\') + 1)..];
            }

            item.Listing = List(item, pattern);
            item.Next = 0;
        }

        if (item.Next >= item.Listing.Count)
        {
            ReplyData(request.Completion, StatusNoMoreFiles, []);
            return;
        }

        var entry = item.Listing[item.Next++];
        var name = Encoding.Unicode.GetBytes(entry.Name);
        var attributes = entry.IsDirectory ? AttrDirectory : AttrNormal;
        ByteWriter w;
        switch (infoClass)
        {
            case DirDirectory:
            case DirFull:
            case DirBoth:
            {
                var extra = infoClass == DirDirectory ? 0 : infoClass == DirFull ? 4 : 4 + 1 + 1 + 24;
                w = new ByteWriter(64 + extra + name.Length)
                    .U32(0).U32(0)
                    .U64(FileTime(entry.Created)).U64(FileTime(entry.Accessed)).U64(FileTime(entry.Written)).U64(FileTime(entry.Written))
                    .U64((ulong)entry.Size).U64(Allocation(entry.Size)).U32(attributes).U32((uint)name.Length);
                if (infoClass != DirDirectory)
                {
                    w.U32(0);               // EaSize
                }

                if (infoClass == DirBoth)
                {
                    w.U8(0).U8(0).Zeros(24);        // kein Kurzname
                }

                break;
            }

            case DirNames:
                w = new ByteWriter(12 + name.Length).U32(0).U32(0).U32((uint)name.Length);
                break;
            default:
                ReplyData(request.Completion, StatusNotSupported, []);
                return;
        }

        ReplyData(request.Completion, StatusSuccess, w.Bytes(name).ToArray());
    }

    // Die Eintraege eines Ordners (gelistet ueber den geoeffneten Deskriptor, nicht ueber den Namen): nur erlaubte Namen, nie ein Symlink.
    private List<Entry> List(Item item, string pattern)
    {
        var list = new List<Entry>();
        if (item.Handle is null)
        {
            return list;
        }

        var self = Describe(item);
        if (Matches(pattern, ".") && self is not null)
        {
            list.Add(self with { Name = "." });
        }

        if (Matches(pattern, "..") && self is not null)
        {
            list.Add(self with { Name = ".." });
        }

        try
        {
            foreach (var info in new DirectoryInfo(JailFs.FdPath(item.Handle)).EnumerateFileSystemInfos())
            {
                if (list.Count >= MaxEntries)
                {
                    break;
                }

                if (!DriveJail.IsAllowedName(info.Name) || info.LinkTarget is not null || !Matches(pattern, info.Name))
                {
                    continue;           // versteckt, nicht darstellbar oder ein Symlink: gibt es fuer den fernen Rechner nicht
                }

                var isDirectory = info is DirectoryInfo;
                list.Add(new Entry(info.Name, isDirectory, isDirectory ? 0 : (info as FileInfo)?.Length ?? 0, info.CreationTimeUtc, info.LastAccessTimeUtc, info.LastWriteTimeUtc));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // was nicht lesbar ist, fehlt in der Liste
        }

        return list;
    }

    /// <summary>Ein Suchmuster (DOS-Art: * und ?, dazu < > " wie in NTFS), Gross und Klein egal. "" und "*.*" meinen alles.</summary>
    public static bool Matches(string pattern, string name)
    {
        if (pattern.Length == 0 || pattern is "*" or "*.*")
        {
            return true;
        }

        return Match(pattern.AsSpan(), name.AsSpan());
    }

    private static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        // Rekursionsfrei mit Rueckfall auf den letzten Stern; Laenge durch Pfad und Namen begrenzt
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] is '*' or '<')
            {
                star = p++;
                mark = n;
            }
            else if (p < pattern.Length && (pattern[p] is '?' or '>' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]) || (pattern[p] == '"' && name[n] == '.')))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] is '*' or '<')
        {
            p++;
        }

        return p == pattern.Length;
    }

    // ---- Datentraeger ----

    private void QueryVolume(Request request, ByteReader reader)
    {
        if (!reader.TryU32(out var infoClass) || !reader.TryU32(out _))
        {
            ReplyData(request.Completion, StatusInvalidParameter, []);
            return;
        }

        long total = 0, free = 0;
        try
        {
            var info = new DriveInfo(_jail.Root);
            total = info.TotalSize;
            free = _drive.Writable ? info.AvailableFreeSpace : 0;       // ein Laufwerk nur zum Lesen meldet keinen freien Platz
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // unbekannt: 0
        }

        var units = (ulong)(total / BlockSize);
        var available = (ulong)(free / BlockSize);
        var label = Encoding.Unicode.GetBytes(_drive.Name);
        var fileSystem = Encoding.Unicode.GetBytes("FAT32");
        byte[] data;
        switch (infoClass)
        {
            case VolumeInfo:
            {
                DateTime created;
                try { created = Directory.GetCreationTimeUtc(_jail.Root); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { created = DateTime.UtcNow; }
                var serial = (uint)_jail.Root.Aggregate(17, (h, c) => unchecked(h * 31 + c));
                data = new ByteWriter(18 + label.Length).U64(FileTime(created)).U32(serial).U32((uint)label.Length).U8(0).U8(0).Bytes(label).ToArray();
                break;
            }

            case VolumeSize:
                data = new ByteWriter(24).U64(units).U64(available).U32(BlockSize / SectorSize).U32(SectorSize).ToArray();
                break;
            case VolumeFullSize:
                data = new ByteWriter(32).U64(units).U64(available).U64(available).U32(BlockSize / SectorSize).U32(SectorSize).ToArray();
                break;
            case VolumeDevice:
                // FILE_DEVICE_DISK, FILE_REMOTE_DEVICE (| FILE_READ_ONLY_DEVICE)
                data = new ByteWriter(8).U32(7).U32(_drive.Writable ? 0x10u : 0x12u).ToArray();
                break;
            case VolumeAttribute:
                // FILE_CASE_PRESERVED_NAMES | FILE_UNICODE_ON_DISK (| FILE_READ_ONLY_VOLUME), Namen bis 255 Zeichen
                data = new ByteWriter(12 + fileSystem.Length).U32(_drive.Writable ? 0x6u : 0x80006u).U32(255).U32((uint)fileSystem.Length).Bytes(fileSystem).ToArray();
                break;
            default:
                ReplyData(request.Completion, StatusNotSupported, []);
                return;
        }

        ReplyData(request.Completion, StatusSuccess, data);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _stop.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* abgebrochen */ }
        lock (_items)
        {
            foreach (var item in _items.Values)
            {
                item.Handle?.Dispose();
            }

            _items.Clear();
        }

        _fs.Dispose();
        _stop.Dispose();
    }
}
