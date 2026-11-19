using System.Threading.Channels;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Channels;
using JulOS.Integrations.Display.Refine;
using JulOS.Integrations.Display.Video;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display;

/// <summary>Was der Browser von einer Bildschirm-Sitzung hoert.</summary>
public abstract record ScreenEvent;

/// <summary>Der Desktop hat diese Groesse; ab jetzt kommen Bilder in dieser Groesse (das naechste ist ein Schluesselbild).</summary>
public sealed record ScreenSize(int Width, int Height) : ScreenEvent;

/// <summary>Ein Bild als H.264 oder H.265 (Annex B, SPS und PPS, bei H.265 auch VPS, vor jedem Schluesselbild). Der Browser bestaetigt jedes Bild.</summary>
public sealed record ScreenPicture(int Sequence, bool IsKey, byte[] Data, VideoCodec Codec = VideoCodec.H264) : ScreenEvent;

/// <summary>Welcher Kodierer gerade arbeitet (nach jedem Neuaufbau); der Browser zeigt es in seinen Zahlen.</summary>
public sealed record ScreenEncoderInfo(VideoCodec Codec, bool Hardware, string Name) : ScreenEvent;

/// <summary>Der ferne Rechner hat Text kopiert.</summary>
public sealed record ScreenClipboardText(string Text) : ScreenEvent;

/// <summary>Es liegt Ton bereit: <see cref="ScreenStream.Audio"/> hat ihn (nur ein Zeichen, der Ton selbst wartet dort in einer begrenzten Warteschlange).</summary>
public sealed record ScreenAudioReady : ScreenEvent;

/// <summary>Der Ton der Sitzung hat sich geaendert (Ausgabe verfuegbar, Mikrofon angeboten oder verlangt).</summary>
public sealed record ScreenAudioStatus(AudioStatus Status) : ScreenEvent;

/// <summary>Der ferne Rechner hat ein Bild kopiert (Format 1 PNG, 2 BMP).</summary>
public sealed record ScreenClipboardImage(byte Format, byte[] Data) : ScreenEvent;

/// <summary>Der ferne Rechner hat Dateien kopiert (leere Liste: die Auswahl ist weg).</summary>
public sealed record ScreenClipboardFiles(uint Batch, IReadOnlyList<RemoteFile> Files) : ScreenEvent;

/// <summary>Ein Stueck einer Datei, die der Browser angefordert hat (Flags siehe <see cref="ClipboardBridge.ChunkLast"/>).</summary>
public sealed record ScreenFileChunk(uint Batch, int Index, byte Flags, byte[] Data) : ScreenEvent;

/// <summary>Wie ein Senden vom Browser ausgegangen ist.</summary>
public sealed record ScreenTransferResult(uint Id, byte Code, string Message) : ScreenEvent;

/// <summary>Ein Rechteck des Bildschirms verlustfrei (PNG mit Palette); der Browser legt es ueber das Video (siehe <see cref="TileRefiner"/>).</summary>
public sealed record ScreenRefine(int X, int Y, int Width, int Height, byte[] Png) : ScreenEvent;

/// <summary>Diese Rechtecke (x, y, breite, hoehe, nacheinander) sind nicht mehr nachgebessert: der Browser nimmt die Ueberlagerung dort weg.</summary>
public sealed record ScreenRefineClear(int[] Rects) : ScreenEvent;

public enum CursorKind { Shape, Hidden, Default }

/// <summary>Die Form des Mauszeigers des fernen Rechners (RGBA, R zuerst, gerade Alpha, von oben nach unten).</summary>
public sealed record ScreenCursor(CursorKind Kind, int HotX, int HotY, int Width, int Height, byte[]? Rgba) : ScreenEvent;

/// <summary>
/// Ein ferner Desktop fuer einen Browser: RDP hinein, H.264-Bilder heraus. Aenderungen am Bildschirm werden gesammelt, nur das geaenderte
/// Rechteck wird umgerechnet und kodiert. Der Browser bestaetigt jedes Bild; solange drei unbestaetigt sind, wird nichts Neues
/// gesendet (so bleibt die Verzoegerung klein, auch auf langsamer Leitung). Steht der Bildschirm still, wird dasselbe Bild so lange
/// neu kodiert, bis es scharf ist.
/// </summary>
public sealed class ScreenStream : IRdpHandler, IAsyncDisposable
{
    /// <summary>So viele Bilder duerfen unterwegs sein, ohne dass der Browser sie bestaetigt hat.</summary>
    public const int MaxPicturesInFlight = 3;

    private const int FramesPerSecond = 30;
    private const int SharpenGapMs = 30;
    private const int IdleWaitMs = 250;

