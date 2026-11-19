using System.Buffers.Binary;
using System.IO.Compression;
using JulOS.Integrations.Display.Rdp;
using JulOS.Integrations.Display.Vnc;
using static JulOS.Integrations.Display.Tests.Vnc.FakeVncServer;

namespace JulOS.Integrations.Display.Tests.Vnc;

/// <summary>The client against a small server in the test: handshake and versions, sign-in, pictures, pointer, size, clipboard.</summary>
public sealed class VncClientTests
{
    private sealed class Recorder : IScreenHandler
    {
        public readonly List<(int X, int Y, int W, int H)> Updates = [];
        public readonly List<(int W, int H)> Sizes = [];
        public readonly List<(int HotX, int HotY, int W, int H, byte[] Rgba)> Cursors = [];
        public int Hidden;
        public readonly Lock Gate = new();

        public void Updated(int x, int y, int width, int height)
        {
            lock (Gate) Updates.Add((x, y, width, height));
        }

        public void Resized(int width, int height)
        {
            lock (Gate) Sizes.Add((width, height));
        }

        public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
        {
            lock (Gate) Cursors.Add((hotX, hotY, width, height, rgba.ToArray()));
        }

        public void CursorHidden() => Interlocked.Increment(ref Hidden);

        public void CursorDefault()
        {
        }

