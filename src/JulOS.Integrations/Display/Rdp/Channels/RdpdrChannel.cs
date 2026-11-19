using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using JulOS.Integrations.Display.Rdp.Drive;

namespace JulOS.Integrations.Display.Rdp.Channels;

// The device redirection channel "rdpdr" (MS-RDPEFS). Without a drive it has no devices at all: it only exists
// because Windows does not talk on "rdpsnd" to a client that does not announce "rdpdr" as well (MS-RDPEA <1>):
// it goes through the handshake - version and client id, the computer's name, capabilities, an empty device
// list - and then has nothing to say.
//
// With a SharedDrive it announces exactly one device, a file system rooted at one folder of the server, and
// answers the server's I/O requests for it through a DriveService (read only unless the drive says otherwise;
// every path goes through the DriveJail). The drive is announced only when the server's capabilities include
// the drive set (a server that forbids drive redirection gets an empty list as before).
//
// Everything happens on the thread that calls Receive and Closed; the properties may be read from any thread.
public sealed class RdpdrChannel : StaticChannel
{
    public const string DefaultComputerName = "JulOS";

    // The longest computer name this client sends; Windows shows it next to redirected devices, of which there
    // are none, so there is no point in more.
    public const int MaxComputerNameLength = 255;

    private const ushort ComponentCore = 0x4472;
    private const ushort ComponentPrinter = 0x5052;

    private const ushort ServerAnnounce = 0x496E;
    private const ushort ClientIdConfirm = 0x4343;
    private const ushort ClientName = 0x434E;
    private const ushort DeviceListAnnounce = 0x4441;
    private const ushort DeviceReply = 0x6472;
    private const ushort DeviceIoRequest = 0x4952;
    private const ushort ServerCapability = 0x5350;
    private const ushort ClientCapability = 0x4350;
    private const ushort UserLoggedOn = 0x554C;

    private const ushort CapabilityGeneral = 1;
    private const ushort CapabilityDrive = 4;
    private const uint DriveVersion2 = 2;
    private const uint DeviceTypeFilesystem = 8;

    /// <summary>The id of the one device this client announces.</summary>
    public const uint DriveDeviceId = 1;
    private const uint GeneralVersion2 = 2;
    private const int GeneralLength = 44;

    // The minor versions a client may name (MS-RDPEFS 2.2.2.3), highest first: this one speaks 13, the
    // version of Windows Server 2016 and later, and goes down to what the server speaks.
    private static readonly ushort[] MinorVersions = [0x000D, 0x000C, 0x000A, 0x0005, 0x0002];

    // ioCode1: the bits marked "unused, always set", and the two security requests (MS-RDPEFS 2.2.2.7.1).
    private const uint AllIoCodes = 0x0000FFFF;

    // extendedPDU: device list remove, display name, user logged on - what FreeRDP and Windows clients say.
    private const uint DeviceRemovePdus = 0x00000001;
    private const uint ClientDisplayNamePdu = 0x00000002;
    private const uint UserLoggedOnPdu = 0x00000004;
    private const uint ExtendedPdus = DeviceRemovePdus | ClientDisplayNamePdu | UserLoggedOnPdu;

    private const uint EnableAsyncIo = 0x00000001;

    private readonly Lock _gate = new();
    private readonly byte[] _name;
    private readonly bool _announceEmptyList;

    // Where the handshake stands; Server Announce starts it over.
    private bool _announced;
    private ushort _minor;
    private uint _clientId;
    private bool _capabilities;
    private uint _serverIoCodes;
    private bool _serverSendsLogon;
    private bool _confirmed;
    private bool _listSent;
    private bool _loggedOn;

    // The shared drive: whether the server's capabilities have the drive set, whether it accepted the device, and the service for this session.
    private readonly SharedDrive? _drive;
    private bool _serverHasDrive;
    private bool _driveAnnounced;
    private DriveService? _service;

