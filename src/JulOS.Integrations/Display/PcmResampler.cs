using System.Buffers.Binary;

namespace JulOS.Integrations.Display;

/// <summary>
/// Macht aus Mono-Ton (16 Bit, little endian) in einer Rate Ton in der Rate und mit der Kanalzahl, die der ferne Rechner fuer das Mikrofon
/// verlangt. Linear interpoliert: fuer Sprache reicht das, und es kostet kaum etwas. Behaelt den Rest zwischen zwei Aufrufen, damit an den
/// Grenzen der Stuecke nichts knackt. Nicht fuer mehrere Faeden gedacht.
/// </summary>
public sealed class PcmResampler
{
    private double _phase;
    private short _previous;
    private int _lastIn, _lastOut;

    public byte[] Convert(ReadOnlySpan<byte> mono, int inRate, int outRate, int outChannels)
    {
        var count = mono.Length / 2;
        if (count == 0 || inRate <= 0 || outRate <= 0 || outChannels < 1)
        {
            return [];
        }

        if (inRate != _lastIn || outRate != _lastOut)
        {
            _phase = 0;
            _lastIn = inRate;
            _lastOut = outRate;
        }

        if (inRate == outRate)
        {
            return Spread(mono, count, outChannels);
        }

        var step = (double)inRate / outRate;
        var capacity = (int)((count - _phase) / step) + 2;
        var output = new byte[capacity * outChannels * 2];
        var written = 0;
        var phase = _phase;
        while (phase < count)
        {
            var index = (int)phase;
            var fraction = phase - index;
            var a = index == 0 ? _previous : BinaryPrimitives.ReadInt16LittleEndian(mono[((index - 1) * 2)..]);
            var b = BinaryPrimitives.ReadInt16LittleEndian(mono[(index * 2)..]);
            var value = (short)Math.Round(a + (b - a) * fraction);
            for (var c = 0; c < outChannels; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan((written * outChannels + c) * 2), value);
            }

            written++;
            phase += step;
        }

        _phase = phase - count;
        _previous = BinaryPrimitives.ReadInt16LittleEndian(mono[((count - 1) * 2)..]);
        return output.AsSpan(0, written * outChannels * 2).ToArray();
    }

    private static byte[] Spread(ReadOnlySpan<byte> mono, int count, int channels)
    {
        if (channels == 1)
        {
            return mono[..(count * 2)].ToArray();
        }

        var output = new byte[count * channels * 2];
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                mono.Slice(i * 2, 2).CopyTo(output.AsSpan((i * channels + c) * 2));
            }
        }

        return output;
    }
}