        public async Task WaitAsync(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException();
                await Task.Delay(10);
            }
        }
    }

    private static VncOptions Options(FakeVncServer server, string password = "secret12", Func<string, bool>? check = null, string user = "") =>
        new() { Host = "127.0.0.1", Port = server.Port, Password = password, UserName = user, CertificateCheck = check, ConnectTimeout = TimeSpan.FromSeconds(30) };

    private static async Task<(VncClient Client, Recorder Handler, Task Run, CancellationTokenSource Cts)> StartAsync(FakeVncServer server, string password = "secret12")
    {
        var handler = new Recorder();
        var client = new VncClient(Options(server, password), handler);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(cts.Token);
        await server.Ready;
        return (client, handler, client.RunAsync(cts.Token), cts);
    }

    // ---- versions and security ----

    [Theory]
    [InlineData("RFB 003.008\n", 8)]
    [InlineData("RFB 003.007\n", 7)]
    [InlineData("RFB 003.003\n", 3)]
    [InlineData("RFB 003.005\n", 3)]        // 3.4 to 3.6 are 3.3 by the specification
    [InlineData("RFB 003.889\n", 8)]        // RealVNC's marker for its own protocol: ask for 3.8
    [InlineData("RFB 004.001\n", 8)]
    public void The_version_that_is_used_is_the_highest_both_know(string banner, int minor)
    {
        Assert.Equal(minor, VncClient.ParseVersion(System.Text.Encoding.ASCII.GetBytes(banner)));
    }

    [Theory]
    [InlineData("HTTP/1.1 400 \n")]
    [InlineData("RFB 3.8\n\0\0\0\0\0")]
    [InlineData("RFB 002.000\n")]
    public void Something_that_is_no_vnc_server_is_reported(string banner)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(banner.PadRight(12)[..12]);
        Assert.Throws<RdpException>(() => VncClient.ParseVersion(bytes));
    }

    [Theory]
    [InlineData("RFB 003.003\n", 3)]
    [InlineData("RFB 003.007\n", 7)]
    [InlineData("RFB 003.008\n", 8)]
    public async Task Every_version_connects_without_sign_in_and_gets_the_size(string banner, int minor)
    {
        using var server = new FakeVncServer { Banner = banner, Width = 100, Height = 60, Name = "den" };
        var (client, _, _, cts) = await StartAsync(server);
        await using var guard = client;
        Assert.Equal((100, 60), (client.Width, client.Height));
        Assert.Equal("den", client.DesktopName);
        Assert.Equal("3." + minor, client.ProtocolVersion);
        Assert.Equal(1, client.SecurityType);
        Assert.Equal(100 * 60 * 4, client.Framebuffer.Length);
        cts.Cancel();
    }

    [Fact]
    public async Task The_client_sets_its_pixel_format_asks_for_its_encodings_and_for_the_first_picture()
    {
        using var server = new FakeVncServer();
        var (client, _, _, cts) = await StartAsync(server);
        await using var guard = client;
        var format = await server.WaitForAsync(m => m[0] == 0);
        Assert.Equal(20, format.Length);
        Assert.Equal([32, 24, 0, 1], format[4..8]);                                // 32 bits, depth 24, little endian, true colour
        Assert.Equal([0, 255, 0, 255, 0, 255], format[8..14]);
        Assert.Equal([16, 8, 0], format[14..17]);                                  // blue in the lowest byte: BGRX in memory

        var encodings = await server.WaitForAsync(m => m[0] == 2);
        var list = Enumerable.Range(0, BinaryPrimitives.ReadUInt16BigEndian(encodings.AsSpan(2))).Select(i => BinaryPrimitives.ReadInt32BigEndian(encodings.AsSpan(4 + i * 4))).ToArray();
        Assert.Equal(16, list[0]);                                                 // ZRLE first
        Assert.Contains(5, list);                                                  // Hextile
        Assert.Contains(1, list);                                                  // CopyRect
        Assert.Contains(0, list);                                                  // Raw
        Assert.Contains(-239, list);                                               // cursor
        Assert.Contains(-308, list);                                               // ExtendedDesktopSize
        Assert.DoesNotContain(7, list);                                            // no Tight

        var request = await server.WaitForAsync(m => m[0] == 3);
        Assert.Equal(0, request[1]);                                               // the first one is not incremental
        Assert.Equal((0, 0, 64, 48), (BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4)), BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6)), BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8))));
        cts.Cancel();
    }

    [Fact]
    public async Task A_vnc_password_is_answered_with_the_des_of_the_challenge()
    {
        using var server = new FakeVncServer { Types = [2], Password = "geheim" };
        var (client, _, _, cts) = await StartAsync(server, "geheim");
        await using var guard = client;
        Assert.True(server.PasswordAccepted);
        Assert.Equal(2, client.SecurityType);
        cts.Cancel();
    }

    [Fact]
    public async Task Only_the_first_eight_characters_of_the_password_count()
    {
        using var server = new FakeVncServer { Types = [2], Password = "12345678" };
        var (client, _, _, cts) = await StartAsync(server, "12345678-and-more");
        await using var guard = client;
        Assert.True(server.PasswordAccepted);
        cts.Cancel();
    }

    [Theory]
    [InlineData("RFB 003.008\n")]
    [InlineData("RFB 003.007\n")]
    [InlineData("RFB 003.003\n")]
    public async Task A_wrong_password_is_a_logon_failure(string banner)
    {
        using var server = new FakeVncServer { Banner = banner, Types = [2], Password = "geheim" };
        await using var client = new VncClient(Options(server, "falsch"), new Recorder());
        var error = await Assert.ThrowsAsync<RdpException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.True(error.IsLogonFailure);
        Assert.False(error.IsCredentialsMissing);
        if (banner.EndsWith("08\n", StringComparison.Ordinal))
        {
            Assert.Contains("Authentication failed", error.Message);                // the server's own reason comes along in 3.8
        }
    }

    [Fact]
    public async Task A_server_that_wants_a_password_when_there_is_none_asks_for_one()
    {
        using var server = new FakeVncServer { Types = [2] };
        await using var client = new VncClient(Options(server, ""), new Recorder());
        var error = await Assert.ThrowsAsync<RdpException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.True(error.IsCredentialsMissing);
        Assert.True(error.IsLogonFailure);
    }

    [Fact]
    public async Task A_server_that_offers_only_unsupported_types_is_named_in_the_error()
    {
        using var server = new FakeVncServer { Types = [30, 129] };
        await using var client = new VncClient(Options(server), new Recorder());
        var error = await Assert.ThrowsAsync<RdpException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Contains("30, 129", error.Message);
        Assert.Contains("not support", error.Message);
    }

    [Fact]
    public async Task A_refusal_shows_the_servers_reason()
    {
        using var server = new FakeVncServer { Refuse = "Too many connections" };
        await using var client = new VncClient(Options(server), new Recorder());
        var error = await Assert.ThrowsAsync<RdpException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Contains("Too many connections", error.Message);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2 }, true, 2)]       // with a password the password sign-in wins
    [InlineData(new byte[] { 1, 2 }, false, 1)]      // without one: none
    [InlineData(new byte[] { 19, 2 }, true, 2)]
    [InlineData(new byte[] { 19, 1 }, true, 19)]
    [InlineData(new byte[] { 19 }, false, 19)]
    [InlineData(new byte[] { 5, 19, 1 }, true, 19)]
    public void The_security_type_is_chosen_by_what_the_user_gave(byte[] offered, bool hasPassword, int expected)
    {
        Assert.Equal(expected, VncClient.ChooseSecurity(offered, hasPassword));
    }

    // ---- VeNCrypt ----

    [Fact]
    public async Task VeNCrypt_with_a_certificate_runs_the_password_sign_in_over_tls_and_remembers_the_fingerprint()
    {
        using var certificate = NewCertificate();
        using var server = new FakeVncServer { Types = [19], SubTypes = [258, 261, 262], Certificate = certificate, Password = "geheim" };
        string? seen = null;
        var handler = new Recorder();
        await using var client = new VncClient(Options(server, "geheim", fingerprint => { seen = fingerprint; return true; }), handler);
        await client.ConnectAsync(CancellationToken.None);
        await server.Ready;
        Assert.Equal(261, client.SecuritySubType);                                  // 258 (TLS without a certificate) was left alone
        Assert.True(server.PasswordAccepted);
        Assert.Equal(JulOS.Integrations.Display.CertificateFingerprint.Of(certificate), seen);
    }

    [Fact]
    public async Task VeNCrypt_plain_sends_user_and_password_over_tls()
    {
        using var certificate = NewCertificate();
        using var server = new FakeVncServer { Types = [19], SubTypes = [262], Certificate = certificate, Password = "pw-1" };
        await using var client = new VncClient(Options(server, "pw-1", user: "anna"), new Recorder());
        await client.ConnectAsync(CancellationToken.None);
        await server.Ready;
        Assert.Equal(("anna", "pw-1"), (server.PlainUser, server.PlainPassword));
    }

    [Fact]
    public async Task A_changed_certificate_stops_the_connection_before_anything_secret_is_sent()
    {
        using var certificate = NewCertificate();
        using var server = new FakeVncServer { Types = [19], SubTypes = [261], Certificate = certificate };
        await using var client = new VncClient(Options(server, check: _ => false), new Recorder());
        var error = await Assert.ThrowsAsync<RdpException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Equal(JulOS.Integrations.Display.CertificateFingerprint.Of(certificate), error.CertificateFingerprint);
        Assert.False(server.PasswordAccepted);
    }

    [Fact]
    public async Task VeNCrypt_without_a_certificate_is_reported_as_not_supported()
    {
        using var server = new FakeVncServer { Types = [19], SubTypes = [257, 258, 259] };
        await using var client = new VncClient(Options(server), new Recorder());
        var error = await Assert.ThrowsAsync<RdpException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Contains("without a certificate", error.Message);
    }

    // ---- pictures ----

    [Fact]
    public async Task Raw_and_copy_rectangles_end_up_in_the_framebuffer_and_are_reported()
    {
        using var server = new FakeVncServer { Width = 8, Height = 4 };
        var (client, handler, _, cts) = await StartAsync(server);
        await using var guard = client;
        server.Send(Update(Rect(0, 0, 2, 1, 0, Pixel(1, 2, 3), Pixel(4, 5, 6)), Rect(4, 2, 2, 1, 1, [0, 0, 0, 0])));
        await handler.WaitAsync(() => handler.Updates.Count >= 2);

        lock (client.FrameLock)
        {
            Assert.Equal([1, 2, 3], client.Framebuffer[..3]);
            Assert.Equal([4, 5, 6], client.Framebuffer[4..7]);
            var at = (2 * 8 + 4) * 4;                                                // copied to x 4, y 2
            Assert.Equal([1, 2, 3], client.Framebuffer[at..(at + 3)]);
            Assert.Equal([4, 5, 6], client.Framebuffer[(at + 4)..(at + 7)]);
        }

        Assert.Contains((0, 0, 2, 1), handler.Updates);
        Assert.Contains((4, 2, 2, 1), handler.Updates);
        // After each update the client asks for the next change
        await server.WaitForAsync(m => m[0] == 3 && m[1] == 1);
        cts.Cancel();
    }

    [Fact]
    public async Task Hextile_and_zrle_arrive_through_the_whole_client()
    {
        using var server = new FakeVncServer { Width = 40, Height = 20 };
        var (client, handler, _, cts) = await StartAsync(server);
        await using var guard = client;
        // Hextile: a 40 x 20 rectangle is 3 x 2 tiles; every tile a solid background (the first one gives it)
        var tiles = new List<byte[]> { new byte[] { 0x02 }, Pixel(9, 8, 7) };
        for (var i = 0; i < 5; i++)
        {
            tiles.Add([0x00]);
        }

        server.Send(Update(Rect(0, 0, 40, 20, 5, tiles.ToArray())));
        await handler.WaitAsync(() => handler.Updates.Count >= 1);

        // ZRLE
        var memory = new MemoryStream();
        using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write([1, 30, 40, 50]);
            zlib.Flush();
        }

        var compressed = memory.ToArray();
        server.Send(Update(Rect(0, 0, 40, 20, 16, U32((uint)compressed.Length), compressed)));
        await handler.WaitAsync(() => handler.Updates.Count >= 2);
        lock (client.FrameLock)
        {
            Assert.Equal([30, 40, 50], client.Framebuffer[..3]);
            var last = (19 * 40 + 39) * 4;
            Assert.Equal([30, 40, 50], client.Framebuffer[last..(last + 3)]);
        }

        cts.Cancel();
    }

    [Fact]
    public async Task The_pointer_shape_becomes_rgba_with_the_mask_as_alpha()
    {
        using var server = new FakeVncServer();
        var (client, handler, _, cts) = await StartAsync(server);
        await using var guard = client;
        // 2 x 2: pixels BGRX, mask rows of one byte (top bit = left pixel): row 0 both set, row 1 only the left one
        server.Send(Update(Rect(1, 0, 2, 2, -239, Pixel(10, 20, 30), Pixel(40, 50, 60), Pixel(70, 80, 90), Pixel(100, 110, 120), [0b1100_0000, 0b1000_0000])));
        await handler.WaitAsync(() => handler.Cursors.Count >= 1);
        var cursor = handler.Cursors[0];
        Assert.Equal((1, 0, 2, 2), (cursor.HotX, cursor.HotY, cursor.W, cursor.H));
        Assert.Equal([30, 20, 10, 255, 60, 50, 40, 255, 90, 80, 70, 255, 120, 110, 100, 0], cursor.Rgba);

        server.Send(Update(Rect(0, 0, 0, 0, -239)));
        await handler.WaitAsync(() => Volatile.Read(ref handler.Hidden) == 1);
        cts.Cancel();
    }

    [Fact]
    public async Task A_new_desktop_size_replaces_the_framebuffer_and_asks_for_everything_again()
    {
        using var server = new FakeVncServer { Width = 8, Height = 4 };
        var (client, handler, _, cts) = await StartAsync(server);
        await using var guard = client;
        Assert.False(client.CanResize);
        var requests = server.Messages.Count(m => m[0] == 3);
        server.Send(Update(Rect(0, 0, 16, 10, -223)));
        await handler.WaitAsync(() => handler.Sizes.Count == 1);
        Assert.Equal((16, 10), handler.Sizes[0]);
        Assert.Equal((16, 10), (client.Width, client.Height));
        Assert.Equal(16 * 10 * 4, client.Framebuffer.Length);
        await server.WaitForAsync(m => m[0] == 3 && m[1] == 0 && BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(6)) == 16 && m.Length > requests);
        cts.Cancel();
    }

    [Fact]
    public async Task ExtendedDesktopSize_enables_size_requests_which_carry_the_screen_of_the_server()
    {
        using var server = new FakeVncServer { Width = 8, Height = 4 };
        var (client, handler, _, cts) = await StartAsync(server);
        await using var guard = client;
        // Reason 0 (server), status 0; one screen with id 7 and flags 3
        byte[] screen = [.. U32(7), .. U16(0), .. U16(0), .. U16(8), .. U16(4), .. U32(3)];
        server.Send(Update(Rect(0, 0, 8, 4, -308, [1, 0, 0, 0], screen)));
        await handler.WaitAsync(() => client.CanResize);

        client.RequestSize(20, 12);
        var message = await server.WaitForAsync(m => m[0] == 251);
        Assert.Equal((20, 12, 1), (BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4)), message[6]));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(8)));
        Assert.Equal((20, 12), (BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(16)), BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(18))));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(20)));

        // The answer: reason 1 (this client), status 0, the new size
        server.Send(Update(Rect(1, 0, 20, 12, -308, [1, 0, 0, 0], [.. U32(7), .. U16(0), .. U16(0), .. U16(20), .. U16(12), .. U32(3)])));
        await handler.WaitAsync(() => handler.Sizes.Count == 1);
        Assert.Equal((20, 12), (client.Width, client.Height));

        // A refusal (status 1) leaves the size as it is
        server.Send(Update(Rect(1, 1, 20, 12, -308, [1, 0, 0, 0], [.. U32(7), .. U16(0), .. U16(0), .. U16(20), .. U16(12), .. U32(3)])));
        await Task.Delay(100);
        Assert.Single(handler.Sizes);
        cts.Cancel();
    }

    [Fact]
    public async Task Without_extended_desktop_size_nothing_is_asked()
    {
        using var server = new FakeVncServer();
        var (client, _, _, cts) = await StartAsync(server);
        await using var guard = client;
        client.RequestSize(100, 100);
        await Task.Delay(100);
        Assert.DoesNotContain(server.Messages, m => m[0] == 251);
        cts.Cancel();
    }

    [Fact]
    public async Task A_rectangle_outside_the_screen_ends_the_session_with_a_clear_error()
    {
        using var server = new FakeVncServer { Width = 8, Height = 4 };
        var (client, _, run, cts) = await StartAsync(server);
        await using var guard = client;
        server.Send(Update(Rect(6, 0, 4, 4, 0, new byte[64])));
        var error = await Assert.ThrowsAsync<RdpException>(() => run);
        Assert.Contains("outside", error.Message);
        cts.Cancel();
    }

    [Fact]
    public async Task A_message_of_an_unknown_type_ends_the_session_with_a_clear_error()
    {
        using var server = new FakeVncServer();
        var (client, _, run, cts) = await StartAsync(server);
        await using var guard = client;
        server.Send([77]);
        var error = await Assert.ThrowsAsync<RdpException>(() => run);
        Assert.Contains("does not understand", error.Message);
        cts.Cancel();
    }

    [Fact]
    public async Task When_the_server_hangs_up_the_session_ends_quietly()
    {
        using var server = new FakeVncServer();
        var (client, _, run, cts) = await StartAsync(server);
        await using var guard = client;
        server.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
    }

    // ---- input ----

    [Fact]
    public async Task Pointer_events_carry_the_position_and_the_buttons_as_they_are_held()
    {
        using var server = new FakeVncServer { Width = 100, Height = 80 };
        var (client, _, _, cts) = await StartAsync(server);
        await using var guard = client;
        client.MouseMove(10, 20);
        client.MouseButton(0, true);
        client.MouseButton(2, true);
        client.MouseMove(500, 500);                           // clamped to the screen
        client.MouseButton(0, false);
        client.MouseButton(1, true);
        client.MouseButton(3, true);
        client.MouseButton(4, true);                          // forward: the protocol's single byte has no room for it
        await server.WaitForAsync(m => m[0] == 5 && m[1] == 0x80 + 2 + 4);
        var pointer = server.Messages.Where(m => m[0] == 5).Select(m => (Mask: m[1], X: BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(2)), Y: BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(4)))).ToArray();
        Assert.Equal([(0, 10, 20), (1, 10, 20), (5, 10, 20), (5, 99, 79), (4, 99, 79), (6, 99, 79), (0x86, 99, 79)], pointer.Select(p => ((int)p.Mask, (int)p.X, (int)p.Y)).ToArray());
        cts.Cancel();
    }

    [Fact]
    public async Task The_wheel_is_a_button_pair_pressed_and_released_once_for_each_notch()
    {
        using var server = new FakeVncServer();
        var (client, _, _, cts) = await StartAsync(server);
        await using var guard = client;
        client.MouseWheel(120, false);                        // up: button 4 = bit 3
        client.MouseWheel(-240, false);                       // down twice: button 5 = bit 4
        client.MouseWheel(120, true);                         // right: button 7 = bit 6
        client.MouseWheel(-120, true);                        // left: button 6 = bit 5
        client.MouseWheel(30, false);                         // less than half a notch: kept, nothing yet
        await server.WaitForAsync(m => m[0] == 5 && m[1] == 32);
        await Task.Delay(100);
        var masks = server.Messages.Where(m => m[0] == 5).Select(m => (int)m[1]).ToArray();
        Assert.Equal([8, 0, 16, 0, 16, 0, 64, 0, 32, 0], masks);
        cts.Cancel();
    }

    [Fact]
    public async Task Small_wheel_steps_add_up_to_a_notch()
    {
        using var server = new FakeVncServer();
        var (client, _, _, cts) = await StartAsync(server);
        await using var guard = client;
        for (var i = 0; i < 4; i++)
        {
            client.MouseWheel(30, false);
        }

        await server.WaitForAsync(m => m[0] == 5 && m[1] == 8);
        await Task.Delay(50);
        Assert.Equal([8, 0], server.Messages.Where(m => m[0] == 5).Select(m => (int)m[1]).ToArray());
        cts.Cancel();
    }

    [Fact]
    public async Task Keys_go_as_keysyms_by_themselves_by_scancode_or_as_typed_characters()
    {
        using var server = new FakeVncServer();
        var (client, _, _, cts) = await StartAsync(server);
        await using var guard = client;
        client.Keysym(0x61, true);
        client.Keysym(0x61, false);
        client.Key(0x2A, false, true);                         // Shift down
        client.Key(0x15, false, true);                         // the key that is z in the German layout, with Shift: Z
        client.Key(0x15, false, false);
        client.Key(0x2A, false, false);
        client.Key(0x38, true, true);                          // AltGr
        client.Key(0x10, false, true);                         // @
        client.Key(0x7F, false, true);                         // unknown: nothing is sent
        client.Unicode('€');
        client.Unicode('\ud83d');                              // an emoji comes as a pair of units and is one key
        client.Unicode('\ude00');
        await server.WaitForAsync(m => m[0] == 4 && BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(4)) == 0x0101F600 && m[1] == 0);
        var keys = server.Messages.Where(m => m[0] == 4).Select(m => (Down: m[1], Keysym: BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(4)))).ToArray();
        Assert.Equal(
            [(1, 0x61u), (0, 0x61u), (1, 0xFFE1u), (1, 0x5Au), (0, 0x5Au), (0, 0xFFE1u), (1, 0xFE03u), (1, 0x40u), (1, 0x20ACu | 0x01000000u), (0, 0x20ACu | 0x01000000u), (1, 0x0101F600u), (0, 0x0101F600u)],
            keys.Select(k => ((int)k.Down, k.Keysym)).ToArray());
        cts.Cancel();
    }

    // ---- clipboard ----

    [Fact]
    public async Task Text_from_the_server_arrives_as_latin1_and_text_to_it_goes_as_latin1()
    {
        using var server = new FakeVncServer();
        var handler = new Recorder();
        var received = new List<string>();
        var client = new VncClient(Options(server), handler) { ClipboardReceived = received.Add };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(cts.Token);
        await server.Ready;
        await using var guard = client;
        var run = client.RunAsync(cts.Token);

        var text = "Grüße"u8.ToArray();
        _ = text;
        byte[] latin1 = [0x47, 0x72, 0xFC, 0xDF, 0x65];
        server.Send([3, 0, 0, 0], U32((uint)latin1.Length), latin1);
        await handler.WaitAsync(() => received.Count == 1);
        Assert.Equal("Grüße", received[0]);

        client.SendClipboard("Preis 5 € ä");
        var message = await server.WaitForAsync(m => m[0] == 6);
        Assert.Equal("Preis 5 ? \u00e4".Length, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(4)));
        Assert.Equal((byte)'?', message[8 + 8]);                       // the euro sign is not in Latin-1
        Assert.Equal(0xE4, message[8 + 10]);
        cts.Cancel();
    }

    [Fact]
    public async Task The_extended_clipboard_carries_utf8_text_both_ways()
    {
        using var server = new FakeVncServer();
        var handler = new Recorder();
        var received = new List<string>();
        var client = new VncClient(Options(server), handler) { ClipboardReceived = received.Add };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(cts.Token);
        await server.Ready;
        await using var guard = client;
        _ = client.RunAsync(cts.Token);

        // The server's capabilities: flags (caps, text, and the actions), one size for text. -(4 + 4) is the length.
        const uint Caps = 1u << 24, Text = 1, Request = 1u << 25, Notify = 1u << 27, Provide = 1u << 28;
        server.Send([3, 0, 0, 0], U32(unchecked((uint)-8)), U32(Caps | Text | Request | Notify | Provide), U32(1 << 20));
        await handler.WaitAsync(() => client.HasExtendedClipboard);
        var caps = await server.WaitForAsync(m => m[0] == 6 && BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(8)) == (Caps | Text | Request | (1u << 26) | Notify | Provide));
        Assert.Equal(-8, BinaryPrimitives.ReadInt32BigEndian(caps.AsSpan(4)));

        // The server has text: the client asks for it, and the server provides it zlib-compressed with a length in front (UTF-8, ends with a zero byte, CRLF)
        server.Send([3, 0, 0, 0], U32(unchecked((uint)-4)), U32(Notify | Text));
        var request = await server.WaitForAsync(m => m[0] == 6 && BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(8)) == (Request | Text));
        Assert.Equal(-4, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4)));
        var utf8 = System.Text.Encoding.UTF8.GetBytes("Grüße €\r\nZeile 2\0");
        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(U32((uint)utf8.Length));
            zlib.Write(utf8);
        }

        var body = packed.ToArray();
        server.Send([3, 0, 0, 0], U32(unchecked((uint)-(4 + body.Length))), U32(Provide | Text), body);
        await handler.WaitAsync(() => received.Count == 1);
        Assert.Equal("Grüße €\nZeile 2", received[0]);

        // Text to the server: a notice first; when the server asks, the text comes zlib-compressed
        client.SendClipboard("Hallo €");
        var notify = await server.WaitForAsync(m => m[0] == 6 && BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(8)) == (Notify | Text));
        Assert.Equal(-4, BinaryPrimitives.ReadInt32BigEndian(notify.AsSpan(4)));
        server.Send([3, 0, 0, 0], U32(unchecked((uint)-4)), U32(Request | Text));
        var provided = await server.WaitForAsync(m => m[0] == 6 && BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(8)) == (Provide | Text));
        var length = -BinaryPrimitives.ReadInt32BigEndian(provided.AsSpan(4));
        using var unpack = new ZLibStream(new MemoryStream(provided, 12, length - 4), CompressionMode.Decompress);
        var header = new byte[4];
        unpack.ReadExactly(header);
        var data = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        unpack.ReadExactly(data);
        Assert.Equal("Hallo €\0", System.Text.Encoding.UTF8.GetString(data));
        cts.Cancel();
    }
}
