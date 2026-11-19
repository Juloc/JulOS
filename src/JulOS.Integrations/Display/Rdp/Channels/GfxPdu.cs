using System.Buffers.Binary;

namespace JulOS.Integrations.Display.Rdp.Channels;

// The messages of the graphics pipeline (MS-RDPEGFX), as bytes: how they are read and how the two the client
// sends are built. Everything is little endian. Nothing here trusts a length from the wire.
//
// Server to client, one dynamic channel message holds a segmented container (2.2.5, "RDP_SEGMENTED_DATA", the
// wrapper of the bulk compression) around one or more PDUs; every PDU has an 8 byte header: command, flags
// (unused) and its own length.
internal static class GfxPdu
{
    public const int HeaderLength = 8;

    public const ushort WireToSurface1 = 0x0001;
    public const ushort WireToSurface2 = 0x0002;
    public const ushort DeleteEncodingContext = 0x0003;
    public const ushort SolidFill = 0x0004;
    public const ushort SurfaceToSurface = 0x0005;
    public const ushort SurfaceToCache = 0x0006;
    public const ushort CacheToSurface = 0x0007;
    public const ushort EvictCacheEntry = 0x0008;
    public const ushort CreateSurface = 0x0009;
    public const ushort DeleteSurface = 0x000A;
    public const ushort StartFrame = 0x000B;
    public const ushort EndFrame = 0x000C;
    public const ushort FrameAcknowledge = 0x000D;
    public const ushort ResetGraphics = 0x000E;
    public const ushort MapSurfaceToOutput = 0x000F;
    public const ushort CacheImportOffer = 0x0010;
    public const ushort CacheImportReply = 0x0011;
    public const ushort CapsAdvertise = 0x0012;
    public const ushort CapsConfirm = 0x0013;
    public const ushort MapSurfaceToWindow = 0x0015;
    public const ushort QoeFrameAcknowledge = 0x0016;
    public const ushort MapSurfaceToScaledOutput = 0x0017;
    public const ushort MapSurfaceToScaledWindow = 0x0018;

    // The codec ids of WIRE_TO_SURFACE_1 (2.2.1.1); only AVC420 is carried on.
    public const ushort CodecUncompressed = 0x0000;
    public const ushort CodecAvc420 = 0x000B;

    public const uint CapsVersion81 = 0x00080105;
    public const uint CapsFlagAvc420Enabled = 0x00000010;
    public const uint CapsFlagAvcDisabled = 0x00000020;

    // What the client puts into FRAME_ACKNOWLEDGE's queue depth to say "do not wait for acknowledgements" is
    // never used: the acknowledgement is the client's way of pacing the server.
    public const uint QueueDepthUnavailable = 0;

    // A server never sends one container of more than this; a longer claim is a broken message.
    public const int MaxPayload = 16 * 1024 * 1024;

    // ---- the client's messages ---------------------------------------------------------------------------------

    // CAPS_ADVERTISE with the one set this client wants: version 8.1 with H.264 (AVC420) switched on. Offering no
    // older set is deliberate: a server that falls back to one of them would send pictures this client cannot
    // pass on, so it is better told straight that there is nothing in common.
    public static byte[] BuildCapsAdvertise()
    {
        var message = new ByteWriter(HeaderLength + 2 + 4 + 4 + 4);
        Header(message, CapsAdvertise, message.Length);
        return message.U16(1).U32(CapsVersion81).U32(4).U32(CapsFlagAvc420Enabled).ToArray();
    }

    // FRAME_ACKNOWLEDGE: the client has finished with this frame; queueDepth says how many frames wait for it.
    public static byte[] BuildFrameAcknowledge(uint frameId, uint totalFramesDecoded, uint queueDepth = 0)
    {
        var message = new ByteWriter(HeaderLength + 12);
        Header(message, FrameAcknowledge, message.Length);
        return message.U32(queueDepth).U32(frameId).U32(totalFramesDecoded).ToArray();
    }

    private static void Header(ByteWriter message, ushort command, int length) => message.U16(command).U16(0).U32((uint)length);

    // ---- reading -----------------------------------------------------------------------------------------------

    // The container around a channel message. One segment, or several that follow each other; each starts with a
    // byte that says whether the bulk compression (3.1.9.1) was applied. This client does not decompress, so a
    // compressed segment ends in false - the caller gives the pipeline up then.
    public static bool TryUnwrap(ReadOnlySpan<byte> message, out byte[] payload, out string? problem)
    {
        payload = [];
        problem = null;
        var reader = new ByteReader(message);
        if (!reader.TryU8(out var descriptor))
        {
            problem = "an empty message";
            return false;
        }

        switch (descriptor)
        {
            case 0xE0:
                // A single segment: the bulk header, then the data.
                if (!reader.TryU8(out var single))
                {
                    problem = "a segment without a header";
                    return false;
                }

                if ((single & 0x20) != 0)
                {
                    problem = "compressed data, which this client does not unpack";
                    return false;
                }

                payload = reader.Rest.ToArray();
                return true;
            case 0xE1:
                if (!reader.TryU16(out var count) || !reader.TryU32(out var total) || total > MaxPayload)
                {
                    problem = "a multipart container with an unreasonable size";
                    return false;
                }

                var result = new byte[total];
                var filled = 0;
                for (var i = 0; i < count; i++)
                {
                    if (!reader.TryU32(out var size) || size < 1 || !reader.TryBytes(size, out var segment))
                    {
                        problem = "a segment that is cut short";
                        return false;
                    }

                    if ((segment[0] & 0x20) != 0)
                    {
                        problem = "compressed data, which this client does not unpack";
                        return false;
                    }

                    var data = segment[1..];
                    if (data.Length > total - filled)
                    {
                        problem = "segments that are longer than the container says";
                        return false;
                    }

                    data.CopyTo(result.AsSpan(filled));
                    filled += data.Length;
                }

                if (filled != total)
                {
                    problem = "segments that are shorter than the container says";
                    return false;
                }

                payload = result;
                return true;
            default:
                problem = $"a container of the unknown kind 0x{descriptor:X2}";
                return false;
        }
    }