    // computerName is sent in UTF-16 with its terminator. announceEmptyDeviceList: when the server is ready for
    // devices, tell it that there are none (an empty Client Device List Announce Request). FreeRDP 3 and
    // IronRDP send nothing in that case; both are within the specification, which only says the client
    // announces "its devices" then.
    //
    // drive: a folder of the server to hand to the remote session as a drive (see SharedDrive); null: none.
    public RdpdrChannel(string computerName = DefaultComputerName, bool announceEmptyDeviceList = true, SharedDrive? drive = null)
    {
        if (drive is not null)
        {
            var jail = DriveJail.TryCreate(drive.Root, out var rootError);
            if (!SharedDrive.IsValidName(drive.Name) || jail is null)
            {
                throw new ArgumentException(string.IsNullOrEmpty(rootError) ? "The drive needs a valid name and an existing root folder." : rootError, nameof(drive));
            }
        }

        _drive = drive;
        ArgumentNullException.ThrowIfNull(computerName);
        if (computerName.Length is 0 or > MaxComputerNameLength || computerName.Contains('\0'))
        {
            throw new ArgumentException($"A computer name has 1 to {MaxComputerNameLength} characters and no NUL.", nameof(computerName));
        }

        ComputerName = computerName;
        _name = Encoding.Unicode.GetBytes(computerName + "\0");
        _announceEmptyList = announceEmptyDeviceList;
    }

    public override string Name => "rdpdr";

    public string ComputerName { get; }

    // The client id the server confirmed, 0 before it did.
    public uint ClientId
    {
        get
        {
            lock (_gate)
            {
                return _confirmed ? _clientId : 0;
            }
        }
    }

    // The minor version both sides speak, 0 before the server announced itself.
    public int VersionMinor
    {
        get
        {
            lock (_gate)
            {
                return _announced ? _minor : 0;
            }
        }
    }

