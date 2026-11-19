using JulOS.Integrations.Display.Vnc;

namespace JulOS.Integrations.Display.Tests.Vnc;

/// <summary>Keys as X11 keysyms: characters, the German layout by scancode, the keys without a character.</summary>
public sealed class VncKeysTests
{
    [Theory]
    [InlineData('a', 0x61u)]
    [InlineData('Z', 0x5Au)]
    [InlineData('~', 0x7Eu)]
    [InlineData(' ', 0x20u)]
    [InlineData('ä', 0xE4u)]                  // Latin-1 is its own value
    [InlineData('ß', 0xDFu)]
    [InlineData('€', 0x010020ACu)]            // everything else: 0x01000000 + code point
    [InlineData('Ω', 0x010003A9u)]
    [InlineData('\n', 0xFF0Du)]               // Return
    [InlineData('\r', 0xFF0Du)]
    [InlineData('\t', 0xFF09u)]
    [InlineData('\b', 0xFF08u)]
    [InlineData('\u0001', 0u)]                // other controls have no key
    [InlineData('\u0085', 0u)]
    public void A_character_is_its_keysym(char c, uint expected) => Assert.Equal(expected, VncKeys.FromCodePoint(c));

    [Fact]
    public void A_character_beyond_the_basic_plane_is_a_keysym_too()
    {
        Assert.Equal(0x0101F600u, VncKeys.FromCodePoint(0x1F600));
        Assert.Equal(0u, VncKeys.FromCodePoint(0xD800));              // a lone surrogate is nothing
        Assert.Equal(0u, VncKeys.FromCodePoint(0x110000));
    }

    [Theory]
    [InlineData(0x15, false, false, false, 'z')]          // the German layout has z where the US one has y
    [InlineData(0x15, false, true, false, 'Z')]
    [InlineData(0x2C, false, false, false, 'y')]
    [InlineData(0x1A, false, false, false, 'ü')]
    [InlineData(0x1A, false, true, false, 'Ü')]
    [InlineData(0x0C, false, false, false, 'ß')]
    [InlineData(0x0C, false, true, false, '?')]
    [InlineData(0x0C, false, false, true, '\\')]
    [InlineData(0x10, false, false, true, '@')]
    [InlineData(0x12, false, false, true, '€')]
    [InlineData(0x08, false, false, true, '{')]
    [InlineData(0x03, false, true, false, '"')]
    [InlineData(0x56, false, false, true, '|')]
    [InlineData(0x35, false, true, false, '_')]
    [InlineData(0x39, false, false, false, ' ')]
    [InlineData(0x02, false, false, false, '1')]
    public void The_german_layout_decides_what_a_key_types(int scancode, bool extended, bool shift, bool altGr, char expected)
    {
        Assert.Equal(VncKeys.FromCodePoint(expected), VncKeys.FromScancode(scancode, extended, shift, altGr));
    }

    [Theory]
    [InlineData(0x01, false, 0xFF1Bu)]       // Escape
    [InlineData(0x0E, false, 0xFF08u)]       // BackSpace
    [InlineData(0x1C, false, 0xFF0Du)]       // Return
    [InlineData(0x1C, true, 0xFF8Du)]        // keypad Enter
    [InlineData(0x3B, false, 0xFFBEu)]       // F1
    [InlineData(0x44, false, 0xFFC7u)]       // F10
    [InlineData(0x57, false, 0xFFC8u)]       // F11
    [InlineData(0x58, false, 0xFFC9u)]       // F12
    [InlineData(0x48, true, 0xFF52u)]        // Up
    [InlineData(0x53, true, 0xFFFFu)]        // Delete
    [InlineData(0x1D, false, 0xFFE3u)]       // Control_L
    [InlineData(0x1D, true, 0xFFE4u)]        // Control_R
    [InlineData(0x2A, false, 0xFFE1u)]       // Shift_L
    [InlineData(0x38, false, 0xFFE9u)]       // Alt_L
    [InlineData(0x38, true, 0xFE03u)]        // AltGr
    [InlineData(0x5B, true, 0xFFEBu)]        // Super_L
    [InlineData(0x47, false, 0xFFB7u)]       // keypad 7
    public void Keys_without_a_character_have_a_keysym_of_their_own(int scancode, bool extended, uint expected)
    {
        Assert.Equal(expected, VncKeys.FromScancode(scancode, extended, false, false));
    }

    [Fact]
    public void An_unknown_key_is_nothing()
    {
        Assert.Equal(0u, VncKeys.FromScancode(0x7F, false, false, false));
        Assert.Equal(0u, VncKeys.FromScancode(0x7F, true, false, false));
    }
}
