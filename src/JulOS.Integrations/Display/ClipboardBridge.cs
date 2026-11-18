using JulOS.Integrations.Display.Rdp.Channels;

namespace JulOS.Integrations.Display;

/// <summary>
/// Text der Zwischenablage zwischen Browser und fernem Rechner, beide Richtungen. Der Kanal (<see cref="ClipboardChannel"/>) kennt nur
/// Ankuendigungen und Anfragen: wer etwas kopiert, sagt nur, welche Formate er haette, und erst beim Einfuegen wird der Inhalt geholt.
/// Hier gilt: kopiert der ferne Rechner Text, holen wir ihn sofort und geben ihn dem Browser (der hat keine Zwischenablage, die er
/// auf Anfrage fuellen koennte); legt der Browser Text ab, kuendigen wir ihn an und liefern ihn, wenn der ferne Rechner einfuegt.
/// Nur Text (CF_UNICODETEXT); Bilder und Dateien gehen nicht ueber diese Bruecke.
/// </summary>
public sealed class ClipboardBridge
{
    /// <summary>Laenger darf ein Text auf dem Weg zwischen Browser und Server nicht sein (UTF-8, in beide Richtungen).</summary>
    public const int MaxTextBytes = 1024 * 1024;

    private readonly ClipboardChannel _channel;
    private readonly Action<string> _remoteText;
    private readonly Lock _gate = new();
    private byte[]? _local;
    private string? _lastLocal;

    /// <param name="remoteText">Der ferne Rechner hat Text kopiert. Wird auf dem Lesefaden des RDP-Clients gerufen: schnell zurueckkehren.</param>
    public ClipboardBridge(ClipboardChannel channel, Action<string> remoteText)
    {
        _channel = channel;
        _remoteText = remoteText;
        channel.RemoteFormats += OnRemoteFormats;
        channel.DataReceived += OnDataReceived;
        channel.DataRequested += OnDataRequested;
    }

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
        }

        _channel.AnnounceFormats([new ClipFormat(ClipboardText.FormatId, null)]);
    }

    private void OnRemoteFormats(IReadOnlyList<ClipFormat> formats)
    {
        foreach (var format in formats)
        {
            if (format.Id == ClipboardText.FormatId && format.Name is null)
            {
                _channel.RequestData(ClipboardText.FormatId);
                return;
            }
        }
    }

    private void OnDataReceived(byte[]? data)
    {
        if (data is null || data.Length > 2 * MaxTextBytes)
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

        _remoteText(text);
    }

    private void OnDataRequested(uint formatId)
    {
        byte[]? data;
        lock (_gate)
        {
            data = formatId == ClipboardText.FormatId ? _local : null;
        }

        _channel.SendData(data);
    }
}
