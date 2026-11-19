using JulOS.Integrations.Display;
using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Maus zum RDP-Server: alle fuenf Tasten und das Rad, auch waagerecht und in feinen Schritten.</summary>
public sealed class MouseInputTests
{
    [Theory]
    [InlineData(0, true, 1, 0x9000)]
    [InlineData(0, false, 1, 0x1000)]
    [InlineData(1, true, 1, 0xC000)]
    [InlineData(2, true, 1, 0xA000)]
    [InlineData(3, true, 2, 0x8001)]      // back: XBUTTON1, an extended event
    [InlineData(3, false, 2, 0x0001)]
    [InlineData(4, true, 2, 0x8002)]      // forward: XBUTTON2
    [InlineData(4, false, 2, 0x0002)]
    public void Each_button_has_its_own_pointer_flags(int button, bool down, int code, int flags)
    {
        Assert.Equal((code, flags), RdpClient.ButtonEvent(button, down));
    }

    [Theory]
    [InlineData(120, false, new[] { 0x278 })]          // one notch up
    [InlineData(-120, false, new[] { 0x388 })]         // one notch down (negative flag, two's complement in the low byte)
    [InlineData(1, false, new[] { 0x201 })]            // fine steps pass as they are
    [InlineData(-1, false, new[] { 0x3FF })]
    [InlineData(300, false, new[] { 0x2FF, 0x22D })]   // more than one message can carry is split
    [InlineData(120, true, new[] { 0x478 })]           // horizontal: right
    [InlineData(-120, true, new[] { 0x588 })]          // horizontal: left
    [InlineData(0, false, new int[0])]
    public void The_wheel_keeps_fine_steps_and_both_directions(int delta, bool horizontal, int[] flags)
    {
        Assert.Equal(flags, RdpClient.WheelFlags(delta, horizontal).ToArray());
    }

    [Fact]
    public void The_relay_lets_all_five_buttons_through_and_nothing_else()
    {
        for (byte b = 0; b <= 4; b++) Assert.True(ScreenRelay.IsMouseButton(b));
        Assert.False(ScreenRelay.IsMouseButton(5));
        Assert.False(ScreenRelay.IsMouseButton(255));
    }
}
