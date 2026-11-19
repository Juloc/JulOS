using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Rdp.Channels;
using JulOS.Integrations.Display.Rdp.Channels.Tests;
using JulOS.Integrations.Display.Video;

namespace JulOS.Integrations.Display.Tests;

/// <summary>
/// Der Durchreichepfad von <see cref="ScreenStream"/> ohne Server: der Kanal wird mit den Nachrichten eines Servers gefuettert (von Hand nach der
/// Spezifikation gebaut), der Strom muss dem Browser die H.264-Bilder in der richtigen Reihenfolge geben und dem Server erst antworten,
/// wenn der Browser bestaetigt hat.
/// </summary>
public sealed class GfxPassthroughTests
{
    private static (ScreenStream Stream, GfxChannel Gfx, List<byte[]> ToServer, CancellationTokenSource Session) Attached()
    {
        var stream = new ScreenStream(new RdpOptions { Host = "rdp.invalid" }, graphicsPipeline: true);
        var gfx = new GfxChannel();
        var toServer = new List<byte[]>();
        gfx.Send = message => toServer.Add(message.ToArray());
        var session = new CancellationTokenSource();
        stream.AttachGfx(gfx, session);
        gfx.Opened();
        toServer.Clear();
        foreach (var message in GfxServer.Start())
        {
            gfx.Receive(message);
        }

        return (stream, gfx, toServer, session);
    }

    private static List<ScreenEvent> Drain(ScreenStream stream)
    {
        var events = new List<ScreenEvent>();
        while (stream.Events.TryRead(out var evt))
        {
            events.Add(evt);
        }

        return events;
    }

    [Fact]
    public async Task A_key_frame_switches_to_the_servers_pictures_and_the_acknowledgement_follows_the_browser()
    {
        var (stream, gfx, toServer, session) = Attached();
        await using var _ = stream;

        gfx.Receive(GfxServer.AvcFrame(10, GfxServer.KeyBitstream));
        gfx.Receive(GfxServer.AvcFrame(11, GfxServer.DeltaBitstream));

        var events = Drain(stream);
        // Everything the own encoder and the refinement had on the way is void, the size is given, then the pictures.
        Assert.IsType<ScreenRefineClear>(events[0]);
        Assert.Equal(new ScreenSize(1280, 720), events[1]);
        var info = Assert.IsType<ScreenEncoderInfo>(events[2]);
        Assert.Contains("RDPGFX", info.Name);
        Assert.Equal(VideoCodec.H264, info.Codec);
        var key = Assert.IsType<ScreenPicture>(events[3]);
        var delta = Assert.IsType<ScreenPicture>(events[4]);
        Assert.True(key.IsKey);
        Assert.False(delta.IsKey);
        Assert.Equal(GfxServer.KeyBitstream, key.Data);
        Assert.Equal(key.Sequence + 1, delta.Sequence);
        Assert.Equal((true, null), stream.GfxState);

        // The server hears nothing until the browser says it has drawn the picture
        Assert.Empty(toServer);
        stream.Acknowledge(key.Sequence);
        Assert.Equal(10u, GfxServer.Read32(Assert.Single(toServer), 12));
        toServer.Clear();
        stream.Acknowledge(delta.Sequence);
        Assert.Equal(11u, GfxServer.Read32(Assert.Single(toServer), 12));
        Assert.False(session.IsCancellationRequested);
    }

    [Fact]
    public async Task A_cumulative_acknowledgement_answers_every_frame_up_to_it()
    {
        var (stream, gfx, toServer, _) = Attached();
        await using var _ = stream;
        gfx.Receive(GfxServer.AvcFrame(1, GfxServer.KeyBitstream));
        gfx.Receive(GfxServer.AvcFrame(2, GfxServer.DeltaBitstream));
        gfx.Receive(GfxServer.AvcFrame(3, GfxServer.DeltaBitstream));
        var pictures = Drain(stream).OfType<ScreenPicture>().ToList();

        stream.Acknowledge(pictures[1].Sequence);

        Assert.Equal([1u, 2u], toServer.Select(message => GfxServer.Read32(message, 12)).ToArray());
        stream.Acknowledge(pictures[2].Sequence);
        Assert.Equal(3u, GfxServer.Read32(toServer[^1], 12));
        // An old or repeated acknowledgement answers nothing again
        stream.Acknowledge(pictures[0].Sequence);
        Assert.Equal(3, toServer.Count);
    }

    [Fact]
    public async Task Pictures_before_the_first_key_frame_are_not_passed_on_but_the_server_is_answered()
    {
        var (stream, gfx, toServer, _) = Attached();
        await using var _ = stream;

        gfx.Receive(GfxServer.AvcFrame(1, GfxServer.DeltaBitstream));

        Assert.Empty(Drain(stream));
        Assert.Equal(1u, GfxServer.Read32(Assert.Single(toServer), 12));
        Assert.False(stream.GfxState.Passthrough);

        gfx.Receive(GfxServer.AvcFrame(2, GfxServer.KeyBitstream));
        Assert.Contains(Drain(stream), evt => evt is ScreenPicture { IsKey: true });
        Assert.True(stream.GfxState.Passthrough);
    }