    // Verlustfreie Nachbesserung ruhender Teile (JULOS_STREAM_REFINE=off schaltet sie ab, zum Vergleichen): hoechstens dieser Teil der Bitrate,
    // und nur, solange der Browser mit den Bildern nachkommt.
    private const double RefineShare = 0.25;
    private const int RefineTilesPerPass = 48;
    private static readonly bool PacerAllowed = !string.Equals(Environment.GetEnvironmentVariable("JULOS_STREAM_PACER"), "off", StringComparison.OrdinalIgnoreCase);
    private static readonly bool RefineAllowed = !string.Equals(Environment.GetEnvironmentVariable("JULOS_STREAM_REFINE"), "off", StringComparison.OrdinalIgnoreCase);

    // Kommt bei vollem Fenster unbestaetigter Bilder so lange keine Bestaetigung, gilt das Fenster als verloren (der Browser dekodiert
    // normalerweise in wenigen Millisekunden): zuruecksetzen und ein Schluesselbild senden, statt fuer immer zu warten.
    private const int AckStallMs = 2000;

    private readonly RdpOptions _options;
    private readonly int _maxKilobitsPerSecond;
    private readonly ILogger? _log;
    private readonly Channel<ScreenEvent> _events = Channel.CreateUnbounded<ScreenEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();

    private readonly ClipboardChannel _clipboardChannel = new();
    private readonly ClipboardBridge _clipboard;
    private readonly SoundChannel _soundChannel = new();
    private readonly AudioInputChannel _microphoneChannel = new();
    private RdpClient? _rdp;
    private DisplayControlChannel? _display;
    private readonly EncoderSelector _selector;
    private IVideoEncoder? _encoder;
    private IEncoderBackend? _encoderBackend;
    private I420Frame? _frame;
    private volatile TileRefiner? _refiner;
    private double _refineCredit;
    private long _refineAt;

    // Was der Browser dekodieren kann (unter _gate); eine Aenderung baut den Kodierer neu auf (zum Beispiel zurueck auf H.264).
    private VideoCodecs _clientCodecs;
    private bool _codecChanged;
    private bool _noHardware;

    // Alles, was sich seit dem letzten Bild geaendert hat: ein Rechteck und eine grobe Zaehlung der Pixel (unter _gate).
    private bool _dirty;
    private int _dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom;
    private long _dirtyPixels;
    private (int Width, int Height)? _pendingSize;

    private readonly PictureWindow _window = new(MaxPicturesInFlight);
    private readonly LinkPacer _pacer;
    private readonly Lock _pacerGate = new();
    private int _kilobits;
    private long _lastAckAt, _lastSentAt, _lastUpdateAt;
    private int _sequence;
    private int _disposed;

    // Gewuenschte Groesse des Desktops (die des Browserfensters) und was daraus geworden ist; unter _gate gelesen und geschrieben.
    private int _wantWidth, _wantHeight, _askedWidth, _askedHeight;
    private bool _settled = true;
    private long _lastAskedAt, _resizedAt;

    private readonly CertificateTrust? _trust;

    public ScreenStream(RdpOptions options, int maxKilobitsPerSecond = 16_000, ILogger? log = null, EncoderSelector? encoders = null, VideoCodecs clientCodecs = VideoCodecs.H264, CertificateTrust? trust = null)
    {
        _trust = trust;
        _options = options;
        _maxKilobitsPerSecond = maxKilobitsPerSecond;
        _kilobits = maxKilobitsPerSecond;
        _pacer = new LinkPacer(maxKilobitsPerSecond);
        _log = log;
        _selector = encoders ?? DefaultSelector;
        _clientCodecs = clientCodecs == VideoCodecs.None ? VideoCodecs.H264 : clientCodecs;
        _clipboard = new ClipboardBridge(_clipboardChannel, new ClipboardSink
        {
            Text = text => _events.Writer.TryWrite(new ScreenClipboardText(text)),
            Image = (format, data) => _events.Writer.TryWrite(new ScreenClipboardImage(format, data)),
            Files = (batch, files) => _events.Writer.TryWrite(new ScreenClipboardFiles(batch, files)),
            FileChunk = (batch, index, flags, data) => _events.Writer.TryWrite(new ScreenFileChunk(batch, index, flags, data)),
            Result = (id, code, message) => _events.Writer.TryWrite(new ScreenTransferResult(id, code, message)),
        });
        Audio = new AudioBridge(_soundChannel, _microphoneChannel);
        Audio.Ready += () => _events.Writer.TryWrite(new ScreenAudioReady());
        Audio.StatusChanged += status => _events.Writer.TryWrite(new ScreenAudioStatus(status));
    }

    /// <summary>Der Ton der Sitzung: pro Verbindung aus, bis der Browser ihn einschaltet.</summary>
    public AudioBridge Audio { get; }

