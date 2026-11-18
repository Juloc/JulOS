using JulOS.Integrations.Display.Rdp.Channels;

namespace JulOS.Integrations.Display;

/// <summary>Eine Datei, die der ferne Rechner kopiert hat: der Name ist schon bereinigt (nur der Dateiname), die Groesse -1, wenn unbekannt.</summary>
public readonly record struct RemoteFile(string Name, long Size);

/// <summary>Wohin die Bruecke meldet, was der ferne Rechner kopiert hat. Wird auf dem Lesefaden des RDP-Clients gerufen: schnell zurueckkehren.</summary>
public sealed class ClipboardSink
{
    /// <summary>Text (UTF-8 in der Naehe von <see cref="ClipboardBridge.MaxTextBytes"/>).</summary>
    public Action<string>? Text { get; init; }

    /// <summary>Ein Bild: Format (1 PNG, 2 BMP) und die Bytes.</summary>
    public Action<byte, byte[]>? Image { get; init; }

    /// <summary>Dateien (Stapel-Nummer, Liste); eine leere Liste heisst: die Auswahl ist weg.</summary>
    public Action<uint, IReadOnlyList<RemoteFile>>? Files { get; init; }

    /// <summary>Ein Stueck einer angeforderten Datei: Stapel, Nummer in der Liste, Flags (1 letztes Stueck, 2 gescheitert: dann steht der Grund als UTF-8 in den Bytes), Bytes.</summary>
    public Action<uint, int, byte, byte[]>? FileChunk { get; init; }

    /// <summary>Ergebnis eines Sendens vom Browser (Nummer, Code, Text); Nummer 0 betrifft nichts, was der Browser gestartet hat.</summary>
    public Action<uint, byte, string>? Result { get; init; }
}

