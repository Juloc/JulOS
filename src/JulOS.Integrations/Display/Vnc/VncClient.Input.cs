using System.Buffers.Binary;

namespace JulOS.Integrations.Display.Vnc;

// Pointer and keys, to the server. Each call is one small message in the queue; nothing here waits.
public sealed partial class VncClient
{
    private readonly Lock _inputGate = new();
    private int _pointerX;
    private int _pointerY;
    private int _buttons;
    private int _wheelRest;
    private bool _shift;
    private bool _altGr;
    private char _highSurrogate;

    // Pointer buttons as the protocol numbers them: 1 left, 2 middle, 3 right, 4 and 5 the wheel up and down, 6 and 7 left and right, 8 back.
    internal const int ButtonLeft = 1, ButtonMiddle = 2, ButtonRight = 4, WheelUp = 8, WheelDown = 16, WheelLeft = 32, WheelRight = 64, ButtonBack = 128;

    public void MouseMove(int x, int y)
    {
        lock (_inputGate)
        {
            _pointerX = Math.Clamp(x, 0, Math.Max(Width - 1, 0));
            _pointerY = Math.Clamp(y, 0, Math.Max(Height - 1, 0));
            SendPointer(_buttons);
        }
    }

    // Buttons as browsers number them: 0 left, 1 middle, 2 right, 3 back, 4 forward (which the protocol's single byte of buttons has no room for).
    public void MouseButton(int button, bool down)
    {
        var bit = button switch
        {
            0 => ButtonLeft,
            1 => ButtonMiddle,
            2 => ButtonRight,
            3 => ButtonBack,
            _ => 0,
        };
        if (bit == 0)
        {
            return;
        }

        lock (_inputGate)
        {
            _buttons = down ? _buttons | bit : _buttons & ~bit;
            SendPointer(_buttons);
        }
    }

    // The wheel is a pair of buttons that are pressed and released: one click for every 120 units (a notch), the rest is kept for the next turn.
    // Vertical: positive is away from the user (up). Horizontal: positive is to the right.
    public void MouseWheel(int delta, bool horizontal)
    {
        lock (_inputGate)
        {
            // Turning the other way drops what was left over
            if (_wheelRest != 0 && Math.Sign(_wheelRest) != Math.Sign(delta))
            {
                _wheelRest = 0;
            }

            _wheelRest += delta;
            var clicks = Math.Min(Math.Abs(_wheelRest) / 120, 20);
            if (clicks == 0)
            {
                return;
            }

            var up = _wheelRest > 0;
            var left = Math.Abs(_wheelRest) - clicks * 120;
            _wheelRest = left > 0 ? Math.Sign(_wheelRest) * left : 0;
            var bit = horizontal ? (up ? WheelRight : WheelLeft) : (up ? WheelUp : WheelDown);
            for (var i = 0; i < clicks; i++)
            {
                SendPointer(_buttons | bit);
                SendPointer(_buttons);
            }
        }
    }

    private void SendPointer(int mask)
    {
        var message = new byte[6];
        message[0] = 5;
        message[1] = (byte)mask;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)_pointerX);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)_pointerY);
        Send(message);
    }

    /// <summary>A key by its X11 keysym (what the browser sends: the character that was typed, whatever the layout).</summary>
    public void Keysym(uint keysym, bool down)
    {
        var message = new byte[8];
        message[0] = 4;
        message[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), keysym);
        Send(message);
    }

    // A key by its scancode (set 1, as the RDP client takes it): mapped with the German layout, Shift and AltGr from the keys pressed so far.
    public void Key(int scancode, bool extended, bool down)
    {
        uint keysym;
        lock (_inputGate)
        {
            if (!extended && scancode is 0x2A or 0x36)
            {
                _shift = down;
            }
            else if (extended && scancode == 0x38)
            {
                _altGr = down;
            }

            keysym = VncKeys.FromScancode(scancode, extended, _shift, _altGr);
        }

        if (keysym != 0)
        {
            Keysym(keysym, down);
        }
    }

    // A character that no key has to exist for: pressed and released. A pair of surrogates (an emoji) arrives as two units and is one key.
    public void Unicode(char unit)
    {
        int codePoint;
        lock (_inputGate)
        {
            if (char.IsHighSurrogate(unit))
            {
                _highSurrogate = unit;
                return;
            }

            if (char.IsLowSurrogate(unit))
            {
                if (_highSurrogate == 0)
                {
                    return;
                }

                codePoint = char.ConvertToUtf32(_highSurrogate, unit);
                _highSurrogate = '\0';
            }
            else
            {
                _highSurrogate = '\0';
                codePoint = unit;
            }
        }

        var keysym = VncKeys.FromCodePoint(codePoint);
        if (keysym != 0)
        {
            Keysym(keysym, true);
            Keysym(keysym, false);
        }
    }
}
