namespace JulOS.Integrations.Guacamole;

/// <summary>
/// Liest einen Bytestrom von guacd und gibt ihn blockweise mit nur ganzen Anweisungen heraus (kein Kopieren pro Anweisung).
/// Der zurueckgegebene Speicher gilt bis zum naechsten Aufruf.
/// </summary>
public sealed class GuacReader(Stream stream, int capacity = 64 * 1024) : IDisposable
{
    private byte[] _buf = System.Buffers.ArrayPool<byte>.Shared.Rent(capacity);
    private InstructionScanner _scanner;
    private int _start, _complete, _filled;

    public async ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_complete > _start)
            {
                var block = _buf.AsMemory(_start, _complete - _start);
                _start = _complete;
                return block;
            }
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _filled - _start);
                _filled -= _start; _complete = 0; _start = 0;
            }
            if (_filled == _buf.Length) throw new InvalidDataException("Anweisung zu lang.");
            var n = await stream.ReadAsync(_buf.AsMemory(_filled), ct).ConfigureAwait(false);
            if (n == 0) return ReadOnlyMemory<byte>.Empty;
            var boundary = _scanner.Scan(_buf.AsSpan(_filled, n));
            if (boundary >= 0) _complete = _filled + boundary + 1;
            _filled += n;
        }
    }

    /// <summary>Fuer den Verbindungsaufbau: genau eine Anweisung; was danach schon da ist, bleibt fuer <see cref="ReadBlockAsync"/> liegen.</summary>
    public async ValueTask<string[]?> ReadInstructionAsync(CancellationToken ct)
    {
        var block = await ReadBlockAsync(ct).ConfigureAwait(false);
        if (block.IsEmpty) return null;
        var first = FirstEnd(block.Span);
        _start -= block.Length - first;                     // Rest zurueckgeben (Speicher ist unveraendert)
        return GuacProtocol.Parse(block.Span[..first]);
    }

    private static int FirstEnd(ReadOnlySpan<byte> data)
    {
        var s = new InstructionScanner();
        for (var i = 0; i < data.Length; i++) if (s.Scan(data.Slice(i, 1)) >= 0) return i + 1;
        throw new InvalidDataException("Unvollständige Anweisung.");
    }

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buf, []);
        if (b.Length > 0) System.Buffers.ArrayPool<byte>.Shared.Return(b);
    }
}