/// <summary>
/// Zwischenablage zwischen Browser und fernem Rechner, beide Richtungen: Text, Bilder und Dateien. Der Kanal (<see cref="ClipboardChannel"/>)
/// kennt nur Ankuendigungen und Anfragen: wer etwas kopiert, sagt nur, welche Formate er haette, und erst beim Einfuegen wird der Inhalt geholt.
///
/// Ferner Rechner kopiert: Dateien (Liste mit Namen und Groessen, der Inhalt erst, wenn der Browser eine Datei anfordert), sonst Text (sofort geholt,
/// der Browser hat keine Zwischenablage, die er auf Anfrage fuellen koennte), sonst ein Bild (PNG, sonst CF_DIB als BMP).
/// Browser legt etwas ab: es wird angekuendigt und geliefert, wenn der ferne Rechner einfuegt. Die neueste Ablage ersetzt die vorige.
/// Bilder und Dateien kommen in Stuecken (<see cref="BeginTransfer"/> ...) und liegen bis zum Einfuegen im Speicher.
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    /// <summary>Laenger darf ein Text auf dem Weg zwischen Browser und Server nicht sein (UTF-8, in beide Richtungen).</summary>
    public const int MaxTextBytes = 1024 * 1024;

    public const int MaxFileBytes = 64 * 1024 * 1024;
    public const int MaxBatchBytes = 64 * 1024 * 1024;
    public const int MaxFiles = 10;

    /// <summary>Das Bild als BMP (oder PNG vom fernen Rechner); die Fotos vom Handy werden vorher verkleinert.</summary>
    public const int MaxImageBytes = 32 * 1024 * 1024;

    public const int MaxChunkBytes = 256 * 1024;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    public const byte KindImage = 1, KindFile = 2;
    public const byte ResultOk = 0, ResultTooBig = 1, ResultNotAllowed = 2, ResultNoClipboard = 3, ResultOther = 4;
    public const byte ChunkLast = 1, ChunkFailed = 2;
    public const byte ImagePng = 1, ImageBmp = 2;

    // So gross darf die Anforderung an den fernen Rechner sein: ein Stueck pro Anfrage
    private const int RangeBytes = 1024 * 1024;
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(1500);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private enum Expect { Text, Image, ImageIsPng, FileList }

    private enum LocalKind { None, Text, Image, Files }

    private sealed class Upload
    {
        public uint Id;
        public byte Kind;
        public string Name = "";
        public byte[] Data = [];
        public int Received;
        public Timer? Idle;
    }

    private sealed class Download
    {
        public uint Batch;
        public int Index;       // Nummer in der Liste, die der Browser kennt
        public int Source;      // Nummer in der Dateiliste des fernen Rechners
        public long Size = -1;
        public long Offset;
        public int Asked;
        public uint StreamId;
        public uint? LockId;
        public Timer? Answer;
    }

    private readonly ClipboardChannel _channel;
    private readonly ClipboardSink _sink;
    private readonly Lock _gate = new();
    private readonly TimeSpan _idle;

    // was der Browser zuletzt abgelegt hat
    private LocalKind _localKind;
    private byte[]? _local;
    private string? _lastLocal;
    private byte[]? _dib;
    private List<(string Name, byte[] Data)> _localFiles = [];
    private long _echoUntil;

    // eine laufende Uebertragung vom Browser, und der Stapel, der noch nicht vollstaendig ist
    private Upload? _upload;
    private List<(string Name, byte[] Data)> _batch = [];
    private int _batchCount;
    private long _batchBytes;

    // Antworten auf unsere Datenanfragen kommen in der Reihenfolge der Anfragen
    private readonly Queue<Expect> _expected = new();

    // was der ferne Rechner zuletzt an Dateien angeboten hat
    private uint _remoteBatch;
    private uint _lockCounter;
    private uint _streamCounter;
    private List<(int Source, string Name, long Size)> _remoteFiles = [];
    private uint? _remoteLock;
    private Download? _download;
    private bool _disposed;

    /// <param name="remoteText">Der ferne Rechner hat Text kopiert. Wird auf dem Lesefaden des RDP-Clients gerufen: schnell zurueckkehren.</param>
    public ClipboardBridge(ClipboardChannel channel, Action<string> remoteText)
        : this(channel, new ClipboardSink { Text = remoteText })
    {
    }

    /// <param name="idleTimeout">So lange darf eine Uebertragung vom Browser ohne neue Daten stehen (Standard <see cref="IdleTimeout"/>).</param>
    public ClipboardBridge(ClipboardChannel channel, ClipboardSink sink, TimeSpan? idleTimeout = null)
    {
        _idle = idleTimeout ?? IdleTimeout;
        _channel = channel;
        _sink = sink;
        channel.RemoteFormats += OnRemoteFormats;
        channel.DataReceived += OnDataReceived;
        channel.DataRequested += OnDataRequested;
        channel.FileContentsRequested += OnFileContentsRequested;
        channel.FileContentsReceived += OnFileContentsReceived;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _upload?.Idle?.Dispose();
            _upload = null;
            _download?.Answer?.Dispose();
            _download = null;
            _batch = [];
        }
    }

    // ---------------------------------------------------------------- Browser legt ab

    /// <summary>Der Browser hat Text abgelegt: der ferne Rechner bekommt ihn, wenn er einfuegt. Zu langer Text wird ignoriert.</summary>
    public void SetLocalText(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
        {
            return;
        }

        var data = ClipboardText.Encode(text);
        lock (_gate)
        {
            _local = data;
            _lastLocal = text;
            _localKind = LocalKind.Text;
            _dib = null;
            _localFiles = [];
        }

        _channel.AnnounceFormats([new ClipFormat(ClipboardText.FormatId, null)]);
    }

    /// <summary>Der Browser faengt an, ein Bild (Art 1, als BMP) oder eine Datei (Art 2) zu senden. Fehler kommen als Ergebnis zurueck.</summary>
    public void BeginTransfer(uint id, byte kind, int index, int count, long size, string name)
    {
        string? error = null;
        byte code = ResultOther;
        string? clean = null;
        lock (_gate)
        {
            DropUpload();
            if (_disposed)
            {
                return;
            }

            if (kind is not (KindImage or KindFile))
            {
                (code, error) = (ResultOther, "Unbekannte Art.");
            }
            else if (!_channel.IsReady)
            {
                (code, error) = (ResultNoClipboard, "Die VM hat keine Zwischenablage (Kanal fehlt).");
            }
            else if (size <= 0)
            {
                (code, error) = (ResultOther, kind == KindFile ? "Die Datei ist leer." : "Das Bild ist leer.");
            }
            else if (kind == KindImage && size > MaxImageBytes)
            {
                (code, error) = (ResultTooBig, "Bild zu groß (höchstens 14 MB).");
            }
            else if (kind == KindFile)
            {
                if (!_channel.CanTransferFiles)
                {
                    (code, error) = (ResultNoClipboard, "Die VM unterstützt keine Dateien in der Zwischenablage.");
                }
                else if (count is < 1 or > MaxFiles || index < 0 || index >= count)
                {
                    (code, error) = (ResultTooBig, $"Zu viele Dateien (höchstens {MaxFiles}).");
                }
                else if (size > MaxFileBytes || (index == 0 ? 0 : _batchBytes) + size > MaxBatchBytes)
                {
                    (code, error) = (ResultTooBig, "Datei zu groß (höchstens 64 MB).");
                }
                else if (ClipboardNames.Sanitize(name) is not { } safe)
                {
                    (code, error) = (ResultNotAllowed, "Ungültiger Dateiname.");
                }
                else if (ClipboardNames.BlockedExtension(safe) is { } blocked)
                {
                    (code, error) = (ResultNotAllowed, $"Dateityp nicht erlaubt ({blocked}).");
                }
                else
                {
                    clean = safe;
                }
            }

            if (error is null)
            {
                if (kind == KindFile)
                {
                    if (index == 0)
                    {
                        _batch = [];
                        _batchBytes = 0;
                        _batchCount = count;
                    }
                    else if (count != _batchCount || index != _batch.Count)
                    {
                        (code, error) = (ResultOther, "Die Reihenfolge der Dateien stimmt nicht.");
                    }
                }
            }

            if (error is null)
            {
                var upload = new Upload { Id = id, Kind = kind, Name = clean ?? "", Data = new byte[size] };
                upload.Idle = new Timer(_ => Expire(upload), null, _idle, Timeout.InfiniteTimeSpan);
                _upload = upload;
            }
        }

        if (error is not null)
        {
            _sink.Result?.Invoke(id, code, error);
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

            if (data.Length > MaxChunkBytes || upload.Received + data.Length > upload.Data.Length)
            {
                DropUpload();
                (code, error) = (ResultOther, "Mehr Daten als angekündigt.");
            }
            else
            {
                data.CopyTo(upload.Data.AsSpan(upload.Received));
                upload.Received += data.Length;
                upload.Idle?.Change(_idle, Timeout.InfiniteTimeSpan);
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
        lock (_gate)
        {
            if (_upload is not { } upload || upload.Id != id)
            {
                return;
            }

            DropUpload();
            if (upload.Received != upload.Data.Length)
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
                    _dib = dib;
                    _local = null;
                    _lastLocal = null;
                    _localFiles = [];
                    _localKind = LocalKind.Image;
                    _echoUntil = Environment.TickCount64 + (long)EchoWindow.TotalMilliseconds;
                    announce = [new ClipFormat(DeviceIndependentBitmap.FormatId, null)];
                }
            }
            else
            {
                _batch.Add((upload.Name, upload.Data));
                _batchBytes += upload.Data.Length;
                if (_batch.Count == _batchCount)
                {
                    _localFiles = _batch;
                    _batch = [];
                    _batchBytes = 0;
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

        _sink.Result?.Invoke(id, code, message);
    }

    private void DropUpload()
    {
        _upload?.Idle?.Dispose();
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
            return;
        }

        var text = ClipboardText.Decode(data);
        if (text.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
        {
            return;
        }

        // Manche Server melden den Text, den wir ihnen eben gegeben haben, als neue Kopie zurueck: das ist keine Neuigkeit fuer den Browser
        lock (_gate)
        {
            if (text == _lastLocal)
            {
                return;
            }
        }

        _sink.Text?.Invoke(text);
    }

    private void ReceivedFileList(byte[] data)
    {
        var files = FileGroupDescriptor.Decode(data);
        if (files is null)
        {
            return;
        }

        // Ordner und alles darin werden nicht uebertragen; von Dateien in Unterordnern zaehlt der Dateiname
        var list = new List<(int Source, string Name, long Size)>();
        for (var i = 0; i < files.Count && list.Count < MaxFiles; i++)
        {
            if (files[i].IsDirectory || FileGroupDescriptor.PathParts(files[i].Name) is null || ClipboardNames.Sanitize(files[i].Name) is not { } name)
            {
                continue;
            }

            list.Add((i, name, files[i].Size));
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
            _remoteFiles = list;
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

        _sink.Files?.Invoke(batch, list.Select(f => new RemoteFile(f.Name, f.Size)).ToArray());
    }

    // ---------------------------------------------------------------- Browser holt eine Datei

    /// <summary>Der Browser will die Datei Nummer <paramref name="index"/> aus der zuletzt gemeldeten Liste. Die Stuecke kommen als <see cref="ClipboardSink.FileChunk"/>.</summary>
    public void RequestFile(uint batch, int index)
    {
        string? error = null;
        Download? start = null;
        lock (_gate)
        {
            if (_download is not null)
            {
                error = "Es läuft schon ein Download.";
            }
            else if (batch != _remoteBatch || index < 0 || index >= _remoteFiles.Count)
            {
                error = "Die Auswahl in der VM hat sich geändert.";
            }
            else if (_remoteFiles[index].Size > MaxFileBytes)
            {
                error = "Datei zu groß (höchstens 64 MB).";
            }
            else
            {
                var file = _remoteFiles[index];
                start = new Download { Batch = batch, Index = index, Source = file.Source, Size = file.Size, LockId = _remoteLock };
                _download = start;
            }
        }

        if (error is not null)
        {
            _sink.FileChunk?.Invoke(batch, Math.Max(index, 0), ChunkFailed, System.Text.Encoding.UTF8.GetBytes(error));
            return;
        }

        AskNext(start!);
    }

    public void CancelFile(uint batch)
    {
        lock (_gate)
        {
            if (_download is { } d && d.Batch == batch)
            {
                d.Answer?.Dispose();
                _download = null;
            }
        }
    }

    // Die naechste Anfrage an den fernen Rechner: erst die Groesse (wenn sie fehlt), dann ein Stueck nach dem anderen
    private void AskNext(Download d)
    {
        bool wantSize;
        long offset;
        int count;
        uint stream;
        lock (_gate)
        {
            if (_download != d)
            {
                return;
            }

            wantSize = d.Size < 0;
            offset = d.Offset;
            count = (int)Math.Min(RangeBytes, d.Size - d.Offset);
            d.Asked = count;
            stream = d.StreamId = ++_streamCounter;
            d.Answer?.Dispose();
            d.Answer = new Timer(_ => Fail(d, "Die VM antwortet nicht."), null, AnswerTimeout, Timeout.InfiniteTimeSpan);
        }

        try
        {
            if (wantSize)
            {
                _channel.RequestFileSize(stream, d.Source, d.LockId);
            }
            else
            {
                _channel.RequestFileRange(stream, d.Source, offset, count, d.LockId);
            }
        }
        catch (ArgumentException)
        {
            Fail(d, "Die Datei ist nicht mehr da.");
        }
    }

    private void OnFileContentsReceived(uint streamId, byte[]? data)
    {
        Download? d;
        bool wasSize;
        lock (_gate)
        {
            d = _download;
            if (d is null || d.StreamId != streamId)
            {
                return;
            }

            wasSize = d.Size < 0;
        }

        if (data is null)
        {
            Fail(d, "Die VM hat die Datei nicht herausgegeben.");
            return;
        }

        if (wasSize)
        {
            var size = ClipboardChannel.FileSize(data);
            if (size is null || size < 0)
            {
                Fail(d, "Die VM hat die Größe nicht genannt.");
                return;
            }

            if (size > MaxFileBytes)
            {
                Fail(d, "Datei zu groß (höchstens 64 MB).");
                return;
            }

            lock (_gate)
            {
                d.Size = size.Value;
            }

            if (size == 0)
            {
                Finish(d, []);
                return;
            }

            AskNext(d);
            return;
        }

        bool last;
        lock (_gate)
        {
            d.Offset += data.Length;
            // Ein kuerzerer Block als verlangt heisst: die Datei endet hier
            last = d.Offset >= d.Size || data.Length < d.Asked;
            if (d.Offset > MaxFileBytes)
            {
                last = true;
            }
        }

        if (last)
        {
            Finish(d, data);
        }
        else
        {
            _sink.FileChunk?.Invoke(d.Batch, d.Index, 0, data);
            AskNext(d);
        }
    }

    private void Finish(Download d, byte[] data)
    {
        lock (_gate)
        {
            if (_download != d)
            {
                return;
            }

            d.Answer?.Dispose();
            _download = null;
        }

        _sink.FileChunk?.Invoke(d.Batch, d.Index, ChunkLast, data);
    }

    private void Fail(Download d, string reason)
    {
        lock (_gate)
        {
            if (_download != d)
            {
                return;
            }

            d.Answer?.Dispose();
            _download = null;
        }

        _sink.FileChunk?.Invoke(d.Batch, d.Index, ChunkFailed, System.Text.Encoding.UTF8.GetBytes(reason));
    }

    // ---------------------------------------------------------------- Der ferne Rechner fuegt ein

    private void OnDataRequested(uint formatId)
    {
        byte[]? data = null;
        lock (_gate)
        {
            switch (_localKind)
            {
                case LocalKind.Text when formatId == ClipboardText.FormatId:
                    data = _local;
                    break;
                case LocalKind.Image when formatId == DeviceIndependentBitmap.FormatId:
                    data = _dib;
                    break;
                case LocalKind.Files when formatId == FileGroupDescriptor.DefaultFormatId:
                    data = FileGroupDescriptor.Encode(_localFiles.Select(f => new ClipFile(f.Name, f.Data.Length)).ToArray());
                    break;
            }
        }

        _channel.SendData(data);
    }

    private void OnFileContentsRequested(FileContentsRequest request)
    {
        byte[]? data = null;
        long? size = null;
        lock (_gate)
        {
            if (_localKind == LocalKind.Files && request.Index >= 0 && request.Index < _localFiles.Count)
            {
                var file = _localFiles[request.Index].Data;
                if (request.IsSizeRequest)
                {
                    size = file.Length;
                }
                else if (request.Offset >= file.Length)
                {
                    data = [];
                }
                else
                {
                    var count = (int)Math.Min(request.Count, file.Length - request.Offset);
                    data = file.AsSpan((int)request.Offset, count).ToArray();
                }
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
}
