using JulOS.Integrations.Display.Rdp.Channels;

namespace JulOS.Integrations.Display;

/// <summary>Ein Eintrag der Auswahl des fernen Rechners: der relative Pfad (Trenner /, schon geprueft), die Groesse (-1, wenn unbekannt; bei Ordnern 0) und ob es ein Ordner ist.</summary>
public readonly record struct RemoteFile(string Name, long Size, bool IsDirectory = false);

/// <summary>
/// Was mit dem Text der Zwischenablage geschah, fuer die Zwischenablage-Diagnose des Browsers und das Protokoll des Servers (nie der Inhalt, nur die Art
/// und die Laenge in Zeichen). Die Zahlen gehen so ueber das Netz (Nachricht 15 des <see cref="ScreenProtocol"/>).
/// </summary>
public enum ClipNote : byte
{
    /// <summary>Nur der Stand des Kanals (bereit, Dateien moeglich) hat sich geaendert, zum Beispiel weil chansrv der VM bereit meldet.</summary>
    Status = 0,

    /// <summary>Text der VM kam beim Browser an (Zeichen).</summary>
    TextFromVm = 1,

    /// <summary>Die VM hat auf die Anfrage nach ihrem Text nicht oder mit nichts geantwortet (Zeitablauf, verweigert, leer).</summary>
    TextFromVmLost = 2,

    /// <summary>Die VM hat den Text des Browsers beim Einfuegen abgeholt (Zeichen).</summary>
    TextFetchedByVm = 3,

    /// <summary>Der Text des Browsers ist zu lang und wurde nicht abgelegt (UTF-8-Bytes).</summary>
    TextTooLong = 4,

    /// <summary>Der Text der VM ist zu lang und wurde nicht weitergegeben (UTF-8-Bytes, bei sehr grossen Texten die Rohgroesse).</summary>
    TextFromVmTooLong = 5,

    /// <summary>Der Browser legte Text ab, aber der Kanal der VM ist noch nicht bereit; der Text wird nachgeliefert, sobald er es ist (Zeichen).</summary>
    TextWaiting = 6,

    /// <summary>Die VM hat die Ankuendigung des Browsers abgelehnt (Format-Liste verweigert).</summary>
    ListRefused = 7,
}

/// <summary>Wohin die Bruecke meldet, was der ferne Rechner kopiert hat. Wird auf dem Lesefaden des RDP-Clients gerufen: schnell zurueckkehren.</summary>
public sealed class ClipboardSink
{
    /// <summary>Text (UTF-8 in der Naehe von <see cref="ClipboardBridge.MaxTextBytes"/>).</summary>
    public Action<string>? Text { get; init; }

    /// <summary>Ein Bild: Format (1 PNG, 2 BMP) und die Bytes.</summary>
    public Action<byte, byte[]>? Image { get; init; }

    /// <summary>Dateien und Ordner (Stapel-Nummer, Liste); eine leere Liste heisst: die Auswahl ist weg.</summary>
    public Action<uint, IReadOnlyList<RemoteFile>>? Files { get; init; }

    /// <summary>Ein Download ist vorbereitet (Stapel, Download): der Browser holt ihn mit einer eigenen Anfrage ab (<see cref="ClipDownload.RunAsync"/>).</summary>
    public Action<uint, ClipDownload>? Download { get; init; }

    /// <summary>Stand eines Downloads: Stapel, Zustand (<see cref="ClipboardBridge.StateRunning"/> ...), uebertragen, gesamt (-1 unbekannt), Text (bei Scheitern der Grund).</summary>
    public Action<uint, byte, long, long, string>? DownloadState { get; init; }

    /// <summary>Ergebnis eines Sendens vom Browser (Nummer, Code, Text); Nummer 0 betrifft nichts, was der Browser gestartet hat.</summary>
    public Action<uint, byte, string>? Result { get; init; }

    /// <summary>Ein Ereignis der Text-Zwischenablage (Art und eine Zahl, siehe <see cref="ClipNote"/>); ohne Inhalt.</summary>
    public Action<ClipNote, int>? Note { get; init; }
}

