using System.Buffers.Binary;

namespace JulOS.Integrations.Display.Vnc;

// The server's bytes, read one after the other with blocking reads (the session has a thread of its own for this). A message of this
// protocol has no length in front, so the decoders pull what they need as they parse; a buffer keeps that from being a system call per number.
internal sealed class RfbReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[32 * 1024];
    private int _position;
    private int _length;

    // Bytes that were read from the stream but not handed out yet. Zero is required before the stream is wrapped in TLS.
    public int Buffered => _length - _position;

    private void Fill()
    {
        var count = stream.Read(_buffer, 0, _buffer.Length);
        if (count <= 0)
        {
            throw new EndOfStreamException();
        }

        _position = 0;
        _length = count;
    }

    public byte U8()
    {
        if (_position == _length)
        {
            Fill();
        }

        return _buffer[_position++];
    }

    public int U16()
    {
        var high = U8();
        return (high << 8) | U8();
    }

    public uint U32()
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public int I32() => (int)U32();

    public void ReadExactly(Span<byte> destination)
    {
        while (destination.Length > 0)
        {
            if (_position == _length)
            {
                // A large piece goes straight into the destination
                if (destination.Length >= _buffer.Length)
                {
                    var count = stream.Read(destination);
                    if (count <= 0)
                    {
                        throw new EndOfStreamException();
                    }

                    destination = destination[count..];
                    continue;
                }

                Fill();
            }

            var take = Math.Min(destination.Length, _length - _position);
            _buffer.AsSpan(_position, take).CopyTo(destination);
            _position += take;
            destination = destination[take..];
        }
    }

    public byte[] Bytes(int count)
    {
        if (count < 0)
        {
            throw new VncProtocolException("a negative length");
        }

        var result = new byte[count];
        ReadExactly(result);
        return result;
    }

    public void Skip(long count)
    {
        Span<byte> scratch = stackalloc byte[512];
        while (count > 0)
        {
            var step = (int)Math.Min(count, scratch.Length);
            ReadExactly(scratch[..step]);
            count -= step;
        }
    }

    // A string with a 32-bit length in front (reasons, desktop name), cut at a sane size.
    public string Text(int maxLength = 4096)
    {
        var length = I32();
        if (length < 0 || length > 1 << 20)
        {
            throw new VncProtocolException("a text of " + length + " bytes");
        }

        var bytes = Bytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, maxLength));
    }
}

// The server's bytes do not hold what the protocol says. Never shown as it is: the client turns it into an RdpException.
internal sealed class VncProtocolException(string message) : Exception(message);
