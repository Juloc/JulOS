using System.Collections.Concurrent;
using System.Text;
using JulOS.Integrations.Display.Rdp.Drive;

namespace JulOS.Integrations.Display.Rdp.Channels.Tests;

// The device redirection channel with a drive: the handshake as a server drives it (messages of MS-RDPEFS section 4), the device list with one file
// system, what the server must have said before a request is served, and a few requests end to end.
public sealed class RdpdrDriveTests : IDisposable
{
    private const string ServerAnnounce = "72 44 6e 49 01 00 0c 00 01 00 00 00";
    private const string ClientIdConfirm = "72 44 43 43 01 00 0c 00 01 00 00 00";
    private const string UserLoggedOn = "72 44 4c 55";

    // 4.8 Server Core Capability Request with the drive set (type 4, version 2)
    private const string ServerCapabilities = """
        72 44 50 53 05 00 00 00 01 00 2c 00 02 00 00 00
        02 00 00 00 00 00 00 00 01 00 0c 00 ff ff 00 00
        00 00 00 00 07 00 00 00 00 00 00 00 00 00 00 00
        02 00 00 00 02 00 08 00 01 00 00 00 03 00 08 00
        01 00 00 00 04 00 08 00 02 00 00 00 05 00 08 00
        01 00 00 00
        """;

    // The same without the drive set: a server whose policy forbids drive redirection
    private const string ServerCapabilitiesWithoutDrive = """
        72 44 50 53 04 00 00 00 01 00 2c 00 02 00 00 00
        02 00 00 00 00 00 00 00 01 00 0c 00 ff ff 00 00
        00 00 00 00 07 00 00 00 00 00 00 00 00 00 00 00
        02 00 00 00 02 00 08 00 01 00 00 00 03 00 08 00
        01 00 00 00 05 00 08 00 01 00 00 00
        """;

    private const string EmptyDeviceList = "72 44 41 44 00 00 00 00";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "julos-rdpdr-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly BlockingCollection<byte[]> _sent = [];
    private readonly List<string> _log = [];