    // The handshake is complete: capabilities exchanged, client id confirmed, and the (empty) device list
    // said as far as it is due.
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _announced && _capabilities && _confirmed;
            }
        }
    }

    // Where a client id comes from when the client has to draw one; the tests bring their own.
    internal Func<uint> RandomId { get; set; } = () => BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));

    public override void Receive(ReadOnlySpan<byte> message)
    {
        var reader = new ByteReader(message);
        if (!reader.TryU16(out var component) || !reader.TryU16(out var packet))
        {
            Report("dropped a message shorter than its header");
            return;
        }

        if (component == ComponentPrinter)
        {
            Report($"ignored printer message {packet:X4}: there are no printers");
            return;
        }

        if (component != ComponentCore)
        {
            Report($"dropped a message of the unknown component {component:X4}");
            return;
        }

        var outgoing = new List<byte[]>();
        string? note;
        DriveService? service = null;
        DriveService? old = null;
        lock (_gate)
        {
            var before = _service;
            note = packet switch
            {
                ServerAnnounce => Announce(reader, outgoing),
                ServerCapability => Capabilities(reader, outgoing),
                ClientIdConfirm => Confirm(reader, outgoing),
                UserLoggedOn => LoggedOn(outgoing),
                DeviceReply => DeviceResponse(reader),
                DeviceIoRequest => IoRequest(reader, out service),
                _ => $"dropped the unknown message {packet:X4}",
            };

            if (!ReferenceEquals(before, _service) && before is not null)
            {
                old = before;
            }
        }

        old?.Dispose();
        if (note is not null)
        {
            Report(note);
        }

        // The disk is read and written on the drive's own thread; this only hands the request over
        service?.Submit(message);

        foreach (var answer in outgoing)
        {
            Transmit(answer);
        }
    }

    public override void Closed()
    {
        DriveService? service;
        lock (_gate)
        {
            _announced = false;
            _capabilities = false;
            _confirmed = false;
            _listSent = false;
            _loggedOn = false;
            _driveAnnounced = false;
            _serverHasDrive = false;
            service = _service;
            _service = null;
        }

        service?.Dispose();
    }

    /// <summary>Whether the server accepted the drive and requests for it are answered (tests, display).</summary>
    public bool DriveReady
    {
        get
        {
            lock (_gate)
            {
                return _service is not null;
            }
        }
    }

    // ---- the server's messages (all under the lock) -----------------------------------------------------------

    private string? Announce(ByteReader reader, List<byte[]> outgoing)
    {
        if (!reader.TryU16(out var major) || !reader.TryU16(out var minor) || !reader.TryU32(out var serverId))
        {
            return "dropped a server announce that is too short";
        }

        // It may come again in the middle of a session: the server reconnected, and everything starts over
        // (MS-RDPEFS 3.2.5.1.2).
        _announced = true;
        _capabilities = false;
        _confirmed = false;
        _listSent = false;
        _loggedOn = false;
        _driveAnnounced = false;
        _serverHasDrive = false;
        _service = null;                        // the session starts over: the drive is announced and accepted again (the old service is disposed by Receive)
        _serverIoCodes = AllIoCodes;
        _serverSendsLogon = true;
        _minor = MinorVersions.FirstOrDefault(version => version <= minor, MinorVersions[^1]);

        // From version 12 on the client takes the server's id; before that it draws one (MS-RDPEFS 3.2.5.1.3),
        // and not one the server could mistake for something else.
        _clientId = serverId;
        if (minor < 0x000C)
        {
            do
            {
                _clientId = RandomId();
            }
            while (_clientId is 0 or uint.MaxValue || _clientId == serverId);
        }

        outgoing.Add(Message(ClientIdConfirm, 8).U16(1).U16(_minor).U32(_clientId).ToArray());
        outgoing.Add(Message(ClientName, 12 + _name.Length).U32(1).U32(0).U32((uint)_name.Length).Bytes(_name).ToArray());
        return major == 1 ? null : $"the server announces major version {major}; answered with 1";
    }

    private string? Capabilities(ByteReader reader, List<byte[]> outgoing)
    {
        if (!_announced)
        {
            return "ignored capabilities before the server announced itself";
        }

        if (!reader.TryU16(out var count) || !reader.TrySkip(2))
        {
            return "dropped capabilities too short to count their sets";
        }

        uint ioCodes = AllIoCodes;
        var logon = true;
        var drive = false;
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryU16(out var type) || !reader.TryU16(out var length) || length < 8 || !reader.TryBytes(length - 4, out var set))
            {
                return "dropped capabilities with a set that does not fit";
            }

            // The general set: its version, then osType, osVersion, the protocol version, ioCode1, ioCode2 and
            // extendedPDU. Version 1 has no SpecialTypeDeviceCap at the end; neither is needed here.
            if (type == CapabilityDrive)
            {
                drive = true;
            }

            if (type == CapabilityGeneral && set.Length >= 4 + 24)
            {
                ioCodes = BinaryPrimitives.ReadUInt32LittleEndian(set[16..]);
                logon = (BinaryPrimitives.ReadUInt32LittleEndian(set[24..]) & UserLoggedOnPdu) != 0;
            }
        }

        _serverIoCodes = ioCodes;
        _serverSendsLogon = logon;
        _serverHasDrive = drive && _drive is not null;

        // The general set; with a drive (and a server that has the drive set) the drive set as well. Without devices only the general set:
        // this client supports no device type, so it lists none (MS-RDPEFS 3.2.5.1.8).
        var driveSet = _serverHasDrive ? 8 : 0;
        var capabilities = Message(ClientCapability, 4 + GeneralLength + driveSet)
            .U16(_serverHasDrive ? (ushort)2 : (ushort)1)
            .U16(0)
            .U16(CapabilityGeneral)
            .U16(GeneralLength)
            .U32(GeneralVersion2)
            .U32(0)
            .U32(0)
            .U16(1)
            .U16(_minor)
            .U32(AllIoCodes & _serverIoCodes)
            .U32(0)
            .U32(ExtendedPdus)
            .U32(EnableAsyncIo)
            .U32(0)
            .U32(0);
        if (_serverHasDrive)
        {
            capabilities.U16(CapabilityDrive).U16(8).U32(DriveVersion2);
        }

        outgoing.Add(capabilities.ToArray());
        _capabilities = true;
        AnnounceIfDue(false, outgoing);
        return null;
    }

    private string? Confirm(ByteReader reader, List<byte[]> outgoing)
    {
        if (!_announced)
        {
            return "ignored a client id confirm before the server announced itself";
        }

        if (!reader.TryU16(out _) || !reader.TryU16(out _) || !reader.TryU32(out var id))
        {
            return "dropped a client id confirm that is too short";
        }

        // The server has the last word on the id (FreeRDP takes it the same way).
        string? note = id == _clientId ? null : $"the server confirms client id {id:X8} instead of {_clientId:X8}";
        _clientId = id;
        _confirmed = true;
        AnnounceIfDue(false, outgoing);
        return note;
    }

    private string? LoggedOn(List<byte[]> outgoing)
    {
        if (!_announced)
        {
            return "ignored a user logged on before the server announced itself";
        }

        _loggedOn = true;
        AnnounceIfDue(true, outgoing);
        return null;
    }

    // Devices are announced once the user is logged on - or, with a server that never says so, as soon as
    // capabilities and client id are settled (MS-RDPEFS 3.2.5.1.5 and 3.2.5.1.9).
    private void AnnounceIfDue(bool loggedOn, List<byte[]> outgoing)
    {
        if (_listSent || !_announceEmptyList)
        {
            return;
        }

        // A drive can only be announced when the server's capabilities are known (they say whether it takes drives at all); a "logged on" that
        // came first is remembered until then.
        if (_drive is not null && !_capabilities)
        {
            return;
        }

        var due = loggedOn || _loggedOn || (_capabilities && _confirmed && (!_serverSendsLogon || _minor < 0x000A));
        if (!due)
        {
            return;
        }

        _listSent = true;
        if (!_serverHasDrive || _drive is null)
        {
            outgoing.Add(Message(DeviceListAnnounce, 4).U32(0).ToArray());
            return;
        }

        // One device: a file system (MS-RDPEFS 2.2.1.3). PreferredDosName is 8 bytes of ASCII with NULs; the device data is the drive's name,
        // ASCII with a terminator, as the clients of the common servers send it.
        _driveAnnounced = true;
        var dos = new byte[8];
        Encoding.ASCII.GetBytes(_drive.Name, dos);
        var data = Encoding.ASCII.GetBytes(_drive.Name + "\0");
        outgoing.Add(Message(DeviceListAnnounce, 4 + 20 + data.Length)
            .U32(1)
            .U32(DeviceTypeFilesystem)
            .U32(DriveDeviceId)
            .Bytes(dos)
            .U32((uint)data.Length)
            .Bytes(data)
            .ToArray());
    }

    // The server's answer to the device list: device id and NTSTATUS. Only success makes the drive usable.
    private string? DeviceResponse(ByteReader reader)
    {
        if (!reader.TryU32(out var device) || !reader.TryU32(out var status))
        {
            return "dropped a device announce response that is too short";
        }

        if (!_driveAnnounced || device != DriveDeviceId || _drive is null)
        {
            return "ignored a device announce response: no such device was announced";
        }

        if (status != 0)
        {
            _driveAnnounced = false;
            return $"the server refused the drive ({status:X8})";
        }

        if (_service is not null)
        {
            return null;
        }

        try
        {
            _service = new DriveService(_drive, DriveDeviceId, Transmit, Report);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or PlatformNotSupportedException)
        {
            _driveAnnounced = false;
            return "the drive cannot be served: " + exception.Message;
        }

        return $"the drive {_drive.Name} is shared ({(_drive.Writable ? "read and write" : "read only")})";
    }

    // A request for a device (MS-RDPEFS 2.2.1.4). Only the one this client announced and the server accepted is served; a request for any other
    // device is ignored (MS-RDPEFS 3.1.5.2), as is every request when there is no drive.
    private string? IoRequest(ByteReader reader, out DriveService? service)
    {
        service = null;
        if (!reader.TryU32(out var device))
        {
            return "dropped an I/O request that is too short";
        }

        if (_service is null || device != DriveDeviceId)
        {
            return "ignored an I/O request: there are no devices";
        }

        service = _service;
        return null;
    }

    private void Transmit(byte[] message)
    {
        var send = Send;
        if (send is null)
        {
            Report("a message is dropped, the channel has no way to send yet");
            return;
        }

        try
        {
            send(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The connection is going down; the core notices that by itself.
            Report("sending failed: " + exception.Message);
        }
    }

    private static ByteWriter Message(ushort packet, int length) =>
        new ByteWriter(4 + length).U16(ComponentCore).U16(packet);
}
