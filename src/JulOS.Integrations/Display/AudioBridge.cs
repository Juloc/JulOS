using JulOS.Integrations.Display.Rdp.Channels;

namespace JulOS.Integrations.Display;

/// <summary>Ein Stueck Ton vom fernen Rechner: 16 Bit, little endian, Kanaele ineinander verschachtelt.</summary>
public sealed record AudioBlock(ReadOnlyMemory<byte> Pcm, int Rate, int Channels, uint TimeMs);

/// <summary>Was der Browser ueber den Ton der Sitzung wissen muss.</summary>
public readonly record struct AudioStatus(bool Output, bool MicrophoneAvailable, bool MicrophoneRecording, int MicrophoneRate)
{
    /// <summary>Die Nachricht an den Browser (siehe <see cref="ScreenProtocol"/>): Bit 0 Ausgabe, Bit 1 Mikrofon-Kanal, Bit 2 Mikrofon verlangt.</summary>
    public byte Flags => (byte)((Output ? 1 : 0) | (MicrophoneAvailable ? 2 : 0) | (MicrophoneRecording ? 4 : 0));
}

/// <summary>
/// Ton zwischen Browser und fernem Rechner: was der Rechner abspielt (<see cref="SoundChannel"/>) geht an den Browser, was dessen Mikrofon
/// aufnimmt (<see cref="AudioInputChannel"/>) an den Rechner. Beides ist pro Verbindung aus, bis der Browser es einschaltet.
///
/// Der Ton wartet hier in einer kleinen Warteschlange auf den Sender des WebSockets. Sie ist begrenzt: ein stehender oder verdeckter
/// Tab bekommt hoechstens <see cref="MaxQueuedMs"/> Ton gepuffert, das Aelteste faellt weg. So wird nichts aufgestaut, und was ankommt,
/// ist so frisch wie moeglich.
/// </summary>
public sealed class AudioBridge
{
    /// <summary>So viel Ton darf auf den Sender warten; mehr ist zu alt, um noch abgespielt zu werden.</summary>
    public const int MaxQueuedMs = 200;

    /// <summary>Mikrofon-Rate, die der Browser nimmt, wenn der ferne Rechner noch nichts verlangt hat.</summary>
    public const int DefaultMicrophoneRate = 16000;

    private readonly SoundChannel _sound;
    private readonly AudioInputChannel _microphone;
    private readonly Lock _gate = new();
    private readonly Queue<AudioBlock> _queue = new();
    private readonly PcmResampler _resampler = new();
    private long _queuedMs;
    private bool _signalled;
    private AudioStatus _last;

    /// <summary>Es liegt Ton bereit (hoechstens einmal, bis <see cref="TryTake"/> die Warteschlange geleert hat). Wird auf dem Lesefaden des RDP-Clients gerufen.</summary>
    public event Action? Ready;

    /// <summary>Der <see cref="Status"/> hat sich geaendert.</summary>
    public event Action<AudioStatus>? StatusChanged;

    public AudioBridge(SoundChannel sound, AudioInputChannel microphone)
    {
        _sound = sound;
        _microphone = microphone;
        sound.Enabled = false;
        sound.Samples += OnSamples;
        sound.Stopped += Clear;
        sound.FormatsChanged += OnChanged;
        microphone.StateChanged += OnChanged;
        microphone.Recording += _ => OnChanged();
    }

    /// <summary>Ob der Browser den Ton des fernen Rechners hoeren will.</summary>
    public bool OutputOn
    {
        get => _sound.Enabled;
        set
        {
            _sound.Enabled = value;
            if (!value)
            {
                Clear();
            }
        }
    }

    public AudioStatus Status => new(_sound.HasFormats, _microphone.IsAvailable, _microphone.IsRecording,
        _microphone.Format?.Rate ?? DefaultMicrophoneRate);

    /// <summary>Nimmt das aelteste wartende Stueck. Ist keins da, ist die Warteschlange leer und <see cref="Ready"/> wird wieder gerufen.</summary>
    public bool TryTake(out AudioBlock block)
    {
        lock (_gate)
        {
            if (_queue.TryDequeue(out block!))
            {
                _queuedMs -= DurationMs(block);
                return true;
            }

            _queuedMs = 0;
            _signalled = false;
            return false;
        }
    }

    /// <summary>
    /// Mono-Ton aus dem Browser (16 Bit, little endian, in der Rate <paramref name="rate"/>) fuer das Mikrofon des fernen Rechners. Wird
    /// in dessen Format umgerechnet. Verworfen, solange der ferne Rechner keinen Ton verlangt.
    /// </summary>
    public void Microphone(ReadOnlySpan<byte> mono, int rate)
    {
        if (_microphone.Format is not { } format || !_microphone.IsRecording)
        {
            return;
        }

        _microphone.SendSamples(_resampler.Convert(mono, rate, format.Rate, format.Channels));
    }

    private void OnSamples(ReadOnlyMemory<byte> pcm, int rate, int channels)
    {
        var block = new AudioBlock(pcm, rate, channels, (uint)Environment.TickCount64);
        bool notify;
        lock (_gate)
        {
            _queue.Enqueue(block);
            _queuedMs += DurationMs(block);
            while (_queuedMs > MaxQueuedMs && _queue.Count > 1)
            {
                _queuedMs -= DurationMs(_queue.Dequeue());
            }

            notify = !_signalled;
            _signalled = true;
        }

        if (notify)
        {
            Ready?.Invoke();
        }
    }

    private void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _queuedMs = 0;
        }
    }

    private void OnChanged()
    {
        var status = Status;
        lock (_gate)
        {
            if (status == _last)
            {
                return;
            }

            _last = status;
        }

        StatusChanged?.Invoke(status);
    }

    private static long DurationMs(AudioBlock block) => block.Pcm.Length * 1000L / (block.Channels * 2L * block.Rate);
}
