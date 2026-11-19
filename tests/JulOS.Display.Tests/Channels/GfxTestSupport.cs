using System.Buffers.Binary;

namespace JulOS.Integrations.Display.Rdp.Channels.Tests;

// A server of the graphics pipeline (MS-RDPEGFX) that says what a test tells it to. The messages are laid out by hand
// from the specification's tables (2.2.1 for the header, 2.2.2 for the commands), so the channel is not checked against its
// own encoder.
internal static class GfxServer
{
    public static byte[] U16(int value) => [(byte)value, (byte)(value >> 8)];

    public static byte[] U32(uint value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    // One PDU: command, flags 0, the length including these 8 bytes, the body.
    public static byte[] Pdu(ushort command, params byte[][] parts)
    {
        var body = parts.SelectMany(part => part).ToArray();
        return [.. U16(command), .. U16(0), .. U32((uint)(8 + body.Length)), .. body];
    }

    // What the server puts on the dynamic channel: one uncompressed segment around PDUs.
    public static byte[] Container(params byte[][] pdus) => [0xE0, 0x04, .. pdus.SelectMany(pdu => pdu)];

    public static byte[] CapsConfirm(uint version, uint flags) =>
        Pdu(0x0013, U32(version), U32(4), U32(flags));

    public static byte[] ResetGraphics(int width, int height) =>
        // Width, height, one monitor (left, top, right, bottom, flags), padding up to the 340 bytes of the PDU.
        Pdu(0x000E, [.. U32((uint)width), .. U32((uint)height), .. U32(1), .. U32(0), .. U32(0), .. U32((uint)width - 1), .. U32((uint)height - 1), .. U32(1), .. new byte[340 - 8 - 12 - 20]]);

    public static byte[] CreateSurface(int id, int width, int height) =>
        Pdu(0x0009, U16(id), U16(width), U16(height), [0x20]);

    public static byte[] DeleteSurface(int id) => Pdu(0x000A, U16(id));

    public static byte[] Map(int id, uint x = 0, uint y = 0) =>
        Pdu(0x000F, U16(id), U16(0), U32(x), U32(y));

    public static byte[] StartFrame(uint frame) => Pdu(0x000B, U32(0), U32(frame));

    public static byte[] EndFrame(uint frame) => Pdu(0x000C, U32(frame));

    // WIRE_TO_SURFACE_1: surface, codec, pixel format, the rectangle of the surface, the data.
    public static byte[] Wire(int surface, ushort codec, byte[] data, int width = 1280, int height = 720) =>
        Pdu(0x0001, U16(surface), U16(codec), [0x20], U16(0), U16(0), U16(width), U16(height), U32((uint)data.Length), data);

    // RFX_AVC420_BITMAP_STREAM: the rectangles (8 bytes each), the quantisation values (2 each), then the bitstream.
    public static byte[] Avc420(byte[] bitstream, int rectangles = 1) =>
    [
        .. U32((uint)rectangles),
        .. Enumerable.Range(0, rectangles).SelectMany(_ => (byte[])[.. U16(0), .. U16(0), .. U16(1280), .. U16(720)]),
        .. Enumerable.Range(0, rectangles).SelectMany(_ => (byte[])[22, 100]),
        .. bitstream,
    ];

    // A key frame (SPS, PPS, IDR slice) and a delta frame, in Annex B: not decodable, but they have the NAL units of the real thing.
    public static readonly byte[] KeyBitstream =
        [0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28, 0xAC, 0xD9, 0x40, 0x50, 0x05, 0xBB, 0x01, 0x10, 0, 0, 0, 1, 0x68, 0xEB, 0xE3, 0xCB, 0x22, 0xC0, 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x33, 0xFF];

    public static readonly byte[] DeltaBitstream = [0, 0, 0, 1, 0x41, 0x9A, 0x24, 0x6C, 0x41, 0xFF, 0xFE, 0x12];

    // The way a conversation starts: capabilities, reset, one surface of the desktop's size, mapped to the origin.
    public static byte[][] Start(int width = 1280, int height = 720) =>
    [
        Container(CapsConfirm(0x00080105, 0x10)),
        Container(ResetGraphics(width, height), CreateSurface(0, width, height), Map(0)),
    ];

    public static byte[] AvcFrame(uint frame, byte[] bitstream) =>
        Container(StartFrame(frame), Wire(0, 0x000B, Avc420(bitstream)), EndFrame(frame));

    public static uint Read32(byte[] message, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(offset));
}
