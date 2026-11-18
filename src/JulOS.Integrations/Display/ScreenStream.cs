using System.Threading.Channels;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Channels;
using JulOS.Integrations.Display.Video;
using Microsoft.Extensions.Logging;

namespace JulOS.Integrations.Display;

/// <summary>Was der Browser von einer Bildschirm-Sitzung hoert.</summary>
public abstract record ScreenEvent;

/// <summary>Der Desktop hat diese Groesse; ab jetzt kommen Bilder in dieser Groesse (das naechste ist ein Schluesselbild).</summary>
public sealed record ScreenSize(int Width, int Height) : ScreenEvent;

/// <summary>Ein Bild als H.264 (Annex B, SPS und PPS vor jedem Schluesselbild). Der Browser bestaetigt jedes Bild.</summary>
public sealed record ScreenPicture(int Sequence, bool IsKey, byte[] Data) : ScreenEvent;

/// <summary>Der ferne Rechner hat Text kopiert.</summary>
public sealed record ScreenClipboardText(string Text) : ScreenEvent;

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
    private RdpClient? _rdp;
    private DisplayControlChannel? _display;
    private H264Encoder? _encoder;
    private I420Frame? _frame;

    // Alles, was sich seit dem letzten Bild geaendert hat: ein Rechteck und eine grobe Zaehlung der Pixel (unter _gate).
    private bool _dirty;
    private int _dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom;
    private long _dirtyPixels;
    private (int Width, int Height)? _pendingSize;

    private int _inFlight;
    private long _lastAckAt, _lastSentAt, _lastUpdateAt;
    private int _sequence;
    private int _disposed;

    // Gewuenschte Groesse des Desktops (die des Browserfensters) und was daraus geworden ist; unter _gate gelesen und geschrieben.
    private int _wantWidth, _wantHeight, _askedWidth, _askedHeight;
    private bool _settled = true;
    private long _lastAskedAt, _resizedAt;

    public ScreenStream(RdpOptions options, int maxKilobitsPerSecond = 16_000, ILogger? log = null)
    {
        _options = options;
        _maxKilobitsPerSecond = maxKilobitsPerSecond;
        _log = log;
        _clipboard = new ClipboardBridge(_clipboardChannel, text => _events.Writer.TryWrite(new ScreenClipboardText(text)));
    }

    /// <summary>Ob H.264 auf dieser Maschine kodiert werden kann (libopenh264 vorhanden).</summary>
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
        rdp.AddChannel(channels);
        rdp.AddChannel(_clipboardChannel);
        _display = display;
        try
        {
            await rdp.ConnectAsync(linked.Token).ConfigureAwait(false);
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

    /// <summary>Der Browser hat ein Bild dekodiert.</summary>
    public void Acknowledge()
    {
        Volatile.Write(ref _lastAckAt, Environment.TickCount64);
        while (true)
        {
            var current = Volatile.Read(ref _inFlight);
            if (current <= 0 || Interlocked.CompareExchange(ref _inFlight, current - 1, current) == current)
            {
                break;
            }
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
        Volatile.Write(ref _lastUpdateAt, Environment.TickCount64);
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
        _events.Writer.TryWrite(new ScreenCursor(CursorKind.Shape, hotX, hotY, width, height, rgba.ToArray()));

    void IRdpHandler.CursorHidden() => _events.Writer.TryWrite(new ScreenCursor(CursorKind.Hidden, 0, 0, 0, 0, null));

    void IRdpHandler.CursorDefault() => _events.Writer.TryWrite(new ScreenCursor(CursorKind.Default, 0, 0, 0, 0, null));

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

            var wait = IdleWaitMs;
            var encoder = _encoder;
            var frame = _frame;

            var inFlight = Volatile.Read(ref _inFlight);
            if (inFlight >= MaxPicturesInFlight)
            {
                var tick = Environment.TickCount64;
                var quietFor = tick - Math.Max(Volatile.Read(ref _lastAckAt), Volatile.Read(ref _lastSentAt));
                if (quietFor > AckStallMs)
                {
                    _log?.LogWarning("Keine Bestaetigung seit {Quiet} ms bei {InFlight} offenen Bildern (letztes Bild vor {Sent} ms, letzte Aenderung vor {Update} ms): Fenster zurueckgesetzt, Schluesselbild folgt",
                        quietFor, inFlight, tick - Volatile.Read(ref _lastSentAt), tick - Volatile.Read(ref _lastUpdateAt));
                    Volatile.Write(ref _inFlight, 0);
                    needKey = true;
                }
            }
            if (encoder is not null && frame is not null && !Volatile.Read(ref _paused) && Volatile.Read(ref _inFlight) < MaxPicturesInFlight)
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
                else if (needKey && !encodedAny)
                {
                    // Kein neuer Inhalt, aber noch nie etwas gesendet: der ganze Bildschirm ist das erste Bild
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

    private bool Send(H264Encoder encoder, I420Frame frame, bool forceKey)
    {
        var data = encoder.Encode(frame, forceKey, out var isKey);
        if (data.IsEmpty)
        {
            return false;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        Interlocked.Increment(ref _inFlight);
        Volatile.Write(ref _lastSentAt, Environment.TickCount64);
        _events.Writer.TryWrite(new ScreenPicture(sequence, isKey, data.ToArray()));
        return true;
    }

    private void Rebuild(int width, int height)
    {
        _encoder?.Dispose();
        _frame?.Dispose();
        _encoder = null;
        _frame = null;
        Volatile.Write(ref _inFlight, 0);

        var evenWidth = width & ~1;
        var evenHeight = height & ~1;
        if (!H264Encoder.Supports(evenWidth, evenHeight))
        {
            throw new RdpException($"The desktop is {width} x {height}, which the picture encoder does not take.", false);
        }

        _frame = new I420Frame(evenWidth, evenHeight);
        _encoder = new H264Encoder(evenWidth, evenHeight, FramesPerSecond, _maxKilobitsPerSecond);
        _frame.MarkChanged();
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

        _encoder?.Dispose();
        _frame?.Dispose();
        _events.Writer.TryComplete();
        _stop.Dispose();
        _wake.Dispose();
    }
}