    /// <summary>Die Auswahl, wenn der Aufrufer keine mitgibt: nur der Software-Kodierer (so verhalten sich Tests ohne GPU und ohne Umgebung).</summary>
    public static EncoderSelector DefaultSelector { get; } = new([new SoftwareBackend()], new EncoderSettings(EncoderMode.Software));

    /// <summary>Ob H.264 auf dieser Maschine kodiert werden kann (libopenh264 vorhanden); er ist der Rueckfall fuer jede GPU.</summary>
    public static bool IsAvailable => H264Encoder.IsAvailable;

    public static string? UnavailableReason => H264Encoder.UnavailableReason;

    /// <summary>Was der Browser hoeren soll, in der Reihenfolge, in der es geschah. Endet, wenn die Sitzung endet.</summary>
    public ChannelReader<ScreenEvent> Events => _events.Reader;

    /// <summary>Warum der ferne Rechner die Sitzung beendet hat, falls er es gesagt hat.</summary>
    public string? EndReason => _rdp?.EndReason;

    /// <summary>Verbindet, meldet sich an und laeuft, bis die Sitzung endet oder abgebrochen wird. Wirft RdpException, wenn das Verbinden misslingt.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var rdp = new RdpClient(_options, this, message => _log?.LogDebug("RDP: {Message}", message));
        _rdp = rdp;
        var channels = new DynamicChannels();
        var display = new DisplayControlChannel();
        channels.Register(display);
        channels.Register(_microphoneChannel);
        rdp.AddChannel(channels);
        rdp.AddChannel(_clipboardChannel);
        rdp.AddChannel(_soundChannel);
        // Windows spricht auf "rdpsnd" nur mit einem Client, der auch "rdpdr" meldet (ohne Geraete); fuer den Ton also noetig
        rdp.AddChannel(new RdpdrChannel());
        _display = display;
        try
        {
            await rdp.ConnectAsync(linked.Token).ConfigureAwait(false);

            // Angemeldet: ein neues oder bestaetigtes Zertifikat wird jetzt gemerkt (nicht schon bei einem Versuch mit falschem Passwort)
            if (_trust is not null && await _trust.CommitAsync(linked.Token).ConfigureAwait(false))
            {
                _log?.LogInformation("RDP-Zertifikat {Decision}", _trust.Decision == CertificateDecision.FirstUse ? "gemerkt" : "ersetzt (vom Benutzer bestätigt)");
            }

            SetPendingSize(rdp.Width, rdp.Height);

            var read = rdp.RunAsync(linked.Token);
            var encode = EncodeLoopAsync(linked.Token);
            var follow = FollowSizeAsync(rdp, linked.Token);
            var first = await Task.WhenAny(read, encode).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { read, encode, follow })
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* wir haben es selbst abgebrochen */ }
            }

            await first.ConfigureAwait(false);
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    // ---- Eingabe vom Browser ----

    public void MouseMove(int x, int y) => _rdp?.MouseMove(x, y);

    public void MouseButton(int button, bool down) => _rdp?.MouseButton(button, down);

    public void MouseWheel(int delta, bool horizontal) => _rdp?.MouseWheel(delta, horizontal);

    public void Key(int scancode, bool extended, bool down) => _rdp?.Key(scancode, extended, down);

    public void Unicode(char unit) => _rdp?.Unicode(unit);

    /// <summary>Der Browser hat Text abgelegt; der ferne Rechner holt ihn, wenn dort eingefuegt wird.</summary>
    public void SetClipboard(string text) => _clipboard.SetLocalText(text);

    /// <summary>Der Browser sendet ein Bild (als BMP) oder eine Datei an die Zwischenablage des fernen Rechners; siehe <see cref="ClipboardBridge"/>.</summary>
    public void BeginTransfer(uint id, byte kind, int index, int count, long size, string name) => _clipboard.BeginTransfer(id, kind, index, count, size, name);

    public void TransferChunk(uint id, ReadOnlySpan<byte> data) => _clipboard.TransferChunk(id, data);

    public void EndTransfer(uint id) => _clipboard.EndTransfer(id);

    public void CancelTransfer(uint id) => _clipboard.CancelTransfer(id);

    /// <summary>Der Browser holt eine Datei, die der ferne Rechner kopiert hat.</summary>
    public void RequestFile(uint batch, int index) => _clipboard.RequestFile(batch, index);

    public void CancelFile(uint batch) => _clipboard.CancelFile(batch);


    /// <summary>Der Browser hat die Bilder bis zu dieser Folgenummer dekodiert (oder verworfen); ohne Nummer: ein Bild mehr.</summary>
    public void Acknowledge(int? sequence = null)
    {
        Volatile.Write(ref _lastAckAt, Environment.TickCount64);
        if (sequence is { } value)
        {
            _window.Acknowledge(value);
            lock (_pacerGate)
            {
                _pacer.Acknowledged(value, Environment.TickCount64);
            }
        }
        else
        {
            _window.AcknowledgeOne();
        }

        Wake();
    }

    /// <summary>
    /// Der Browser will den Desktop in dieser Groesse (Anpassung an das Fenster). Das Nachfuehren geschieht im Hintergrund: ein Wechsel ist ein
    /// Hin und Her mit dem fernen Rechner (er nimmt die Anordnung, trennt die Sitzung kurz und baut sie in der neuen Groesse neu auf).
    /// </summary>
    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            width &= ~1;
            height &= ~1;
            if (width != _wantWidth || height != _wantHeight)
            {
                _wantWidth = width;
                _wantHeight = height;
                _settled = false;
            }
        }
    }

    /// <summary>
    /// Die Ansicht im Browser ist im Hintergrund (zum Beispiel eine andere Sitzung vorn): solange kodiert der Server nichts. Der Desktop laeuft
    /// weiter, die Aenderungen sammeln sich im Bild; beim Weitermachen geht daraus das naechste Bild hinaus (ohne Luecke fuer den Dekodierer).
    /// </summary>
    public void SetPaused(bool paused)
    {
        var was = Volatile.Read(ref _paused);
        Volatile.Write(ref _paused, paused);
        if (was && !paused)
        {
            Wake();
        }
    }

    /// <summary>Das Senden eines Bildes an den WebSocket hat so lange gedauert: ein voller Sendepuffer bremst (siehe <see cref="LinkPacer"/>).</summary>
    public void SocketSent(long elapsedMilliseconds)
    {
        lock (_pacerGate)
        {
            _pacer.SocketSent(elapsedMilliseconds, Environment.TickCount64);
        }
    }

    /// <summary>
    /// Was der Browser dekodieren kann (er meldet es beim Verbinden und noch einmal, wenn ein Dekodierer nicht ging, dann ohne H.265).
    /// Passt der laufende Kodierer nicht mehr, kommt der naechste mit einem Schluesselbild.
    /// </summary>
    public void SetClientCodecs(VideoCodecs codecs)
    {
        codecs = (codecs & (VideoCodecs.H264 | VideoCodecs.Hevc)) == 0 ? VideoCodecs.H264 : codecs;
        lock (_gate)
        {
            if (codecs == _clientCodecs)
            {
                return;
            }

            _clientCodecs = codecs;
            _codecChanged = true;
        }

        Wake();
    }

    /// <summary>Ein Schluesselbild als naechstes (der Browser hat den Faden verloren).</summary>
    public void RequestKeyFrame()
    {
        lock (_gate)
        {
            _forceKey = true;
        }

        Wake();
    }

    // ---- was der RDP-Client meldet (auf seiner Leseschleife: schnell zurueckkehren) ----

    void IRdpHandler.Updated(int x, int y, int width, int height)
    {
        var tick = Environment.TickCount64;
        Volatile.Write(ref _lastUpdateAt, tick);
        _refiner?.Mark(x, y, width, height, tick);
        lock (_gate)
        {
            if (_dirty)
            {
                _dirtyLeft = Math.Min(_dirtyLeft, x);
                _dirtyTop = Math.Min(_dirtyTop, y);
                _dirtyRight = Math.Max(_dirtyRight, x + width);
                _dirtyBottom = Math.Max(_dirtyBottom, y + height);
            }
            else
            {
                _dirty = true;
                _dirtyLeft = x;
                _dirtyTop = y;
                _dirtyRight = x + width;
                _dirtyBottom = y + height;
            }

            _dirtyPixels += (long)width * height;
        }

        Wake();
    }

    void IRdpHandler.Resized(int width, int height)
    {
        lock (_gate)
        {
            // Was der ferne Rechner aus der Anfrage gemacht hat, gilt: dieselbe Groesse wird nicht noch einmal verlangt
            _resizedAt = Environment.TickCount64;
            _settled = true;
        }

        SetPendingSize(width, height);
    }

    void IRdpHandler.Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba) =>
        PublishCursor(new ScreenCursor(CursorKind.Shape, hotX, hotY, width, height, rgba.ToArray()));

    void IRdpHandler.CursorHidden() => PublishCursor(new ScreenCursor(CursorKind.Hidden, 0, 0, 0, 0, null));

    void IRdpHandler.CursorDefault() => PublishCursor(new ScreenCursor(CursorKind.Default, 0, 0, 0, 0, null));

    // Die letzte Form wird gemerkt: ein Browser, der sich wieder anhaengt (Resume), hat sie nicht gesehen.
    private volatile ScreenCursor? _lastCursor;
    private volatile ScreenEncoderInfo? _lastEncoderInfo;

    private void PublishCursor(ScreenCursor cursor)
    {
        _lastCursor = cursor;
        _events.Writer.TryWrite(cursor);
    }

    /// <summary>
    /// Kein Browser mehr dran (die Verbindung riss ab, die Sitzung wird erhalten): nichts kodieren, den Ton aus. Der Desktop laeuft weiter,
    /// die Aenderungen sammeln sich im Bild. <see cref="Resume"/> holt den neuen Browser auf den Stand.
    /// </summary>
    public void Park()
    {
        Volatile.Write(ref _paused, true);
        Audio.OutputOn = false;
    }

    /// <summary>
    /// Ein neuer Browser hat sich an die erhaltene Sitzung gehaengt (der Aufrufer liest die Ereignisse noch nicht): was noch unterwegs war, gilt
    /// als verloren, der Browser bekommt Groesse, Zeigerform und Kodierer neu gemeldet und als naechstes ein Schluesselbild des ganzen Bildes.
    /// </summary>
    public void Resume()
    {
        // Was der alte Browser noch haette hoeren sollen, ist veraltet
        while (_events.Reader.TryRead(out _))
        {
        }

        lock (_pacerGate)
        {
            _window.Reset();
            _pacer.Reset(Volatile.Read(ref _sequence));
        }

        var frame = _frame;
        if (frame is not null)
        {
            _events.Writer.TryWrite(new ScreenSize(frame.Width, frame.Height));
            if (_lastEncoderInfo is { } info)
            {
                _events.Writer.TryWrite(info);
            }

            if (_lastCursor is { } cursor)
            {
                _events.Writer.TryWrite(cursor);
            }

            // Die Ueberlagerung (verlustfreie Teile) des neuen Browsers ist leer: alles wieder nachbessern
            _refiner?.Mark(0, 0, frame.Width, frame.Height, Environment.TickCount64);
        }

        Volatile.Write(ref _lastAckAt, Environment.TickCount64);
        lock (_gate)
        {
            _forceKey = true;
        }

        Volatile.Write(ref _paused, false);
        Wake();
    }

    // ---- Kodierschleife ----

    private bool _forceKey;
    private bool _paused;

    private async Task EncodeLoopAsync(CancellationToken cancellationToken)
    {
        long lastEncodedAt = 0;
        var needKey = true;
        var encodedAny = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TakePendingSize(out var size))
            {
                Rebuild(size.Width, size.Height);
                _events.Writer.TryWrite(new ScreenSize(_frame!.Width, _frame.Height));
                needKey = true;
                encodedAny = false;
            }
            else if (TakeCodecChange() && _frame is not null && _encoder is { } current && !ClientCodecs.Has(current.Codec))
            {
                ReplaceEncoder();
                needKey = true;
                encodedAny = false;
            }

            var wait = IdleWaitMs;
            var encoder = _encoder;
            var frame = _frame;

            var inFlight = _window.InFlight;
            if (_window.Full)
            {
                var tick = Environment.TickCount64;
                var quietFor = tick - Math.Max(Volatile.Read(ref _lastAckAt), Volatile.Read(ref _lastSentAt));
                if (quietFor > AckStallMs)
                {
                    _log?.LogWarning("Keine Bestaetigung seit {Quiet} ms bei {InFlight} offenen Bildern (letztes Bild vor {Sent} ms, letzte Aenderung vor {Update} ms): Fenster zurueckgesetzt, Schluesselbild folgt",
                        quietFor, inFlight, tick - Volatile.Read(ref _lastSentAt), tick - Volatile.Read(ref _lastUpdateAt));
                    _window.Reset();
                    lock (_pacerGate)
                    {
                        _pacer.Reset(Volatile.Read(ref _sequence));
                    }

                    needKey = true;
                }
            }

            if (_window.Full && !Volatile.Read(ref _paused) && (needKey || PeekDirty(out _, out _, out _, out _, out _) || (encodedAny && encoder is { IsSharp: false })))
            {
                // Es gaebe etwas zu senden, aber das Fenster ist voll: der Engpass ist die Leitung oder der Browser
                lock (_pacerGate)
                {
                    _pacer.Blocked(Environment.TickCount64);
                }
            }

            AdaptToLink();
            if (encoder is not null && frame is not null && !Volatile.Read(ref _paused) && !_window.Full)
            {
                var now = Environment.TickCount64;
                var hasChange = PeekDirty(out var left, out var top, out var width, out var height, out var pixels);
                if (TakeForceKey())
                {
                    needKey = true;
                }

                if (hasChange)
                {
                    var due = needKey ? now : lastEncodedAt + encoder.SuggestedInterval(pixels);
                    if (now >= due)
                    {
                        if (Convert(frame, left, top, width, height))
                        {
                            ClearDirty();
                            ClearRefinements();
                            encodedAny |= Send(encoder, frame, needKey);
                            needKey = false;
                            lastEncodedAt = Environment.TickCount64;
                        }

                        wait = 0;
                    }
                    else
                    {
                        wait = (int)Math.Max(1, due - now);
                    }
                }
                else if (needKey)
                {
                    // Kein neuer Inhalt, aber ein Schluesselbild ist verlangt (erstes Bild, Groessenwechsel, der Browser hat den Faden verloren,
                    // Fenster zurueckgesetzt): der ganze Bildschirm. Auch ein scharfer, ruhender Bildschirm wird so neu gesendet; sonst bliebe
                    // die Anfrage des Browsers unbeantwortet und sein Bild leer.
                    frame.MarkChanged();
                    encodedAny |= Send(encoder, frame, true);
                    needKey = false;
                    lastEncodedAt = Environment.TickCount64;
                    wait = 0;
                }
                else if (encodedAny && !encoder.IsSharp)
                {
                    var due = lastEncodedAt + SharpenGapMs;
                    if (now >= due)
                    {
                        Send(encoder, frame, needKey);
                        needKey = false;
                        lastEncodedAt = Environment.TickCount64;
                        wait = 0;
                    }
                    else
                    {
                        wait = (int)Math.Max(1, due - now);
                    }
                }
            }

            if (wait > 0 && RefineAllowed)
            {
                wait = Math.Min(wait, RefineStep());
            }

            if (wait > 0)
            {
                try { await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // Rechnet das geaenderte Rechteck des Desktops ins Bild des Kodierers um. False, wenn der Desktop gerade eine andere Groesse hat
    // als der Kodierer (dann kommt gleich die neue Groesse).
    private bool Convert(I420Frame frame, int left, int top, int width, int height)
    {
        var rdp = _rdp;
        if (rdp is null)
        {
            return false;
        }

        lock (rdp.FrameLock)
        {
            if ((rdp.Width & ~1) != frame.Width || (rdp.Height & ~1) != frame.Height)
            {
                return false;
            }

            ColorConverter.BgrxToI420(rdp.Framebuffer, rdp.Width * 4, frame, left, top, width, height);
        }

        return true;
    }

    // Langsame Leitung: Bitrate und Fenster folgen dem, was die Messung (LinkPacer) sagt.
    private void AdaptToLink()
    {
        if (!PacerAllowed)
        {
            return;
        }

        int kilobits, window;
        lock (_pacerGate)
        {
            if (!_pacer.Update(Environment.TickCount64))
            {
                return;
            }

            (kilobits, window) = (_pacer.Kilobits, _pacer.Window);
        }

        if (kilobits != _kilobits)
        {
            _log?.LogDebug("Leitung: Bitrate {From} -> {To} kbit/s, Fenster {Window}", _kilobits, kilobits, window);
            _kilobits = kilobits;
            _encoder?.TrySetBitrate(kilobits);
        }

        _window.Max = window;
    }

    // Kacheln, die der Browser nachgebessert hat und die sich wieder geaendert haben, werden dort gestrichen, bevor das Bild mit der Aenderung
    // hinausgeht (sonst bliebe das alte Bild darueber stehen).
    private void ClearRefinements()
    {
        var invalid = _refiner?.TakeInvalidations();
        if (invalid is not { Count: > 0 })
        {
            return;
        }

        var rects = new int[invalid.Count * 4];
        for (var i = 0; i < invalid.Count; i++)
        {
            rects[i * 4] = invalid[i].X;
            rects[i * 4 + 1] = invalid[i].Y;
            rects[i * 4 + 2] = invalid[i].Width;
            rects[i * 4 + 3] = invalid[i].Height;
        }

        _events.Writer.TryWrite(new ScreenRefineClear(rects));
    }

    // Schickt, was lange genug still war, verlustfrei hinterher. Gibt zurueck, in wie vielen Millisekunden es weitergeht (int.MaxValue: nichts wartet).
    private int RefineStep()
    {
        var refiner = _refiner;
        var rdp = _rdp;
        if (refiner is null || rdp is null || refiner.DirtyTiles == 0)
        {
            return int.MaxValue;
        }

        var now = Environment.TickCount64;
        var perSecond = _kilobits * 125.0 * RefineShare;
        _refineCredit = Math.Min(perSecond, _refineCredit + perSecond * Math.Clamp(now - _refineAt, 0, 1000) / 1000.0);
        _refineAt = now;
        if (Volatile.Read(ref _paused) || _window.InFlight > 1)
        {
            return 100;
        }

        if (_refineCredit <= 0)
        {
            return 50;
        }

        foreach (var run in refiner.TakeQuiet(now, RefineTilesPerPass))
        {
            var pixels = new byte[run.Width * run.Height * 4];
            lock (rdp.FrameLock)
            {
                if ((rdp.Width & ~1) != refiner.Width || (rdp.Height & ~1) != refiner.Height)
                {
                    return 50;
                }

                CopyPixels(rdp.Framebuffer, rdp.Width * 4, run, pixels);
            }

            EmitRefinement(refiner, run, pixels);
        }

        return refiner.NextDue() is { } due ? (int)Math.Max(1, due - Environment.TickCount64) : int.MaxValue;
    }

    private static void CopyPixels(byte[] framebuffer, int stride, TileRect rect, byte[] target)
    {
        for (var y = 0; y < rect.Height; y++)
        {
            framebuffer.AsSpan((rect.Y + y) * stride + rect.X * 4, rect.Width * 4).CopyTo(target.AsSpan(y * rect.Width * 4));
        }
    }

    // Ein Rechteck als PNG; hat es zu viele Farben (Foto, Video), wird es in Kacheln geteilt, und was dann noch zu bunt ist, bleibt dem Video.
    private void EmitRefinement(TileRefiner refiner, TileRect rect, byte[] pixels)
    {
        var png = TilePng.Encode(pixels, rect.Width, rect.Height);
        if (png is not null)
        {
            _refineCredit -= png.Length;
            _events.Writer.TryWrite(new ScreenRefine(rect.X, rect.Y, rect.Width, rect.Height, png));
            return;
        }

        if (rect.Width <= TileRefiner.Tile)
        {
            refiner.Forget(rect);
            return;
        }

        var leftWidth = (rect.Width / TileRefiner.Tile + 1) / 2 * TileRefiner.Tile;
        foreach (var part in new[] { new TileRect(rect.X, rect.Y, leftWidth, rect.Height), new TileRect(rect.X + leftWidth, rect.Y, rect.Width - leftWidth, rect.Height) })
        {
            var sub = new byte[part.Width * part.Height * 4];
            for (var y = 0; y < part.Height; y++)
            {
                pixels.AsSpan((y * rect.Width + part.X - rect.X) * 4, part.Width * 4).CopyTo(sub.AsSpan(y * part.Width * 4));
            }

            EmitRefinement(refiner, part, sub);
        }
    }

    private bool Send(IVideoEncoder encoder, I420Frame frame, bool forceKey)
    {
        ReadOnlyMemory<byte> data;
        bool isKey;
        try
        {
            data = encoder.Encode(frame, forceKey, out isKey);
        }
        catch (Exception e) when (encoder.IsHardware && e is not OperationCanceledException)
        {
            // Die GPU hat mitten in der Sitzung aufgegeben: ab jetzt Software (H.264), mit einem Schluesselbild. Die Sitzung geht weiter.
            _log?.LogWarning(e, "Der Hardware-Kodierer {Encoder} ist ausgefallen; die Sitzung geht mit dem Software-Kodierer weiter.", encoder.Name);
            if (_encoderBackend is { } backend)
            {
                _selector.ReportFailure(backend, e);
            }

            _noHardware = true;
            _forceKey = true;
            ReplaceEncoder();
            return false;
        }

        if (data.IsEmpty)
        {
            return false;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        _window.Sent(sequence);
        var tick = Environment.TickCount64;
        lock (_pacerGate)
        {
            _pacer.Sent(sequence, data.Length, tick);
        }

        Volatile.Write(ref _lastSentAt, tick);
        _events.Writer.TryWrite(new ScreenPicture(sequence, isKey, data.ToArray(), encoder.Codec));
        return true;
    }

    private void Rebuild(int width, int height)
    {
        _encoder?.Dispose();
        _frame?.Dispose();
        _encoder = null;
        _frame = null;
        _window.Reset();

        var evenWidth = width & ~1;
        var evenHeight = height & ~1;
        if (!H264Encoder.Supports(evenWidth, evenHeight))
        {
            throw new RdpException($"The desktop is {width} x {height}, which the picture encoder does not take.", false);
        }

        _frame = new I420Frame(evenWidth, evenHeight);
        MakeEncoder(evenWidth, evenHeight);
        _frame.MarkChanged();
        var refiner = new TileRefiner(evenWidth, evenHeight);
        refiner.Mark(0, 0, evenWidth, evenHeight, Environment.TickCount64);
        _refiner = refiner;
        lock (_gate)
        {
            _dirty = true;
            _dirtyLeft = 0;
            _dirtyTop = 0;
            _dirtyRight = evenWidth;
            _dirtyBottom = evenHeight;
            _dirtyPixels = (long)evenWidth * evenHeight;
        }
    }

    // Ein anderer Kodierer fuer dasselbe Bild (der Browser kann ein Codec nicht, oder die GPU ist ausgefallen). Die naechste Runde sendet
    // ein Schluesselbild des ganzen Bildes; was noch unterwegs war, wird nicht mehr erwartet.
    private void ReplaceEncoder()
    {
        var frame = _frame;
        if (frame is null)
        {
            return;
        }

        _encoder?.Dispose();
        _encoder = null;
        _window.Reset();
        MakeEncoder(frame.Width, frame.Height);
        frame.MarkChanged();
        lock (_gate)
        {
            _dirty = true;
            _dirtyLeft = 0;
            _dirtyTop = 0;
            _dirtyRight = frame.Width;
            _dirtyBottom = frame.Height;
            _dirtyPixels = (long)frame.Width * frame.Height;
            _forceKey = true;
        }
    }

    private void MakeEncoder(int width, int height)
    {
        var choice = _selector.Choose(new EncoderRequest(width, height, FramesPerSecond, _kilobits, ClientCodecs), _noHardware);
        _encoder = choice.Encoder;
        _encoderBackend = choice.Backend;
        foreach (var line in choice.Skipped)
        {
            _log?.LogInformation("Kodierer uebersprungen: {Reason}", line);
        }

        _log?.LogInformation("Kodierer: {Name} ({Codec}, {Kind}), {Width} x {Height}", choice.Encoder.Name, choice.Encoder.Codec.Label(),
            choice.Encoder.IsHardware ? "GPU" : "CPU", width, height);
        var info = new ScreenEncoderInfo(choice.Encoder.Codec, choice.Encoder.IsHardware, choice.Encoder.Name);
        _lastEncoderInfo = info;
        _events.Writer.TryWrite(info);
    }

    internal VideoCodecs ClientCodecs
    {
        get
        {
            lock (_gate)
            {
                return _clientCodecs;
            }
        }
    }

    private bool TakeCodecChange()
    {
        lock (_gate)
        {
            var value = _codecChanged;
            _codecChanged = false;
            return value;
        }
    }

    // Eine Anfrage nach der anderen (eine zweite mitten im Wechsel laesst manche Server haengen); nach 3 s noch einmal, wenn nichts begann
    // (xrdp verwirft Anfragen, solange die Anmeldung laeuft), nach 30 s, wenn die Sitzung gerade neu aufgebaut wird.
    private async Task FollowSizeAsync(RdpClient rdp, CancellationToken cancellationToken)
    {
        var reactivating = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                var display = _display;
                var active = rdp.IsActive;
                if (!active)
                {
                    reactivating = true;
                }

                int wantWidth, wantHeight;
                long now;
                lock (_gate)
                {
                    wantWidth = _wantWidth;
                    wantHeight = _wantHeight;
                    now = Environment.TickCount64;
                    if (display is null || !display.IsReady || wantWidth <= 0 || _settled || (rdp.Width == wantWidth && rdp.Height == wantHeight))
                    {
                        continue;
                    }

                    var inFlight = _askedWidth > 0 && (rdp.Width != _askedWidth || rdp.Height != _askedHeight)
                        && now - _lastAskedAt < (reactivating ? 30_000 : 3_000);
                    if (inFlight || !active || now - _lastAskedAt < 400 || now - _resizedAt < 600)
                    {
                        continue;
                    }

                    _askedWidth = wantWidth;
                    _askedHeight = wantHeight;
                    _lastAskedAt = now;
                }

                display.SetSize(wantWidth, wantHeight);
                reactivating = false;
            }
        }
        catch (OperationCanceledException)
        {
            // die Sitzung endet
        }
    }

    // ---- kleine Helfer ----

    private void SetPendingSize(int width, int height)
    {
        lock (_gate)
        {
            _pendingSize = (width, height);
        }

        Wake();
    }

    private bool TakePendingSize(out (int Width, int Height) size)
    {
        lock (_gate)
        {
            if (_pendingSize is { } pending)
            {
                size = pending;
                _pendingSize = null;
                return true;
            }
        }

        size = default;
        return false;
    }

    private bool PeekDirty(out int left, out int top, out int width, out int height, out long pixels)
    {
        lock (_gate)
        {
            left = _dirtyLeft;
            top = _dirtyTop;
            width = _dirtyRight - _dirtyLeft;
            height = _dirtyBottom - _dirtyTop;
            pixels = _dirtyPixels;
            return _dirty;
        }
    }

    private void ClearDirty()
    {
        lock (_gate)
        {
            _dirty = false;
            _dirtyPixels = 0;
        }
    }

    private bool TakeForceKey()
    {
        lock (_gate)
        {
            var value = _forceKey;
            _forceKey = false;
            return value;
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { /* schon geweckt */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_rdp is not null)
        {
            await _rdp.DisposeAsync().ConfigureAwait(false);
        }

        _clipboard.Dispose();
        _encoder?.Dispose();
        _frame?.Dispose();
        _events.Writer.TryComplete();
        _stop.Dispose();
        _wake.Dispose();
    }
}
