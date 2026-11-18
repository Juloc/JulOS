using System.Text;
using JulOS.Integrations.Display.Rdp.Channels;
using JulOS.Integrations.Display.Rdp.Channels.Tests;

namespace JulOS.Integrations.Display.Tests;

/// <summary>Die Zwischenablage-Bruecke gegen einen Server, der genau das sagt, was der Test vorgibt (Hilfsklassen der Kanal-Tests).</summary>
public sealed class ClipboardBridgeTests
{
    private const uint Text = 13;

    private static (ClipboardChannel Channel, Wire Wire, ClipboardBridge Bridge, List<string> Remote) Started()
    {
        var channel = new ClipboardChannel();
        var wire = new Wire(channel);
        var remote = new List<string>();
        var bridge = new ClipboardBridge(channel, remote.Add);
        channel.Receive(ClipServer.Capabilities(0x3E));
        channel.Receive(ClipServer.MonitorReady());
        channel.Receive(ClipServer.FormatListResponse(true));
        wire.Take();
        return (channel, wire, bridge, remote);
    }

    [Fact]
    public void Text_copied_on_the_remote_desktop_goes_to_the_browser()
    {
        var (channel, wire, _, remote) = Started();

        channel.Receive(ClipServer.LongFormatList((Text, null), (16, null), (1, null)));
        var sent = wire.Take();
        Assert.Equal(ClipServer.FormatListResponseType, ClipServer.Parse(sent[0]).Type);
        var request = ClipServer.Parse(sent[1]);                            // sofort geholt, ohne dass jemand einfuegt
        Assert.Equal(ClipServer.DataRequestType, request.Type);

        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("Grüße\r\nzweite Zeile €\0")));
        Assert.Equal(["Grüße\nzweite Zeile €"], remote);
    }

    [Fact]
    public void A_copy_without_text_and_a_refused_or_empty_answer_tell_the_browser_nothing()
    {
        var (channel, wire, _, remote) = Started();

        channel.Receive(ClipServer.LongFormatList((8, null), (2, null)));      // nur ein Bild
        Assert.Single(wire.Take());                                            // nur die Bestaetigung, keine Anfrage
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(null));                       // verweigert
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("\0")));   // leer
        Assert.Empty(remote);
    }

    [Fact]
    public void Text_from_the_browser_is_announced_and_handed_over_when_the_server_pastes()
    {
        var (channel, wire, bridge, _) = Started();

        bridge.SetLocalText("Hallo\nWelt ü");
        var (type, _, body) = ClipServer.Parse(wire.One());
        Assert.Equal(ClipServer.FormatListType, type);
        Hex.Same("0d 00 00 00 00 00", body);
        channel.Receive(ClipServer.FormatListResponse(true));

        channel.Receive(ClipServer.DataRequest(Text));
        var (answer, flags, data) = ClipServer.Parse(wire.One());
        Assert.Equal((ClipServer.DataResponseType, ClipServer.Ok), (answer, flags));
        Assert.Equal("Hallo\r\nWelt ü\0", Encoding.Unicode.GetString(data));
    }

    [Fact]
    public void Other_formats_are_refused_and_so_is_everything_before_the_browser_gave_text()
    {
        var (channel, wire, bridge, _) = Started();

        channel.Receive(ClipServer.DataRequest(Text));                          // der Browser hat noch nichts abgelegt
        Assert.Equal(ClipServer.Fail, ClipServer.Parse(wire.One()).Flags);

        bridge.SetLocalText("x");
        wire.Take();
        channel.Receive(ClipServer.FormatListResponse(true));
        channel.Receive(ClipServer.DataRequest(8));                             // ein Bild gibt es nicht
        Assert.Equal(ClipServer.Fail, ClipServer.Parse(wire.One()).Flags);
    }

    [Fact]
    public void Our_own_text_coming_back_as_a_new_copy_is_not_forwarded_and_too_long_text_is_dropped()
    {
        var (channel, wire, bridge, remote) = Started();

        bridge.SetLocalText("Echo");
        wire.Take();
        channel.Receive(ClipServer.FormatListResponse(true));
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("Echo\0")));
        Assert.Empty(remote);

        bridge.SetLocalText(new string('a', ClipboardBridge.MaxTextBytes + 1));
        wire.Nothing();                                                         // nichts angekuendigt: der Text war zu lang
    }

    [Fact]
    public void Text_announced_before_the_server_is_ready_is_sent_when_it_is()
    {
        var channel = new ClipboardChannel();
        var wire = new Wire(channel);
        var bridge = new ClipboardBridge(channel, _ => { });
        bridge.SetLocalText("frueh");
        wire.Nothing();
        channel.Receive(ClipServer.Capabilities(0x3E));
        channel.Receive(ClipServer.MonitorReady());
        var sent = wire.Take();
        Assert.Contains(sent, m => ClipServer.Parse(m).Type == ClipServer.FormatListType && ClipServer.Parse(m).Body.Length > 0);
    }
}
