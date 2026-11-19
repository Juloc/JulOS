namespace JulOS.Integrations.Display.Refine;

internal readonly record struct TileRect(int X, int Y, int Width, int Height);

/// <summary>
/// Buchfuehrung fuer die verlustfreie Nachbesserung: Der H.264-Strom ist bei Farbe (4:2:0) und Kanten nie ganz scharf. Steht ein Teil des
/// Bildschirms still, bekommt der Browser ihn zusaetzlich verlustfrei (PNG) und legt ihn ueber das Video. Hier wird nur gezaehlt, welche
/// Kacheln (32 x 32) sich geaendert haben, seit wann, und welche der Browser schon nachgebessert hat. Der Rest (Pixel holen, PNG, Senden)
/// steht in <see cref="ScreenStream"/>.
///
/// Eine Kachel ist "schmutzig", wenn sich ihr Inhalt seit der letzten Nachbesserung geaendert hat; sie wird nachgebessert, sobald sie
/// QuietMilliseconds lang ruhig war (so kommt nichts fuer Bilder, die sich bewegen). Eine Kachel mit Nachbesserung ("gueltig"), die sich
/// wieder aendert, muss vor dem naechsten Videobild beim Browser gestrichen werden (<see cref="TakeInvalidations"/>), sonst bliebe das
/// alte Bild darueber stehen.
/// </summary>
internal sealed class TileRefiner
{
    public const int Tile = 32;

    private readonly Lock _gate = new();
    private readonly int _columns, _rows;
    private readonly long[] _changedAt;      // 0: sauber, sonst Zeit der letzten Aenderung (mindestens 1)
    private readonly bool[] _valid;          // der Browser hat eine Nachbesserung fuer diese Kachel
    private int _dirty;

    public TileRefiner(int width, int height, int quietMilliseconds = 120)
    {
        Width = width;
        Height = height;
        QuietMilliseconds = quietMilliseconds;
        _columns = (width + Tile - 1) / Tile;
        _rows = (height + Tile - 1) / Tile;
        _changedAt = new long[_columns * _rows];
        _valid = new bool[_columns * _rows];
    }

    public int Width { get; }

    public int Height { get; }

    public int QuietMilliseconds { get; }

    /// <summary>Wie viele Kacheln noch auf ihre Nachbesserung warten.</summary>
    public int DirtyTiles => Volatile.Read(ref _dirty);

    /// <summary>Dieses Rechteck des Bildschirms hat sich geaendert.</summary>
    public void Mark(int x, int y, int width, int height, long now)
    {
        var first = Math.Max(0, x) / Tile;
        var firstRow = Math.Max(0, y) / Tile;
        var last = Math.Min(_columns, (x + width + Tile - 1) / Tile);
        var lastRow = Math.Min(_rows, (y + height + Tile - 1) / Tile);
        if (first >= last || firstRow >= lastRow)
        {
            return;
        }

        var stamp = Math.Max(1, now);
        lock (_gate)
        {
            for (var row = firstRow; row < lastRow; row++)
            {
                for (var column = first; column < last; column++)
                {
                    var i = row * _columns + column;
                    if (_changedAt[i] == 0)
                    {
                        _dirty++;
                    }

                    _changedAt[i] = stamp;
                }
            }
        }
    }

    /// <summary>
    /// Kacheln, die der Browser nachgebessert hat und die sich seitdem geaendert haben: sie sind ab jetzt nicht mehr nachgebessert (der Aufrufer
    /// sagt es dem Browser, bevor das Videobild mit der Aenderung hinausgeht). Nebeneinanderliegende Kacheln einer Zeile sind ein Rechteck.
    /// </summary>
    public List<TileRect> TakeInvalidations()
    {
        var result = new List<TileRect>();
        lock (_gate)
        {
            for (var row = 0; row < _rows; row++)
            {
                var start = -1;
                for (var column = 0; column <= _columns; column++)
                {
                    var hit = column < _columns && _valid[row * _columns + column] && _changedAt[row * _columns + column] != 0;
                    if (hit)
                    {
                        _valid[row * _columns + column] = false;
                        if (start < 0)
                        {
                            start = column;
                        }
                    }
                    else if (start >= 0)
                    {
                        result.Add(Cut(start, column, row));
                        start = -1;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Kacheln, die lange genug ruhig waren, als Rechtecke (Reihen nebeneinanderliegender Kacheln, hoechstens maxRun breit), hoechstens
    /// maxTiles Kacheln. Sie gelten ab jetzt als nachgebessert und sauber; kommt eine Aenderung dazwischen, wird sie wieder schmutzig.
    /// </summary>
    public List<TileRect> TakeQuiet(long now, int maxTiles, int maxRun = 20)
    {
        var result = new List<TileRect>();
        if (DirtyTiles == 0)
        {
            return result;
        }

        lock (_gate)
        {
            var taken = 0;
            for (var row = 0; row < _rows && taken < maxTiles; row++)
            {
                var column = 0;
                while (column < _columns && taken < maxTiles)
                {
                    if (!IsReady(row * _columns + column, now))
                    {
                        column++;
                        continue;
                    }

                    var start = column;
                    while (column < _columns && taken < maxTiles && column - start < maxRun && IsReady(row * _columns + column, now))
                    {
                        var i = row * _columns + column;
                        _changedAt[i] = 0;
                        _dirty--;
                        _valid[i] = true;
                        taken++;
                        column++;
                    }

                    result.Add(Cut(start, column, row));
                }
            }
        }

        return result;
    }

    /// <summary>Die Nachbesserung dieses Rechtecks kam nicht zustande (zu viele Farben): sie gilt nicht als vorhanden.</summary>
    public void Forget(TileRect rect)
    {
        lock (_gate)
        {
            for (var row = rect.Y / Tile; row < (rect.Y + rect.Height + Tile - 1) / Tile && row < _rows; row++)
            {
                for (var column = rect.X / Tile; column < (rect.X + rect.Width + Tile - 1) / Tile && column < _columns; column++)
                {
                    _valid[row * _columns + column] = false;
                }
            }
        }
    }

    /// <summary>Wann (Uhr der Aufrufer) die naechste Kachel ruhig genug ist; null, wenn keine wartet.</summary>
    public long? NextDue()
    {
        if (DirtyTiles == 0)
        {
            return null;
        }

        lock (_gate)
        {
            long newest = long.MaxValue;
            foreach (var at in _changedAt)
            {
                if (at != 0 && at < newest)
                {
                    newest = at;
                }
            }

            return newest == long.MaxValue ? null : newest + QuietMilliseconds;
        }
    }

    private bool IsReady(int i, long now) => _changedAt[i] != 0 && now - _changedAt[i] >= QuietMilliseconds;

    private TileRect Cut(int firstColumn, int endColumn, int row)
    {
        var x = firstColumn * Tile;
        var y = row * Tile;
        return new TileRect(x, y, Math.Min(Width, endColumn * Tile) - x, Math.Min(Height, (row + 1) * Tile) - y);
    }
}
