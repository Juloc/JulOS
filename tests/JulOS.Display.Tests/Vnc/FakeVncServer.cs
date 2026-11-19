using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JulOS.Integrations.Display.Vnc;

namespace JulOS.Integrations.Display.Tests.Vnc;

/// <summary>A small VNC server in the test: the handshake as configured, then whatever the test sends; everything the client sends is recorded.</summary>
internal sealed class FakeVncServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<byte[]> _messages = [];
    private readonly Lock _gate = new();
    private Stream? _stream;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Banner { get; init; } = "RFB 003.008\n";

    // What the server offers (3.7 and 3.8: a list; 3.3: the first one decides).
    public byte[] Types { get; init; } = [VncClient_None];

    public string Password { get; init; } = "secret12";

    public int Width { get; init; } = 64;

    public int Height { get; init; } = 48;

    public string Name { get; init; } = "fake desktop";

    // VeNCrypt: the sub-types offered and the certificate TLS uses (null: no TLS is done even if a TLS sub-type is chosen).
    public int[] SubTypes { get; init; } = [];

    public X509Certificate2? Certificate { get; init; }

    public string? Refuse { get; init; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public const byte VncClient_None = 1;

    public int ChosenType { get; private set; }

    public int ChosenSubType { get; private set; }

    public string? PlainUser { get; private set; }

    public string? PlainPassword { get; private set; }

    public bool PasswordAccepted { get; private set; }

    public FakeVncServer()
    {
        _listener.Start();
        _ = Task.Factory.StartNew(Serve, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public IReadOnlyList<byte[]> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>Completes when the handshake is over and the server may send pictures.</summary>
    public Task Ready => _ready.Task;

    public void Send(params byte[][] parts)
    {
        var all = parts.SelectMany(p => p).ToArray();
        lock (_gate)
        {
            _stream!.Write(all);
            _stream.Flush();
        }
    }

    public async Task<byte[]> WaitForAsync(Func<byte[], bool> match, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var found = Messages.FirstOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The client did not send the message.");
    }

    // The messages a client sends: their lengths, so the server can cut the stream up.
    private static void ReadMessages(Stream stream, List<byte[]> into, Lock gate, CancellationToken stop)
    {
        var header = new byte[1];
        while (!stop.IsCancellationRequested && stream.Read(header) == 1)
        {
            byte[] rest;
            switch (header[0])
            {
                case 0: rest = Read(stream, 19); break;
                case 2:
                {
                    var head = Read(stream, 3);
                    rest = [.. head, .. Read(stream, BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1)) * 4)];
                    break;
                }

                case 3: rest = Read(stream, 9); break;
                case 4: rest = Read(stream, 7); break;
                case 5: rest = Read(stream, 5); break;
                case 6:
                {
                    var head = Read(stream, 7);
                    var length = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(3));
                    rest = [.. head, .. Read(stream, Math.Abs(length))];
                    break;
                }

                case 251:
                {
                    var head = Read(stream, 7);
                    rest = [.. head, .. Read(stream, head[5] * 16)];
                    break;
                }

                default: return;
            }

            lock (gate)
            {
                into.Add([header[0], .. rest]);
            }
        }
    }

    private static byte[] Read(Stream stream, int count)
    {
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private void Serve()
    {
        try
        {
            using var client = _listener.AcceptTcpClient();
            client.NoDelay = true;
            Stream stream = client.GetStream();
            stream.Write(Encoding.ASCII.GetBytes(Banner));
            var answer = Encoding.ASCII.GetString(Read(stream, 12));
            var minor = answer.Contains("003.008", StringComparison.Ordinal) ? 8 : answer.Contains("003.007", StringComparison.Ordinal) ? 7 : 3;

            if (Refuse is not null)
            {
                if (minor == 3)
                {
                    stream.Write(new byte[4]);
                }
                else
                {
                    stream.WriteByte(0);
                }

                WriteText(stream, Refuse);
                return;
            }

            if (minor == 3)
            {
                stream.Write([0, 0, 0, Types[0]]);
                ChosenType = Types[0];
            }
            else
            {
                stream.WriteByte((byte)Types.Length);
                stream.Write(Types);
                ChosenType = Read(stream, 1)[0];
            }

            switch (ChosenType)
            {
                case 1:
                    if (minor >= 8)
                    {
                        stream.Write(new byte[4]);
                    }

                    break;
                case 2:
                    if (!VncAuth(stream, minor))
                    {
                        return;
                    }

                    break;
                case 19:
                    if (!VeNCrypt(ref stream, minor))
                    {
                        return;
                    }

                    break;
            }

            Read(stream, 1);                                   // ClientInit
            var init = new byte[24];
            BinaryPrimitives.WriteUInt16BigEndian(init, (ushort)Width);
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), (ushort)Height);
            init[4] = 32;
            init[5] = 24;
            init[7] = 1;
            stream.Write(init.AsSpan(0, 20));                  // size and a pixel format (ours replaces it)
            WriteText(stream, Name);
            lock (_gate)
            {
                _stream = stream;
            }

            _ready.TrySetResult();
            ReadMessages(stream, _messages, _gate, _stop.Token);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException or EndOfStreamException or System.Security.Authentication.AuthenticationException)
        {
            _ready.TrySetException(exception);
        }
    }

    private bool VncAuth(Stream stream, int minor)
    {
        var challenge = RandomNumberGenerator.GetBytes(16);
        stream.Write(challenge);
        var response = Read(stream, 16);
        var accepted = response.AsSpan().SequenceEqual(VncDes.Response(Password, challenge));
        PasswordAccepted = accepted;
        if (accepted)
        {
            stream.Write(new byte[4]);
            return true;
        }

        stream.Write([0, 0, 0, 1]);
        if (minor >= 8)
        {
            WriteText(stream, "Authentication failed");
        }

        return false;
    }

    private bool VeNCrypt(ref Stream stream, int minor)
    {
        stream.Write([0, 2]);
        var version = Read(stream, 2);
        stream.WriteByte(version[0] == 0 && version[1] == 2 ? (byte)0 : (byte)1);
        stream.WriteByte((byte)SubTypes.Length);
        foreach (var subtype in SubTypes)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, subtype);
            stream.Write(bytes);
        }

        ChosenSubType = BinaryPrimitives.ReadInt32BigEndian(Read(stream, 4));
        if (!SubTypes.Contains(ChosenSubType))
        {
            stream.WriteByte(0);
            return false;
        }

        stream.WriteByte(1);
        if (ChosenSubType is >= 257 and <= 262 && Certificate is not null)
        {
            var ssl = new SslStream(stream, false);
            ssl.AuthenticateAsServer(Certificate);
            stream = ssl;
        }

        switch (ChosenSubType)
        {
            case 260:
                stream.Write(new byte[4]);
                return true;
            case 261:
                return VncAuth(stream, minor);
            case 262:
            {
                var lengths = Read(stream, 8);
                PlainUser = Encoding.UTF8.GetString(Read(stream, BinaryPrimitives.ReadInt32BigEndian(lengths)));
                PlainPassword = Encoding.UTF8.GetString(Read(stream, BinaryPrimitives.ReadInt32BigEndian(lengths.AsSpan(4))));
                var ok = PlainPassword == Password;
                PasswordAccepted = ok;
                stream.Write(ok ? new byte[4] : [0, 0, 0, 1]);
                if (!ok && minor >= 8)
                {
                    WriteText(stream, "Wrong user or password");
                }

                return ok;
            }
            default:
                return false;
        }
    }

    private static void WriteText(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_gate)
        {
            _stream?.Dispose();
        }
    }

    // ---- pictures, as bytes ----

    public static byte[] Update(params byte[][] rects)
    {
        var head = new byte[4];
        head[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), (ushort)rects.Length);
        return [.. head, .. rects.SelectMany(r => r)];
    }

    public static byte[] Rect(int x, int y, int w, int h, int encoding, params byte[][] data)
    {
        var head = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(head, (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(4), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(6), (ushort)h);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(8), encoding);
        return [.. head, .. data.SelectMany(d => d)];
    }

    public static byte[] Pixel(byte b, byte g, byte r) => [b, g, r, 0];

    public static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    public static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=vnc-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}
