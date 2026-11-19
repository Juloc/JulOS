namespace JulOS.Integrations.Display;

/// <summary>
/// Was eine Bildschirm-Sitzung von der Quelle ihres Bildes braucht (RDP-Client oder VNC-Client): einen Bildspeicher, den sie lesen kann, und Eingaben,
/// die sie weitergibt. Der Strom (<see cref="ScreenStream"/>) kodiert das Bild immer gleich, egal woher es kommt.
/// </summary>
public interface IScreenSource : IAsyncDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>BGRX, vier Bytes je Pixel, Blau zuerst, von oben nach unten, Zeilenlaenge Breite * 4. Nur unter <see cref="FrameLock"/> lesen: bei einer neuen Groesse wird er ersetzt.</summary>
    byte[] Framebuffer { get; }

    object FrameLock { get; }

    /// <summary>Warum der ferne Rechner die Sitzung beendet hat, falls er es gesagt hat.</summary>
    string? EndReason { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task RunAsync(CancellationToken cancellationToken);

    void Disconnect();

    void MouseMove(int x, int y);

    /// <summary>Tasten wie im Browser: 0 links, 1 Mitte, 2 rechts, 3 zurueck, 4 vor.</summary>
    void MouseButton(int button, bool down);

    /// <summary>Raddrehung in Windows-Einheiten (120 je Raste); senkrecht positiv ist vom Benutzer weg (hoch), waagerecht positiv ist nach rechts.</summary>
    void MouseWheel(int delta, bool horizontal);

    /// <summary>Eine Taste ueber ihren Scancode (Satz 1).</summary>
    void Key(int scancode, bool extended, bool down);

    /// <summary>Ein Zeichen (UTF-16-Einheit), gedrueckt und losgelassen.</summary>
    void Unicode(char unit);
}

/// <summary>Was die Quelle dem meldet, der das Bild zeigt. Alles wird auf ihrer Leseschleife aufgerufen: schnell zurueckkehren, nicht in die Quelle zurueckrufen und dort warten.</summary>
public interface IScreenHandler
{
    /// <summary>Ein Rechteck des Bildspeichers hat sich geaendert (es steht schon im Bildspeicher).</summary>
    void Updated(int x, int y, int width, int height);

    /// <summary>Der Desktop hat eine neue Groesse; der Bildspeicher wurde ersetzt.</summary>
    void Resized(int width, int height);

    /// <summary>Die Form des Zeigers: RGBA, R zuerst, gerade Alpha, von oben nach unten.</summary>
    void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba);

    void CursorHidden();

    void CursorDefault();
}