/// <summary>
/// Zwischenablage zwischen Browser und fernem Rechner, beide Richtungen: Text, Bilder, Dateien und Ordner. Der Kanal (<see cref="ClipboardChannel"/>)
/// kennt nur Ankuendigungen und Anfragen: wer etwas kopiert, sagt nur, welche Formate er haette, und erst beim Einfuegen wird der Inhalt geholt.
///
/// Ferner Rechner kopiert: Dateien und Ordner (Liste mit Pfaden und Groessen, der Inhalt erst, wenn der Browser einen Download anfordert), sonst Text (sofort
/// geholt, der Browser hat keine Zwischenablage, die er auf Anfrage fuellen koennte), sonst ein Bild (PNG, sonst CF_DIB als BMP). Ein Download laeuft als
/// HTTP-Strom (<see cref="ClipDownload"/>): der Server holt die Stuecke der Reihe nach vom fernen Rechner und gibt jedes erst weiter, wenn das vorige beim
/// Browser angekommen ist (Rueckdruck); nichts wird zusammengesetzt. Eine Datei kommt als sie selbst, ein Ordner oder mehrere Eintraege als ZIP.
/// Browser legt etwas ab: es wird angekuendigt und geliefert, wenn der ferne Rechner einfuegt. Die neueste Ablage ersetzt die vorige. Bilder liegen im
/// Speicher; Dateien und Ordner kommen in Stuecken (<see cref="BeginTransfer"/> ...) und liegen als Dateien im Ablageordner (<see cref="ClipboardOptions"/>),
/// bis der ferne Rechner sie liest (Bereichsanfragen) oder etwas Neues abgelegt wird.
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    /// <summary>Laenger darf ein Text auf dem Weg zwischen Browser und Server nicht sein (UTF-8, in beide Richtungen).</summary>
    public const int MaxTextBytes = 1024 * 1024;

    /// <summary>Das Bild als BMP (oder PNG vom fernen Rechner); die Fotos vom Handy werden vorher verkleinert.</summary>
    public const int MaxImageBytes = 32 * 1024 * 1024;

    public const int MaxChunkBytes = 256 * 1024;

    /// <summary>Hoechstens so viele Eintraege in der Auswahl des fernen Rechners; mehr wird nicht angeboten (eine halbe Auswahl waere falsch).</summary>
    public const int MaxRemoteEntries = 10000;

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    public const byte KindImage = 1, KindFile = 2, KindFolder = 3;
    public const byte ResultOk = 0, ResultTooBig = 1, ResultNotAllowed = 2, ResultNoClipboard = 3, ResultOther = 4;
    public const byte StateReady = 0, StateRunning = 1, StateDone = 2, StateFailed = 3;
    public const byte ImagePng = 1, ImageBmp = 2;

    // So gross darf die Anforderung an den fernen Rechner sein: ein Stueck pro Anfrage
    private const int RangeBytes = 1024 * 1024;
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(1500);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private enum Expect { Text, Image, ImageIsPng, FileList }

    private enum LocalKind { None, Text, Image, Files }

    // Ein Eintrag der Ablage vom Browser: eine Datei (im Ablageordner) oder ein Ordner. Pfad relativ, Trenner \
    private sealed record LocalEntry(string Path, long Size, bool IsDirectory, string? File);

    // Der Ablageordner einer Ablage: wird mit ihr geloescht
    private sealed class Stash : IDisposable
    {
        public readonly string Directory;
        public readonly List<LocalEntry> Entries = [];
        public readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);
        public long Bytes;
        public int Files;

        public Stash(string root)
        {
            Directory = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // wird beim naechsten Start aufgeraeumt (Sweep)
            }
        }
    }

    private sealed class Upload
    {
        public uint Id;
        public byte Kind;
        public string Path = "";
        public long Size;
        public long Received;
        public byte[] Data = [];            // Bild
        public FileStream? File;            // Datei
        public string? FilePath;
        public Timer? Idle;
    }

    private readonly ClipboardChannel _channel;
    private readonly ClipboardSink _sink;
    private readonly Lock _gate = new();
    private readonly TimeSpan _idle;
    private readonly ClipboardOptions _options;

    // was der Browser zuletzt abgelegt hat
    private LocalKind _localKind;
    private byte[]? _local;
    private string? _lastLocal;
    private long _textEchoUntil;
    private byte[]? _dib;
    private Stash? _localFiles;
    private long _echoUntil;
    private bool _peerCrLf;

    // eine laufende Uebertragung vom Browser, und die Ablage, die noch nicht vollstaendig ist
    private Upload? _upload;
    private Stash? _batch;
    private int _batchCount;
    private int _batchReceived;

    // Antworten auf unsere Datenanfragen kommen in der Reihenfolge der Anfragen
    private readonly Queue<Expect> _expected = new();

    // was der ferne Rechner zuletzt an Dateien angeboten hat
    private uint _remoteBatch;
    private uint _lockCounter;
    private uint _streamCounter;
    private List<DownloadEntry> _remoteEntries = [];
    private uint? _remoteLock;
    private ClipDownload? _download;
    private readonly Dictionary<uint, TaskCompletionSource<byte[]?>> _ranges = [];
    private bool _disposed;

    /// <param name="remoteText">Der ferne Rechner hat Text kopiert. Wird auf dem Lesefaden des RDP-Clients gerufen: schnell zurueckkehren.</param>
    public ClipboardBridge(ClipboardChannel channel, Action<string> remoteText)
        : this(channel, new ClipboardSink { Text = remoteText })
    {
    }

    /// <param name="idleTimeout">So lange darf eine Uebertragung vom Browser ohne neue Daten stehen (Standard <see cref="IdleTimeout"/>).</param>
    /// <param name="options">Grenzen und Ablageordner (Standard <see cref="ClipboardOptions.Default"/>).</param>
    public ClipboardBridge(ClipboardChannel channel, ClipboardSink sink, TimeSpan? idleTimeout = null, ClipboardOptions? options = null)
    {
        _idle = idleTimeout ?? IdleTimeout;
        _options = options ?? ClipboardOptions.Default;
        _channel = channel;
        _sink = sink;
        channel.RemoteFormats += OnRemoteFormats;
        channel.Ready += () => _sink.Note?.Invoke(ClipNote.Status, 0);
        channel.FormatsAcknowledged += accepted =>
        {
            if (!accepted)
            {
                _sink.Note?.Invoke(ClipNote.ListRefused, 0);
            }
        };
        channel.DataReceived += OnDataReceived;
        channel.DataRequested += OnDataRequested;
        channel.FileContentsRequested += OnFileContentsRequested;
        channel.FileContentsReceived += OnFileContentsReceived;
    }

    /// <summary>Die Grenzen, die diese Bruecke einhaelt (der Browser bekommt sie gemeldet).</summary>
    public ClipboardOptions Options => _options;

    public void Dispose()
    {
        List<TaskCompletionSource<byte[]?>> waiting;
        ClipDownload? download;
        Stash?[] stashes;
        lock (_gate)
        {
            _disposed = true;
            DropUpload();
            download = _download;
            _download = null;
            waiting = [.. _ranges.Values];
            _ranges.Clear();
            stashes = [_batch, _localFiles];
            _batch = null;
            _localFiles = null;
        }

        download?.Stop();
        foreach (var w in waiting)
        {
            w.TrySetCanceled();
        }

        foreach (var stash in stashes)
        {
            stash?.Dispose();
        }
    }

    /// <summary>Raeumt Ablageordner weg, die ein abgestuerzter Server zurueckgelassen hat (aelter als 12 Stunden). Wird beim Start einmal gerufen.</summary>
    public static void SweepOld(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromHours(12))
                {
                    try { Directory.Delete(dir, true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* naechstes Mal */ }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // ohne Ablageordner gibt es keine Aufraeumarbeit
        }
    }

    // ---------------------------------------------------------------- Browser legt ab

    /// <summary>
    /// Der Browser hat Text abgelegt: der ferne Rechner bekommt ihn, wenn er einfuegt. Zu langer Text wird nicht abgelegt (der Browser erfaehrt es ueber
    /// <see cref="ClipNote.TextTooLong"/>). Zeilenenden: LF, bis der ferne Rechner Text mit CR LF geschickt hat (ein Windows-Rechner), siehe <see cref="LineEnding"/>.
    /// </summary>
    public void SetLocalText(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxTextBytes)
        {
            _sink.Note?.Invoke(ClipNote.TextTooLong, bytes);
            return;
        }

        byte[] data;
        Stash? old;
        lock (_gate)
        {
            data = ClipboardText.Encode(text, UseCrLf);
            _local = data;
            _lastLocal = text.Replace("\r\n", "\n");
            _textEchoUntil = Environment.TickCount64 + (long)EchoWindow.TotalMilliseconds;
            _localKind = LocalKind.Text;
            _dib = null;
            old = _localFiles;
            _localFiles = null;
        }

        old?.Dispose();
        _channel.AnnounceFormats([new ClipFormat(ClipboardText.FormatId, null)]);
        if (!_channel.IsReady)
        {
            _sink.Note?.Invoke(ClipNote.TextWaiting, text.Length);
        }
    }

    /// <summary>Wie die Zeilenenden des Textes aus dem Browser gehen: Standard <see cref="ClipboardLineEnding.Auto"/>.</summary>
    public ClipboardLineEnding LineEnding => _options.LineEnding;

    // Unter _gate. Auto: LF (Linux, xrdp reicht den Text unveraendert weiter und ein CR landet im Terminal und im Editor), CR LF erst, wenn der ferne Rechner selbst
    // Text mit CR LF und ohne einzelnes LF geschickt hat (Windows).
    private bool UseCrLf => _options.LineEnding switch
    {
        ClipboardLineEnding.CrLf => true,
        ClipboardLineEnding.Lf => false,
        _ => _peerCrLf,
    };

    /// <summary>
    /// Der Browser faengt an, ein Bild (Art 1, als BMP), eine Datei (Art 2) oder einen leeren Ordner (Art 3, Groesse 0) zu senden. <paramref name="name"/> ist bei Dateien
    /// und Ordnern ein relativer Pfad (Trenner / oder \): ein Pfad mit "..", ein absoluter Pfad oder ein Laufwerk wird abgelehnt. Ordner mit Inhalt entstehen aus den
    /// Pfaden der Dateien. Fehler kommen als Ergebnis zurueck.
    /// </summary>
    public void BeginTransfer(uint id, byte kind, int index, int count, long size, string name)
    {
        string? error = null;
        byte code = ResultOther;
        IReadOnlyList<string>? parts = null;
        lock (_gate)
        {
            DropUpload();
            if (_disposed)
            {
                return;
            }

            if (kind is not (KindImage or KindFile or KindFolder))
            {
                (code, error) = (ResultOther, "Unbekannte Art.");
            }
            else if (!_channel.IsReady)
            {
                (code, error) = (ResultNoClipboard, "Die VM hat keine Zwischenablage (Kanal fehlt).");
            }
            else if (kind == KindImage)
            {
                if (size <= 0)
                {
                    (code, error) = (ResultOther, "Das Bild ist leer.");
                }
                else if (size > MaxImageBytes)
                {
                    (code, error) = (ResultTooBig, "Bild zu groß (höchstens 14 MB).");
                }
            }
            else
            {
                (code, error) = CheckEntry(kind, index, count, size, name, out parts);
            }

            if (error is null)
            {
                var upload = new Upload { Id = id, Kind = kind, Size = size };
                try
                {
                    if (kind == KindImage)
                    {
                        upload.Data = new byte[size];
                    }
                    else
                    {
                        if (index == 0)
                        {
                            _batch?.Dispose();
                            _batch = new Stash(_options.TempRoot);
                            _batchCount = count;
                            _batchReceived = 0;
                        }

                        upload.Path = string.Join('\\', parts!);
                        if (kind == KindFile && size > 0)
                        {
                            upload.FilePath = Path.Combine(_batch!.Directory, "f" + index);
                            upload.File = new FileStream(upload.FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
                        }
                    }

                    upload.Idle = new Timer(_ => Expire(upload), null, _idle, Timeout.InfiniteTimeSpan);
                    _upload = upload;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    upload.File?.Dispose();
                    (code, error) = (ResultOther, "Auf dem Server kann nicht abgelegt werden.");
                }
            }
        }

        if (error is not null)
        {
            _sink.Result?.Invoke(id, code, error);
        }
    }

    // Prueft einen Eintrag gegen die Regeln (unter _gate). Fehler: Code und Text.
    private (byte Code, string? Error) CheckEntry(byte kind, int index, int count, long size, string name, out IReadOnlyList<string>? parts)
    {
        parts = null;
        if (!_channel.CanTransferFiles)
        {
            return (ResultNoClipboard, "Die VM unterstützt keine Dateien in der Zwischenablage.");
        }

        if (count is < 1 or > ClipboardOptions.MaxEntries || index < 0 || index >= count)
        {
            return (ResultTooBig, $"Zu viele Dateien (höchstens {ClipboardOptions.MaxFiles}).");
        }

        if (kind == KindFolder ? size != 0 : size < 0 || (size == 0 && count == 1))
        {
            return (ResultOther, kind == KindFile ? "Die Datei ist leer." : "Ungültiger Ordner.");
        }

        if (size > _options.MaxFileBytes || (index == 0 ? 0 : _batch?.Bytes ?? 0) + size > _options.MaxFileBytes)
        {
            return (ResultTooBig, $"Datei zu groß (höchstens {FormatLimit(_options.MaxFileBytes)}).");
        }

        parts = ClipboardNames.SafePath(name);
        if (parts is null)
        {
            return (ResultNotAllowed, "Ungültiger Dateiname oder Pfad.");
        }

        if (kind == KindFile && ClipboardNames.BlockedExtension(parts[^1]) is { } blocked)
        {
            return (ResultNotAllowed, $"Dateityp nicht erlaubt ({blocked}).");
        }

        if (index == 0)
        {
            // der Platz auf dem Datentraeger: eine Ablage, die ihn fuellt, wird gar nicht erst angenommen
            if (!HasRoom(_options.TempRoot, Math.Min(_options.MaxFileBytes, Math.Max(size, 1))))
            {
                return (ResultOther, "Auf dem Server ist nicht genug Platz.");
            }
        }
        else if (count != _batchCount || index != _batchReceived || _batch is null)
        {
            return (ResultOther, "Die Reihenfolge der Dateien stimmt nicht.");
        }

        var batch = index == 0 ? null : _batch;
        var key = string.Join('\\', parts);
        if (batch is not null)
        {
            if (kind == KindFile && batch.Files >= ClipboardOptions.MaxFiles)
            {
                return (ResultTooBig, $"Zu viele Dateien (höchstens {ClipboardOptions.MaxFiles}).");
            }

            if (batch.Paths.Contains(key))
            {
                return (ResultNotAllowed, "Doppelter Name in der Auswahl.");
            }
        }
        else if (kind == KindFile && ClipboardOptions.MaxFiles < 1)
        {
            return (ResultTooBig, "Zu viele Dateien.");
        }

        return (ResultOk, null);
    }

    private static string FormatLimit(long bytes) => bytes % (1024L * 1024 * 1024) == 0 ? $"{bytes / (1024L * 1024 * 1024)} GB" : $"{bytes / (1024 * 1024)} MB";

    private static bool HasRoom(string root, long needed)
    {
        try
        {
            Directory.CreateDirectory(root);
            return new DriveInfo(Path.GetFullPath(root)).AvailableFreeSpace >= needed + ClipboardOptions.FreeSpaceReserve;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;       // nicht messbar: nicht blockieren, ein volles Laufwerk meldet sich beim Schreiben
        }
    }

    public void TransferChunk(uint id, ReadOnlySpan<byte> data)
    {
        string? error = null;
        byte code = ResultOther;
        lock (_gate)
        {
            if (_upload is not { } upload || upload.Id != id)
            {
                return;
            }

            if (data.Length > MaxChunkBytes || upload.Received + data.Length > upload.Size)
            {
                DropUpload();
                (code, error) = (ResultOther, "Mehr Daten als angekündigt.");
            }
            else
            {
                try
                {
                    if (upload.File is { } file)
                    {
                        file.Write(data);
                    }
                    else
                    {
                        data.CopyTo(upload.Data.AsSpan((int)upload.Received));
                    }

                    upload.Received += data.Length;
                    upload.Idle?.Change(_idle, Timeout.InfiniteTimeSpan);
                }
                catch (IOException)
                {
                    DropUpload();
                    (code, error) = (ResultOther, "Auf dem Server konnte nicht geschrieben werden (Platz?).");
                }
            }
        }

        if (error is not null)
        {
            _sink.Result?.Invoke(id, code, error);
        }
    }

    public void CancelTransfer(uint id)
    {
        lock (_gate)
        {
            if (_upload is { } upload && upload.Id == id)
            {
                DropUpload();
            }
        }
    }

    /// <summary>Der Browser ist mit dem Senden fertig: nur wenn alles angekommen ist, wird es abgelegt (und bei Dateien, wenn die Reihe komplett ist).</summary>
    public void EndTransfer(uint id)
    {
        byte code = ResultOk;
        string message = "";
        List<ClipFormat>? announce = null;
        Stash? old = null;
        lock (_gate)
        {
            if (_upload is not { } upload || upload.Id != id)
            {
                return;
            }

            DropUpload(keepFile: upload.Received == upload.Size);
            if (upload.Received != upload.Size)
            {
                (code, message) = (ResultOther, "Die Übertragung war unvollständig.");
            }
            else if (upload.Kind == KindImage)
            {
                var dib = DeviceIndependentBitmap.FromBmp(upload.Data);
                if (dib is null)
                {
                    (code, message) = (ResultNotAllowed, "Kein gültiges Bild.");
                }
                else
                {
                    old = _localFiles;
                    _dib = dib;
                    _local = null;
                    _lastLocal = null;
                    _localFiles = null;
                    _localKind = LocalKind.Image;
                    _echoUntil = Environment.TickCount64 + (long)EchoWindow.TotalMilliseconds;
                    announce = [new ClipFormat(DeviceIndependentBitmap.FormatId, null)];
                }
            }
            else if (_batch is { } batch)
            {
                batch.Entries.Add(new LocalEntry(upload.Path, upload.Size, upload.Kind == KindFolder, upload.FilePath));
                batch.Paths.Add(upload.Path);
                batch.Bytes += upload.Size;
                if (upload.Kind == KindFile)
                {
                    batch.Files++;
                }

                _batchReceived++;
                if (_batchReceived == _batchCount)
                {
                    old = _localFiles;
                    Complete(batch);
                    _localFiles = batch;
                    _batch = null;
                    _local = null;
                    _lastLocal = null;
                    _dib = null;
                    _localKind = LocalKind.Files;
                    _echoUntil = Environment.TickCount64 + (long)EchoWindow.TotalMilliseconds;
                    announce = [new ClipFormat(FileGroupDescriptor.DefaultFormatId, FileGroupDescriptor.FormatName)];
                }
            }
        }

        if (announce is not null)
        {
            try
            {
                _channel.AnnounceFormats(announce);
            }
            catch (ArgumentException)
            {
                (code, message) = (ResultOther, "Die Zwischenablage nimmt das nicht an.");
            }
        }

        old?.Dispose();
        _sink.Result?.Invoke(id, code, message);
    }

    // Aus den Eintraegen die Liste machen, die der ferne Rechner sieht: fehlende Ordner ergaenzen (jeder Pfad einer Datei hat seine Ordner), und
    // jeder Ordner steht vor seinem Inhalt (so verlangt es die Liste), sonst nach dem Namen geordnet.
    private static void Complete(Stash stash)
    {
        var known = new HashSet<string>(stash.Entries.Where(e => e.IsDirectory).Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in stash.Entries.ToArray())
        {
            var at = -1;
            while ((at = entry.Path.IndexOf('\\', at + 1)) > 0)
            {
                var folder = entry.Path[..at];
                if (known.Add(folder))
                {
                    stash.Entries.Add(new LocalEntry(folder, 0, true, null));
                }
            }
        }

        stash.Entries.Sort((a, b) => ComparePaths(a.Path, b.Path));
    }

    private static int ComparePaths(string a, string b)
    {
        var x = a.Split('\\');
        var y = b.Split('\\');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var c = string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0)
            {
                return c;
            }
        }

        return x.Length - y.Length;
    }

    // unter _gate; keepFile: die Datei ist vollstaendig und wird abgelegt
    private void DropUpload(bool keepFile = false)
    {
        if (_upload is { } upload)
        {
            upload.Idle?.Dispose();
            upload.File?.Dispose();
            if (!keepFile && upload.FilePath is { } path)
            {
                try { File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* mit dem Ordner */ }
            }
        }

        _upload = null;
    }

    private void Expire(Upload upload)
    {
        lock (_gate)
        {
            if (_upload != upload)
            {
                return;
            }

            DropUpload();
        }

        _sink.Result?.Invoke(upload.Id, ResultOther, "Die Übertragung wurde abgebrochen (keine Daten mehr).");
    }

    // ---------------------------------------------------------------- Ferner Rechner kopiert

    private void OnRemoteFormats(IReadOnlyList<ClipFormat> formats)
    {
        var fileFormat = FileGroupDescriptor.FindFormat(formats);
        var hasText = formats.Any(f => f.Id == ClipboardText.FormatId && f.Name is null);
        var png = formats.FirstOrDefault(f => string.Equals(f.Name, "PNG", StringComparison.OrdinalIgnoreCase));
        var dib = formats.FirstOrDefault(f => f.Name is null && f.Id == ClipboardFormats.Dib)
            ?? formats.FirstOrDefault(f => f.Name is null && f.Id == ClipboardFormats.DibV5);

        // Was wir eben selbst angekuendigt haben, kommt bei manchen Servern als neue Kopie zurueck
        bool echo;
        lock (_gate)
        {
            echo = Environment.TickCount64 < _echoUntil
                && ((_localKind == LocalKind.Files && fileFormat is not null) || (_localKind == LocalKind.Image && (png is not null || dib is not null)));
        }

        if (echo)
        {
            return;
        }

        if (fileFormat is { } fileId && _channel.CanTransferFiles)
        {
            lock (_gate)
            {
                _expected.Enqueue(Expect.FileList);
            }

            _channel.RequestData(fileId);
        }
        else if (hasText)
        {
            lock (_gate)
            {
                _expected.Enqueue(Expect.Text);
            }

            _channel.RequestData(ClipboardText.FormatId);
        }
        else if (png is not null)
        {
            lock (_gate)
            {
                _expected.Enqueue(Expect.ImageIsPng);
            }

            _channel.RequestData(png.Id);
        }
        else if (dib is not null)
        {
            lock (_gate)
            {
                _expected.Enqueue(Expect.Image);
            }

            _channel.RequestData(dib.Id);
        }
    }

    private void OnDataReceived(byte[]? data)
    {
        Expect kind;
        lock (_gate)
        {
            kind = _expected.Count > 0 ? _expected.Dequeue() : Expect.Text;
        }

        if (data is null)
        {
            if (kind == Expect.Text)
            {
                _sink.Note?.Invoke(ClipNote.TextFromVmLost, 0);
            }

            return;
        }

        switch (kind)
        {
            case Expect.Text:
                ReceivedText(data);
                break;
            case Expect.ImageIsPng:
                if (data.Length <= MaxImageBytes && data.AsSpan().StartsWith(PngSignature))
                {
                    _sink.Image?.Invoke(ImagePng, data);
                }
                else if (data.Length > MaxImageBytes)
                {
                    _sink.Result?.Invoke(0, ResultTooBig, "Das Bild aus der VM ist zu groß.");
                }

                break;
            case Expect.Image:
                if (data.Length > MaxImageBytes)
                {
                    _sink.Result?.Invoke(0, ResultTooBig, "Das Bild aus der VM ist zu groß.");
                }
                else if (DeviceIndependentBitmap.ToBmp(data) is { } bmp)
                {
                    _sink.Image?.Invoke(ImageBmp, bmp);
                }

                break;
            case Expect.FileList:
                ReceivedFileList(data);
                break;
        }
    }

    private void ReceivedText(byte[] data)
    {
        if (data.Length > 2 * MaxTextBytes)
        {
            _sink.Note?.Invoke(ClipNote.TextFromVmTooLong, data.Length / 2);
            return;
        }

        var text = ClipboardText.Decode(data, out var crlf, out var lf);
        if (text.Length == 0)
        {
            _sink.Note?.Invoke(ClipNote.TextFromVmLost, 0);
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxTextBytes)
        {
            _sink.Note?.Invoke(ClipNote.TextFromVmTooLong, bytes);
            return;
        }

        // Manche Server melden den Text, den wir ihnen eben gegeben haben, als neue Kopie zurueck: das ist keine Neuigkeit fuer den Browser, aber nur kurz nach dem
        // Ablegen (wer denselben Text spaeter in der VM wieder kopiert, will ihn im Browser haben)
        lock (_gate)
        {
            if (crlf > 0 && lf == 0)
            {
                _peerCrLf = true;
            }
            else if (lf > 0 && crlf == 0)
            {
                _peerCrLf = false;
            }

            if (_lastLocal is not null && text == _lastLocal && Environment.TickCount64 < _textEchoUntil)
            {
                return;
            }

            _lastLocal = null;
        }

        _sink.Text?.Invoke(text);
        _sink.Note?.Invoke(ClipNote.TextFromVm, text.Length);
    }

    private void ReceivedFileList(byte[] data)
    {
        var files = FileGroupDescriptor.Decode(data);
        if (files is null)
        {
            return;
        }

        // Pfade, die nicht schlicht und relativ sind (Weg nach oben, Laufwerk, verbotene Zeichen), werden nicht angeboten; was darin liegt, hat einen eigenen Pfad
        var list = new List<DownloadEntry>();
        var tooMany = files.Count > MaxRemoteEntries;
        for (var i = 0; i < files.Count && !tooMany; i++)
        {
            if (FileGroupDescriptor.PathParts(files[i].Name) is not { } parts)
            {
                continue;
            }

            list.Add(new DownloadEntry(i, string.Join('/', parts), files[i].IsDirectory ? 0 : files[i].Size, files[i].IsDirectory, files[i].LastWriteTimeUtc));
        }

        if (tooMany)
        {
            list.Clear();
            _sink.Result?.Invoke(0, ResultTooBig, $"Zu viele Dateien in der VM (höchstens {MaxRemoteEntries}).");
        }

        uint batch;
        uint? oldLock;
        uint? newLock = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            batch = ++_remoteBatch;
            _remoteEntries = list;
            oldLock = _remoteLock;
            _remoteLock = null;
            if (list.Count > 0 && _channel.CanLock)
            {
                newLock = ++_lockCounter;
                _remoteLock = newLock;
            }
        }

        // Die Liste festhalten, damit eine Datei auch noch geholt werden kann, wenn der ferne Rechner schon etwas anderes kopiert hat
        if (newLock is { } id && !_channel.Lock(id))
        {
            lock (_gate)
            {
                _remoteLock = null;
            }
        }

        if (oldLock is { } previous)
        {
            _channel.Unlock(previous);
        }

        _sink.Files?.Invoke(batch, list.Select(f => new RemoteFile(f.Path, f.Size, f.IsDirectory)).ToArray());
    }

    // ---------------------------------------------------------------- Browser holt Dateien

    /// <summary>
    /// Der Browser will den Eintrag <paramref name="path"/> aus der zuletzt gemeldeten Liste (leer: alles): eine Datei als sie selbst, ein Ordner mit allem darin
    /// oder alles zusammen als ZIP. Kommt der Eintrag in Frage, meldet <see cref="ClipboardSink.Download"/> den vorbereiteten Download; sonst
    /// <see cref="ClipboardSink.DownloadState"/> den Grund. Es laeuft hoechstens ein Download.
    /// </summary>
    public void RequestFile(uint batch, string path)
    {
        string? error = null;
        ClipDownload? download = null;
        lock (_gate)
        {
            if (_download is not null)
            {
                error = "Es läuft schon ein Download.";
            }
            else if (batch != _remoteBatch || _remoteEntries.Count == 0)
            {
                error = "Die Auswahl in der VM hat sich geändert.";
            }
            else
            {
                download = Select(batch, path);
                if (download is null)
                {
                    error = "Diesen Eintrag gibt es nicht (mehr).";
                }
                else
                {
                    _download = download;
                    download.WaitingTimer = new Timer(_ => FailUnstarted(download), null, ReadyTimeout, Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (error is not null)
        {
            _sink.DownloadState?.Invoke(batch, StateFailed, 0, 0, error);
            return;
        }

        _sink.Download?.Invoke(batch, download!);
    }

    // unter _gate
    private ClipDownload? Select(uint batch, string path)
    {
        var wanted = (path ?? "").Replace('\\', '/').Trim('/');
        var all = _remoteEntries;
        if (wanted.Length == 0)
        {
            if (all.Count == 1 && !all[0].IsDirectory)
            {
                return Single(batch, all[0]);
            }

            return Archive(batch, "Dateien-aus-der-VM.zip", all, 0);
        }

        var exact = all.FirstOrDefault(e => string.Equals(e.Path, wanted, StringComparison.Ordinal));
        if (exact is { IsDirectory: false })
        {
            return Single(batch, exact);
        }

        var prefix = wanted + "/";
        var below = all.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal) || string.Equals(e.Path, wanted, StringComparison.Ordinal)).ToList();
        if (below.Count == 0)
        {
            return null;
        }

        var cut = wanted.LastIndexOf('/') + 1;
        return Archive(batch, wanted[cut..] + ".zip", below, cut);
    }

    private ClipDownload Single(uint batch, DownloadEntry e)
    {
        var name = e.Path[(e.Path.LastIndexOf('/') + 1)..];
        return new ClipDownload(this, batch, name, false, e.Size, e.Size, [e], 0, _remoteLock);
    }

    private ClipDownload Archive(uint batch, string name, List<DownloadEntry> entries, int cut)
    {
        var total = entries.Any(e => !e.IsDirectory && e.Size < 0) ? -1 : entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        return new ClipDownload(this, batch, name, true, -1, total, entries, cut, _remoteLock);
    }

    /// <summary>Der Browser bricht den Download ab (oder die Seite geht zu): der Strom endet, der Platz fuer einen neuen ist frei.</summary>
    public void CancelFile(uint batch)
    {
        ClipDownload? download;
        lock (_gate)
        {
            download = _download is { } d && d.Batch == batch ? d : null;
        }

        download?.Stop();
        if (download is not null)
        {
            Release(download);
        }
    }

    // Der Download wurde nicht innerhalb der Frist abgeholt
    private void FailUnstarted(ClipDownload d)
    {
        bool started;
        lock (_gate)
        {
            started = d.Started || _download != d;
        }

        if (started)
        {
            return;
        }

        Release(d);
        _sink.DownloadState?.Invoke(d.Batch, StateFailed, 0, 0, "Der Download wurde nicht gestartet.");
    }

    private void Release(ClipDownload d)
    {
        lock (_gate)
        {
            if (_download != d)
            {
                return;
            }

            _download = null;
        }

        d.WaitingTimer?.Dispose();
        d.WaitingTimer = null;
    }

    // Eine Anfrage an den fernen Rechner (Groesse oder ein Stueck) und warten auf die Antwort (Null: verweigert)
    internal async Task<byte[]?> AskAsync(ClipDownload d, int source, long offset, int count, bool wantSize, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint stream;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stream = ++_streamCounter;
            _ranges[stream] = answer;
        }

        try
        {
            try
            {
                if (wantSize)
                {
                    _channel.RequestFileSize(stream, source, d.LockId);
                }
                else
                {
                    _channel.RequestFileRange(stream, source, offset, count, d.LockId);
                }
            }
            catch (ArgumentException)
            {
                throw new IOException("Die Datei ist nicht mehr da.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AnswerTimeout);
            try
            {
                return await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("Die VM antwortet nicht.");
            }
        }
        finally
        {
            lock (_gate)
            {
                _ranges.Remove(stream);
            }
        }
    }

    private void OnFileContentsReceived(uint streamId, byte[]? data)
    {
        TaskCompletionSource<byte[]?>? answer;
        lock (_gate)
        {
            _ranges.TryGetValue(streamId, out answer);
        }

        answer?.TrySetResult(data);
    }

    internal bool Begin(ClipDownload d)
    {
        lock (_gate)
        {
            if (_download != d || d.Started || _disposed)
            {
                return false;
            }

            d.Started = true;
            d.WaitingTimer?.Dispose();
            d.WaitingTimer = null;
            return true;
        }
    }

    internal void Report(ClipDownload d, byte state, long done, long total, string text) => _sink.DownloadState?.Invoke(d.Batch, state, done, total, text);

    internal void End(ClipDownload d) => Release(d);

    internal static TimeSpan ReportInterval => ProgressEvery;

    internal static int Range => RangeBytes;

    // ---------------------------------------------------------------- Der ferne Rechner fuegt ein

    private void OnDataRequested(uint formatId)
    {
        byte[]? data = null;
        var served = 0;
        lock (_gate)
        {
            switch (_localKind)
            {
                case LocalKind.Text when formatId == ClipboardText.FormatId:
                    data = _local;
                    served = _lastLocal?.Length ?? 0;
                    break;
                case LocalKind.Image when formatId == DeviceIndependentBitmap.FormatId:
                    data = _dib;
                    break;
                case LocalKind.Files when formatId == FileGroupDescriptor.DefaultFormatId && _localFiles is { } stash:
                    try
                    {
                        data = FileGroupDescriptor.Encode(stash.Entries.Select(f => new ClipFile(f.Path, f.IsDirectory ? 0 : f.Size, f.IsDirectory)).ToArray());
                    }
                    catch (ArgumentException)
                    {
                        data = null;          // ein Name, der nicht in die Liste passt: der ferne Rechner bekommt nichts
                    }

                    break;
            }
        }

        _channel.SendData(data);
        if (data is not null && served > 0)
        {
            _sink.Note?.Invoke(ClipNote.TextFetchedByVm, served);
        }
    }

    private void OnFileContentsRequested(FileContentsRequest request)
    {
        byte[]? data = null;
        long? size = null;
        LocalEntry? entry = null;
        lock (_gate)
        {
            if (_localKind == LocalKind.Files && _localFiles is { } stash && request.Index >= 0 && request.Index < stash.Entries.Count)
            {
                entry = stash.Entries[request.Index];
            }
        }

        if (entry is { IsDirectory: false })
        {
            if (request.IsSizeRequest)
            {
                size = entry.Size;
            }
            else if (request.Offset >= entry.Size)
            {
                data = [];
            }
            else
            {
                data = ReadStashed(entry, request.Offset, (int)Math.Min(request.Count, entry.Size - request.Offset));
            }
        }

        if (size is { } s)
        {
            _channel.SendFileSize(request.StreamId, s);
        }
        else
        {
            _channel.SendFileContents(request.StreamId, data);
        }
    }

    // Ein Bereich einer abgelegten Datei: der Speicher ist nur so gross wie der Bereich (der Kanal begrenzt ihn)
    private static byte[]? ReadStashed(LocalEntry entry, long offset, int count)
    {
        if (entry.File is null)
        {
            return [];
        }

        try
        {
            using var handle = File.OpenHandle(entry.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var data = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = RandomAccess.Read(handle, data.AsSpan(read), offset + read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read == count ? data : data[..read];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Ein Eintrag der Auswahl des fernen Rechners: Nummer in dessen Liste (Anfragen gehen nach ihr), Pfad (/), Groesse, Ordner, Zeit.</summary>
internal sealed record DownloadEntry(int Source, string Path, long Size, bool IsDirectory, DateTime? Time);

/// <summary>
/// Ein vorbereiteter Download vom fernen Rechner zum Browser: eine Datei (<see cref="IsArchive"/> falsch) oder ein ZIP aus Ordnern und Dateien. Der Aufruf
/// <see cref="RunAsync"/> schreibt ihn in einen Strom (die HTTP-Antwort) und holt dazu Stueck fuer Stueck vom fernen Rechner, immer erst das naechste, wenn das
/// vorige geschrieben ist: der Server haelt nie mehr als ein Stueck (1 MiB) im Speicher, egal wie gross die Dateien sind.
/// </summary>
public sealed class ClipDownload
{
    private readonly ClipboardBridge _bridge;
    private readonly List<DownloadEntry> _entries;
    private readonly int _cut;
    private readonly CancellationTokenSource _stop = new();

    internal ClipDownload(ClipboardBridge bridge, uint batch, string fileName, bool isArchive, long length, long total, List<DownloadEntry> entries, int cut, uint? lockId)
    {
        _bridge = bridge;
        Batch = batch;
        FileName = fileName;
        IsArchive = isArchive;
        Length = length;
        Total = total;
        _entries = entries;
        _cut = cut;
        LockId = lockId;
    }

    public uint Batch { get; }

    /// <summary>Der Name, unter dem der Browser speichert.</summary>
    public string FileName { get; }

    public bool IsArchive { get; }

    /// <summary>Die Laenge der Antwort, wenn sie vorher feststeht (eine Datei bekannter Groesse), sonst -1.</summary>
    public long Length { get; }

    /// <summary>Die Summe der Dateigroessen (-1, wenn eine unbekannt ist): Grundlage der Fortschrittsanzeige.</summary>
    public long Total { get; }

    internal uint? LockId { get; }

    internal bool Started { get; set; }

    internal Timer? WaitingTimer { get; set; }

    internal void Stop()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { /* war schon fertig */ }
    }

    /// <summary>Schreibt den Download in <paramref name="output"/>. Wirft <see cref="IOException"/> mit dem Grund (auch Text fuer den Browser), wenn er scheitert.</summary>
    public async Task RunAsync(Stream output, CancellationToken ct)
    {
        if (!_bridge.Begin(this))
        {
            throw new InvalidOperationException("Der Download ist nicht (mehr) bereit.");
        }

        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        long done = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        void Progress(bool force)
        {
            if (force || clock.Elapsed - lastReport >= ClipboardBridge.ReportInterval)
            {
                lastReport = clock.Elapsed;
                _bridge.Report(this, ClipboardBridge.StateRunning, done, Total, "");
            }
        }

        try
        {
            Progress(true);
            if (!IsArchive)
            {
                var written = await CopyAsync(_entries[0], _entries[0].Size, data => output.WriteAsync(data, link.Token).AsTask(), link.Token, n => { done += n; Progress(false); }).ConfigureAwait(false);
                if (_entries[0].Size >= 0 && written != _entries[0].Size)
                {
                    throw new IOException("Die Datei war kürzer als angekündigt.");
                }
            }
            else
            {
                var zip = new ClipboardZipWriter(output);
                foreach (var e in _entries)
                {
                    link.Token.ThrowIfCancellationRequested();
                    var name = e.Path[_cut..];
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (e.IsDirectory)
                    {
                        await zip.AddDirectoryAsync(name, e.Time, link.Token).ConfigureAwait(false);
                        continue;
                    }

                    var size = e.Size >= 0 ? e.Size : await AskSizeAsync(e, link.Token).ConfigureAwait(false);
                    await zip.BeginFileAsync(name, size, e.Time, link.Token).ConfigureAwait(false);
                    await CopyAsync(e, size, data => zip.WriteFileAsync(data, link.Token), link.Token, n => { done += n; Progress(false); }).ConfigureAwait(false);
                    await zip.EndFileAsync(link.Token).ConfigureAwait(false);
                }

                await zip.FinishAsync(link.Token).ConfigureAwait(false);
            }

            await output.FlushAsync(link.Token).ConfigureAwait(false);
            _bridge.Report(this, ClipboardBridge.StateDone, done, Total < 0 ? done : Total, "");
        }
        catch (OperationCanceledException)
        {
            _bridge.Report(this, ClipboardBridge.StateFailed, done, Total, "Abgebrochen.");
            throw;
        }
        catch (IOException e)
        {
            _bridge.Report(this, ClipboardBridge.StateFailed, done, Total, e.Message);
            throw;
        }
        finally
        {
            _bridge.End(this);
            link.Cancel();
        }
    }

    // Dateigroesse beim fernen Rechner erfragen
    private async Task<long> AskSizeAsync(DownloadEntry e, CancellationToken ct)
    {
        var data = await _bridge.AskAsync(this, e.Source, 0, 8, true, ct).ConfigureAwait(false)
            ?? throw new IOException("Die VM hat die Datei nicht herausgegeben.");
        return Rdp.Channels.ClipboardChannel.FileSize(data) is { } size and >= 0 ? size : throw new IOException("Die VM hat die Größe nicht genannt.");
    }

    // Eine Datei Stueck fuer Stueck vom fernen Rechner holen und an "write" geben (das erst zurueckkehrt, wenn der Browser es angenommen hat): das ist der Rueckdruck.
    // Gibt die Zahl der Bytes zurueck. Ein kuerzerer Block als verlangt heisst: die Datei endet hier.
    private async Task<long> CopyAsync(DownloadEntry e, long size, Func<ReadOnlyMemory<byte>, Task> write, CancellationToken ct, Action<long> progress)
    {
        if (size < 0)
        {
            size = await AskSizeAsync(e, ct).ConfigureAwait(false);
        }

        long offset = 0;
        while (offset < size)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(ClipboardBridge.Range, size - offset);
            var data = await _bridge.AskAsync(this, e.Source, offset, count, false, ct).ConfigureAwait(false)
                ?? throw new IOException("Die VM hat die Datei nicht herausgegeben.");
            if (data.Length == 0)
            {
                break;
            }

            await write(data).ConfigureAwait(false);
            offset += data.Length;
            progress(data.Length);
            if (data.Length < count)
            {
                break;
            }
        }

        return offset;
    }
}
