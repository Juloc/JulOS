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
    public void A_copy_without_anything_usable_and_a_refused_or_empty_answer_tell_the_browser_nothing()
    {
        var (channel, wire, _, remote) = Started();

        channel.Receive(ClipServer.LongFormatList((3, null), (16, null)));     // nichts, was wir weitergeben
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
        Assert.Equal("Hallo\nWelt ü\0", Encoding.Unicode.GetString(data));          // LF: ein Linux-Desktop hinter xrdp bekommt sonst ein CR in jede Zeile
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

    private static (ClipboardChannel Channel, Wire Wire, ClipboardBridge Bridge, List<string> Remote, List<(ClipNote Note, int Count)> Notes) StartedWith(ClipboardOptions? options = null)
    {
        var channel = new ClipboardChannel();
        var wire = new Wire(channel);
        var remote = new List<string>();
        var notes = new List<(ClipNote, int)>();
        var bridge = new ClipboardBridge(channel, new ClipboardSink { Text = remote.Add, Note = (n, c) => notes.Add((n, c)) }, options: options);
        channel.Receive(ClipServer.Capabilities(0x3E));
        channel.Receive(ClipServer.MonitorReady());
        channel.Receive(ClipServer.FormatListResponse(true));
        wire.Take();
        notes.Clear();
        return (channel, wire, bridge, remote, notes);
    }

    private static string Pasted(ClipboardChannel channel, Wire wire)
    {
        channel.Receive(ClipServer.FormatListResponse(true));
        channel.Receive(ClipServer.DataRequest(Text));
        var (_, _, data) = ClipServer.Parse(wire.One());
        return Encoding.Unicode.GetString(data);
    }

    [Fact]
    public void Lines_go_to_the_vm_with_a_lone_line_feed_until_the_vm_itself_sends_carriage_returns()
    {
        var (channel, wire, bridge, remote, _) = StartedWith();

        bridge.SetLocalText("a\r\nb\nc");                                        // auch ein Windows-Browser liefert CR LF
        wire.Take();
        Assert.Equal("a\r\nb\nc\0", Pasted(channel, wire));                        // eine vorhandene CR bleibt, es kommt keine dazu

        // Ein Windows-Rechner kopiert Text mit CR LF: ab jetzt will er es auch so
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("x\r\ny\r\nz\0")));
        Assert.Equal(["x\ny\nz"], remote);
        bridge.SetLocalText("p\nq");
        wire.Take();
        Assert.Equal("p\r\nq\0", Pasted(channel, wire));

        // und kopiert er Text mit einzelnem LF (xrdp), geht es wieder mit LF
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("1\n2\0")));
        bridge.SetLocalText("p\nq");
        wire.Take();
        Assert.Equal("p\nq\0", Pasted(channel, wire));
    }

    [Theory]
    [InlineData("lf", "u\nv", "u\nv\0")]
    [InlineData("crlf", "u\nv", "u\r\nv\0")]
    [InlineData("auto", "u\nv", "u\nv\0")]
    [InlineData("nonsense", "u\nv", "u\nv\0")]
    public void The_setting_for_line_ends_wins_over_what_the_vm_showed(string setting, string text, string expected)
    {
        var (channel, wire, bridge, _, _) = StartedWith(ClipboardOptions.Parse(null, null, setting));
        if (setting is "lf")
        {
            channel.Receive(ClipServer.LongFormatList((Text, null)));
            wire.Take();
            channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("1\r\n2\0")));      // zeigt CR LF, soll aber nichts aendern
        }

        bridge.SetLocalText(text);
        wire.Take();
        Assert.Equal(expected, Pasted(channel, wire));
    }

    [Fact]
    public void The_same_text_copied_in_the_vm_after_a_paste_from_the_browser_reaches_the_browser()
    {
        var (channel, wire, bridge, remote, _) = StartedWith();

        bridge.SetLocalText("A");
        wire.Take();
        channel.Receive(ClipServer.FormatListResponse(true));
        // in der VM wird erst etwas anderes kopiert, dann wieder "A": beides gehoert dem Browser, auch das "A" (kein Echo mehr)
        foreach (var copied in new[] { "B", "A" })
        {
            channel.Receive(ClipServer.LongFormatList((Text, null)));
            wire.Take();
            channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes(copied + "\0")));
        }

        Assert.Equal(["B", "A"], remote);
    }

    [Fact]
    public async Task Text_that_the_vm_reports_back_long_after_the_paste_is_a_new_copy()
    {
        var (channel, wire, bridge, remote, _) = StartedWith();

        bridge.SetLocalText("Echo");
        wire.Take();
        await Task.Delay(1700);                                                 // das Echo-Fenster ist vorbei
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("Echo\0")));
        Assert.Equal(["Echo"], remote);
    }

    [Fact]
    public void The_bridge_reports_what_happened_to_the_text_without_ever_naming_it()
    {
        var (channel, wire, bridge, _, notes) = StartedWith();

        bridge.SetLocalText(new string('a', ClipboardBridge.MaxTextBytes + 1));
        Assert.Equal([(ClipNote.TextTooLong, ClipboardBridge.MaxTextBytes + 1)], notes);
        notes.Clear();

        bridge.SetLocalText("Geheim äö");
        wire.Take();
        Pasted(channel, wire);
        Assert.Equal([(ClipNote.TextFetchedByVm, 9)], notes);                     // Zeichen, nicht Bytes
        notes.Clear();

        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(null));
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("\0")));
        channel.Receive(ClipServer.LongFormatList((Text, null)));
        wire.Take();
        channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("zehn Zeichen")));
        Assert.Equal([(ClipNote.TextFromVmLost, 0), (ClipNote.TextFromVmLost, 0), (ClipNote.TextFromVm, 12)], notes);
        Assert.DoesNotContain(notes, n => n.Count > 100);
    }

    [Fact]
    public void Text_before_the_channel_is_ready_is_reported_as_waiting_and_the_channel_reports_when_it_is_ready()
    {
        var channel = new ClipboardChannel();
        var wire = new Wire(channel);
        var notes = new List<(ClipNote, int)>();
        var bridge = new ClipboardBridge(channel, new ClipboardSink { Note = (n, c) => notes.Add((n, c)) });
        bridge.SetLocalText("frueh");
        Assert.Equal([(ClipNote.TextWaiting, 5)], notes);

        channel.Receive(ClipServer.Capabilities(0x3E));
        channel.Receive(ClipServer.MonitorReady());
        Assert.Equal([(ClipNote.TextWaiting, 5), (ClipNote.Status, 0)], notes);
        channel.Receive(ClipServer.FormatListResponse(false));
        Assert.Equal((ClipNote.ListRefused, 0), notes[^1]);
    }
}
