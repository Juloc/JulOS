namespace JulOS.Integrations.Display.Vnc;

// The client's own copy of the picture while a rectangle is decoded (no lock needed: only the read loop touches it). Finished rectangles are
// copied to the framebuffer the session encodes from. BGRX, four bytes a pixel, top row first.
internal sealed class RfbCanvas(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Pixels { get; } = new byte[checked(width * height * 4)];

    public int Stride => Width * 4;

    // Throws when a rectangle sticks out of the picture: a server that does that is broken, and writing there would corrupt memory.
    public void Check(int x, int y, int w, int h)
    {
        if (x < 0 || y < 0 || w < 0 || h < 0 || x + w > Width || y + h > Height)
        {
            throw new VncProtocolException($"a rectangle {w} x {h} at {x},{y} outside the {Width} x {Height} screen");
        }
    }

    public void Fill(int x, int y, int w, int h, byte b, byte g, byte r)
    {
        for (var row = 0; row < h; row++)
        {
            var at = (y + row) * Stride + x * 4;
            for (var column = 0; column < w; column++, at += 4)
            {
                Pixels[at] = b;
                Pixels[at + 1] = g;
                Pixels[at + 2] = r;
                Pixels[at + 3] = 0xFF;
            }
        }
    }
}