    [Fact]
    public async Task A_new_desktop_size_is_announced_and_waits_for_its_key_frame()
    {
        var (stream, gfx, toServer, _) = Attached();
        await using var _ = stream;
        gfx.Receive(GfxServer.AvcFrame(1, GfxServer.KeyBitstream));
        Drain(stream);
        toServer.Clear();

        gfx.Receive(GfxServer.Container(GfxServer.ResetGraphics(1920, 1080), GfxServer.CreateSurface(1, 1920, 1080), GfxServer.Map(1)));
        // A delta picture still on the way from before is of no use to the browser: its decoder needs the new key frame first
        gfx.Receive(GfxServer.Container(GfxServer.StartFrame(2), GfxServer.Wire(1, 0x000B, GfxServer.Avc420(GfxServer.DeltaBitstream)), GfxServer.EndFrame(2)));
        gfx.Receive(GfxServer.Container(GfxServer.StartFrame(3), GfxServer.Wire(1, 0x000B, GfxServer.Avc420(GfxServer.KeyBitstream)), GfxServer.EndFrame(3)));

        var events = Drain(stream);
        Assert.Equal(new ScreenSize(1920, 1080), events[0]);
        var picture = Assert.IsType<ScreenPicture>(Assert.Single(events, evt => evt is ScreenPicture));
        Assert.True(picture.IsKey);
        Assert.Equal(2u, GfxServer.Read32(toServer[0], 12));    // the dropped delta was answered at once
    }

    [Fact]
    public async Task A_key_frame_request_cannot_be_served_by_the_server_and_ends_the_pipeline_for_this_session()
    {
        var (stream, gfx, _, session) = Attached();
        await using var _ = stream;

        // Before the switch there is nothing to give up: the own encoder answers as ever
        stream.RequestKeyFrame();
        Assert.False(session.IsCancellationRequested);

        gfx.Receive(GfxServer.AvcFrame(1, GfxServer.KeyBitstream));
        stream.RequestKeyFrame();

        Assert.True(session.IsCancellationRequested);
        Assert.Contains("Schluesselbild", stream.GfxState.Left);
    }

    [Fact]
    public async Task A_browser_that_falls_far_behind_ends_the_pipeline_instead_of_letting_pictures_pile_up()
    {
        var (stream, gfx, _, session) = Attached();
        await using var _ = stream;

        gfx.Receive(GfxServer.AvcFrame(1, GfxServer.KeyBitstream));
        for (uint frame = 2; frame <= ScreenStream.MaxGfxPending; frame++)
        {
            gfx.Receive(GfxServer.AvcFrame(frame, GfxServer.DeltaBitstream));
        }

        Assert.False(session.IsCancellationRequested);
        gfx.Receive(GfxServer.AvcFrame(ScreenStream.MaxGfxPending + 1, GfxServer.DeltaBitstream));

        Assert.True(session.IsCancellationRequested);
        Assert.Contains("nicht nach", stream.GfxState.Left);
    }

    [Fact]
    public async Task A_server_that_cannot_be_passed_on_ends_the_pipeline_with_its_reason()
    {
        var (stream, gfx, toServer, session) = Attached();
        await using var _ = stream;

        // The planar codec, as the xrdp of Debian 13 sends it
        gfx.Receive(GfxServer.Container(GfxServer.StartFrame(1), GfxServer.Wire(0, 0x000A, [1, 2, 3, 4, 5, 6, 7, 8]), GfxServer.EndFrame(1)));

        Assert.True(session.IsCancellationRequested);
        Assert.Contains("0x000A", stream.GfxState.Left);
        Assert.False(stream.GfxState.Passthrough);
        Assert.Empty(Drain(stream));
        Assert.Empty(toServer);
    }

    [Fact]
    public void The_switch_is_off_unless_the_environment_says_so()
    {
        Assert.False(GfxSettings.IsOn(null));
        Assert.False(GfxSettings.IsOn(""));
        Assert.False(GfxSettings.IsOn("0"));
        Assert.False(GfxSettings.IsOn("off"));
        Assert.False(GfxSettings.IsOn("maybe"));
        Assert.True(GfxSettings.IsOn("1"));
        Assert.True(GfxSettings.IsOn(" ON "));
        Assert.True(GfxSettings.IsOn("True"));
        Assert.True(GfxSettings.IsOn("yes"));
    }

    [Fact]
    public void A_server_that_gave_nothing_is_left_alone_for_a_while()
    {
        GfxSettings.Forget();
        try
        {
            Assert.False(GfxSettings.IsAvoided("rdp.invalid", 3389));
            GfxSettings.Avoid("RDP.invalid", 3389);
            Assert.True(GfxSettings.IsAvoided("rdp.invalid", 3389));
            Assert.False(GfxSettings.IsAvoided("rdp.invalid", 3390));
            Assert.False(GfxSettings.IsAvoided("other.invalid", 3389));
        }
        finally
        {
            GfxSettings.Forget();
        }
    }
}