    // The next PDU: its command and body. False when what is left is too short or its length is nonsense.
    public static bool TryNext(ref ByteReader reader, out ushort command, out ReadOnlySpan<byte> body)
    {
        command = 0;
        body = default;
        if (reader.Remaining < HeaderLength)
        {
            return false;
        }

        reader.TryU16(out command);
        reader.TrySkip(2);
        reader.TryU32(out var length);
        return length >= HeaderLength && reader.TryBytes((long)length - HeaderLength, out body);
    }

    // CAPS_CONFIRM: the set the server picked.
    public static bool TryCapsConfirm(ReadOnlySpan<byte> body, out uint version, out uint flags)
    {
        version = 0;
        flags = 0;
        var reader = new ByteReader(body);
        if (!reader.TryU32(out version) || !reader.TryU32(out var length) || !reader.TryBytes(length, out var data))
        {
            return false;
        }

        // Every set up to version 10.7 starts with the 32 flags (the sets of 10.1 hold only reserved bytes).
        if (data.Length >= 4)
        {
            flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
        }

        return true;
    }

    // Whether the set the server chose carries H.264: version 8.1 says so in a flag, from version 10 on it is
    // there unless a flag switches it off. Version 8 and older have no H.264 at all.
    public static bool AvcEnabled(uint version, uint flags) => version switch
    {
        CapsVersion81 => (flags & CapsFlagAvc420Enabled) != 0,
        >= 0x000A0002 and <= 0x000A07FF => (flags & CapsFlagAvcDisabled) == 0,
        _ => false,
    };

    // "8.1", "10.2": the name the specification gives a version number.
    public static string VersionName(uint version) =>
        (version >> 16) == 8 ? ((version & 0xFFFF) == 0x0105 ? "8.1" : "8.0") : $"{version >> 16}.{(version & 0xFFFF) >> 8}";

    // RESET_GRAPHICS: the size of the desktop (the monitors that follow are of no use to a client of one picture).
    public static bool TryResetGraphics(ReadOnlySpan<byte> body, out int width, out int height)
    {
        width = 0;
        height = 0;
        var reader = new ByteReader(body);
        if (!reader.TryU32(out var w) || !reader.TryU32(out var h) || w is 0 or > 32768 || h is 0 or > 32768)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    public static bool TryCreateSurface(ReadOnlySpan<byte> body, out ushort id, out int width, out int height)
    {
        width = 0;
        height = 0;
        var reader = new ByteReader(body);
        if (!reader.TryU16(out id) || !reader.TryU16(out var w) || !reader.TryU16(out var h) || w == 0 || h == 0)
        {
            return false;
        }

        width = w;
        height = h;
        return true;
    }

    public static bool TryDeleteSurface(ReadOnlySpan<byte> body, out ushort id) => new ByteReader(body).TryU16(out id);

    // MAP_SURFACE_TO_OUTPUT: where a surface sits on the desktop.
    public static bool TryMapSurface(ReadOnlySpan<byte> body, out ushort id, out uint x, out uint y)
    {
        x = 0;
        y = 0;
        var reader = new ByteReader(body);
        return reader.TryU16(out id) && reader.TrySkip(2) && reader.TryU32(out x) && reader.TryU32(out y);
    }

    public static bool TryStartFrame(ReadOnlySpan<byte> body, out uint frameId)
    {
        frameId = 0;
        var reader = new ByteReader(body);
        return reader.TrySkip(4) && reader.TryU32(out frameId);
    }

    public static bool TryEndFrame(ReadOnlySpan<byte> body, out uint frameId) => new ByteReader(body).TryU32(out frameId);

    // WIRE_TO_SURFACE_1: a picture for a rectangle of a surface, in the format the codec id names. The data is
    // only a view into the body.
    public static bool TryWireToSurface1(ReadOnlySpan<byte> body, out ushort surface, out ushort codec, out ReadOnlySpan<byte> data)
    {
        codec = 0;
        data = default;
        var reader = new ByteReader(body);
        // Surface, codec, pixel format and the destination rectangle (four 16-bit values), then the data.
        return reader.TryU16(out surface) && reader.TryU16(out codec) && reader.TrySkip(1 + 8)
            && reader.TryU32(out var length) && reader.TryBytes(length, out data);
    }

    // RFX_AVC420_BITMAP_STREAM (2.2.4.4): the rectangles that count, their quantisation values, then the H.264
    // bitstream in Annex B form. Only the bitstream is of use here: it is the picture of the whole surface, and the
    // rectangles say which parts of it the server changed.
    public static bool TryAvc420(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> bitstream)
    {
        bitstream = default;
        var reader = new ByteReader(data);
        if (!reader.TryU32(out var rectangles) || rectangles > 65536 || !reader.TrySkip(rectangles * 10L))
        {
            return false;
        }

        bitstream = reader.Rest;
        return bitstream.Length > 4;
    }

    // Whether an Annex B stream holds the picture of an IDR NAL unit (type 5): the one a decoder can begin on.
    public static bool HasIdr(ReadOnlySpan<byte> annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 && (annexB[i + 3] & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }
}