    public RdpdrDriveTests()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the drive is Linux only");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello world");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { /* egal */ }
    }

    private RdpdrChannel Create(bool writable = false, string name = "JULOS")
    {
        var channel = new RdpdrChannel("TEST", true, new SharedDrive(_root, writable, name));
        channel.Send = message => _sent.Add(message.ToArray());
        channel.Log = message => { lock (_log) _log.Add(message); };
        return channel;
    }

    private byte[] Next() => _sent.TryTake(out var message, TimeSpan.FromSeconds(10)) ? message : throw new TimeoutException("nothing was sent");

    private void NothingMore() => Assert.False(_sent.TryTake(out _, TimeSpan.FromMilliseconds(300)), "something was sent");

    // Everything up to and including the device list
    private void Handshake(RdpdrChannel channel, string capabilities = ServerCapabilities)
    {
        channel.Receive(Hex.Bytes(ServerAnnounce));
        Next(); Next();                                              // client id, client name
        channel.Receive(Hex.Bytes(capabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        channel.Receive(Hex.Bytes(UserLoggedOn));
    }

    private static byte[] Irp(uint device, uint fileId, uint completion, uint major, uint minor, byte[] body)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        w.Write((ushort)0x4472); w.Write((ushort)0x4952); w.Write(device); w.Write(fileId); w.Write(completion); w.Write(major); w.Write(minor); w.Write(body);
        return m.ToArray();
    }

    private static byte[] CreateBody(string path, uint access, uint disposition = 1, uint options = 0)
    {
        var name = Encoding.Unicode.GetBytes(path + "\0");
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        w.Write(access); w.Write(0L); w.Write(0u); w.Write(7u); w.Write(disposition); w.Write(options); w.Write((uint)name.Length); w.Write(name);
        return m.ToArray();
    }

    private static byte[] DeviceReply(uint device, uint status) => [0x72, 0x44, 0x72, 0x64, .. BitConverter.GetBytes(device), .. BitConverter.GetBytes(status)];

    [Fact]
    public void The_capabilities_name_the_drive_set_and_the_device_list_holds_one_file_system()
    {
        var channel = Create();
        channel.Receive(Hex.Bytes(ServerAnnounce));
        Next(); Next();

        channel.Receive(Hex.Bytes(ServerCapabilities));
        var caps = Next();
        Assert.Equal(2, BitConverter.ToUInt16(caps, 4));                                       // two sets: general and drive
        Assert.Equal("04 00 08 00 02 00 00 00", Hex.Text(caps[^8..]).Trim());                  // drive set, length 8, version 2
        Assert.Equal(4 + 4 + 44 + 8, caps.Length);

        channel.Receive(Hex.Bytes(ClientIdConfirm));
        NothingMore();
        channel.Receive(Hex.Bytes(UserLoggedOn));
        var list = Next();
        Assert.Equal("72 44 41 44", Hex.Text(list[..4]).Trim());
        Assert.Equal(1u, BitConverter.ToUInt32(list, 4));                                      // one device
        Assert.Equal(8u, BitConverter.ToUInt32(list, 8));                                      // RDPDR_DTYP_FILESYSTEM
        Assert.Equal(1u, BitConverter.ToUInt32(list, 12));                                     // its id
        Assert.Equal("JULOS\0\0\0", Encoding.ASCII.GetString(list, 16, 8));                    // preferred DOS name
        Assert.Equal(6u, BitConverter.ToUInt32(list, 24));
        Assert.Equal("JULOS\0", Encoding.ASCII.GetString(list, 28, 6));
        Assert.Equal(34, list.Length);
        Assert.False(channel.DriveReady);                                                      // not before the server accepted it
    }

    [Fact]
    public void A_server_that_forbids_drives_gets_the_empty_list_and_the_old_capabilities()
    {
        var channel = Create();
        Handshake(channel, ServerCapabilitiesWithoutDrive);
        // announce replies were taken; now: capabilities (one set), then the empty list
        var all = new List<byte[]>();
        while (_sent.TryTake(out var m)) all.Add(m);
        Assert.Equal(2, all.Count);
        Assert.Equal(1, BitConverter.ToUInt16(all[0], 4));
        Assert.Equal(4 + 4 + 44, all[0].Length);
        Assert.Equal(EmptyDeviceList, Hex.Text(all[1]).Trim().Replace("  ", " "));
        channel.Receive(DeviceReply(1, 0));
        Assert.False(channel.DriveReady);
    }

    [Fact]
    public void Requests_are_served_only_after_the_server_accepted_the_device()
    {
        var channel = Create();
        Handshake(channel);
        Next(); Next();                                                                         // capabilities, device list

        // before the answer to the device list: ignored
        channel.Receive(Irp(1, 0, 7, 0, 0, CreateBody("\\hello.txt", 0x80000000)));
        NothingMore();
        Assert.False(channel.DriveReady);

        channel.Receive(DeviceReply(1, 0));
        Assert.True(channel.DriveReady);
        channel.Receive(Irp(1, 0, 8, 0, 0, CreateBody("\\hello.txt", 0x80000000)));
        var answer = Next();
        Assert.Equal("72 44 43 49", Hex.Text(answer[..4]).Trim());                              // PAKID_CORE_DEVICE_IOCOMPLETION
        Assert.Equal(1u, BitConverter.ToUInt32(answer, 4));                                     // device
        Assert.Equal(8u, BitConverter.ToUInt32(answer, 8));                                     // completion id
        Assert.Equal(0u, BitConverter.ToUInt32(answer, 12));                                    // success
        var fileId = BitConverter.ToUInt32(answer, 16);
        Assert.NotEqual(0u, fileId);

        var read = new MemoryStream(); var w = new BinaryWriter(read);
        w.Write(100u); w.Write(0UL); w.Write(new byte[20]);
        channel.Receive(Irp(1, fileId, 9, 3, 0, read.ToArray()));
        var data = Next();
        Assert.Equal(11u, BitConverter.ToUInt32(data, 16));
        Assert.Equal("hello world", Encoding.ASCII.GetString(data, 20, 11));

        // another device id: ignored, and nothing opened
        channel.Receive(Irp(3, 0, 10, 0, 0, CreateBody("\\hello.txt", 0x80000000)));
        NothingMore();
        channel.Closed();
    }

    [Fact]
    public void A_refused_device_is_never_served()
    {
        var channel = Create();
        Handshake(channel);
        Next(); Next();
        channel.Receive(DeviceReply(1, 0xC0000001));
        Assert.False(channel.DriveReady);
        channel.Receive(Irp(1, 0, 8, 0, 0, CreateBody("\\hello.txt", 0x80000000)));
        NothingMore();
        Assert.Contains(_log, l => l.Contains("refused"));
    }

    [Fact]
    public void A_reply_for_a_device_that_was_not_announced_is_ignored()
    {
        var channel = Create();
        Handshake(channel);
        Next(); Next();
        channel.Receive(DeviceReply(5, 0));
        Assert.False(channel.DriveReady);
        channel.Receive(DeviceReply(1, 0));
        channel.Receive(DeviceReply(1, 0));                                                      // twice: the same service
        Assert.True(channel.DriveReady);
    }

    [Fact]
    public void Read_only_is_what_the_server_sees_on_a_drive_without_the_write_switch()
    {
        var channel = Create(writable: false);
        Handshake(channel);
        Next(); Next();
        channel.Receive(DeviceReply(1, 0));
        channel.Receive(Irp(1, 0, 8, 0, 0, CreateBody("\\new.txt", 0x40000000, 2)));
        var answer = Next();
        Assert.Equal(0xC0000022u, BitConverter.ToUInt32(answer, 12));                          // access denied
        Assert.False(File.Exists(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public void The_same_object_serves_the_next_session_after_it_closed()
    {
        var channel = Create();
        Handshake(channel);
        Next(); Next();
        channel.Receive(DeviceReply(1, 0));
        Assert.True(channel.DriveReady);

        channel.Closed();
        Assert.False(channel.DriveReady);
        channel.Receive(Irp(1, 0, 8, 0, 0, CreateBody("\\hello.txt", 0x80000000)));
        NothingMore();

        // a new session, and a server announce in the middle of a session starts over as well
        Handshake(channel);
        Next(); Next();
        channel.Receive(DeviceReply(1, 0));
        Assert.True(channel.DriveReady);
        channel.Receive(Hex.Bytes(ServerAnnounce));
        Assert.False(channel.DriveReady);
    }

    [Fact]
    public void A_logon_message_before_the_capabilities_waits_for_them_before_the_list_goes_out()
    {
        var channel = Create();
        channel.Receive(Hex.Bytes(ServerAnnounce));
        Next(); Next();
        channel.Receive(Hex.Bytes(UserLoggedOn));
        NothingMore();                                                                          // the drive needs the capabilities first
        channel.Receive(Hex.Bytes(ServerCapabilities));
        Next();                                                                                 // capabilities
        var list = Next();
        Assert.Equal(1u, BitConverter.ToUInt32(list, 4));
    }

    [Fact]
    public void A_drive_needs_a_valid_name_and_an_existing_root()
    {
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("T", true, new SharedDrive(_root + "-missing")));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("T", true, new SharedDrive("relative")));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("T", true, new SharedDrive("/")));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("T", true, new SharedDrive(_root, false, "")));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("T", true, new SharedDrive(_root, false, "TOOLONGNAME")));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("T", true, new SharedDrive(_root, false, "a b")));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("T", true, new SharedDrive(_root, false, "../x")));
        Assert.True(SharedDrive.IsValidName("JULOS"));
        Assert.True(SharedDrive.IsValidName("a_1"));
        Assert.False(SharedDrive.IsValidName(null));
    }
}
