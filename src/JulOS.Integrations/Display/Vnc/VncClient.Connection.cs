using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using JulOS.Integrations.Display.Rdp;

namespace JulOS.Integrations.Display.Vnc;

// The way in: version, security (none, password, VeNCrypt over TLS with a certificate), ClientInit and ServerInit.
public sealed partial class VncClient
{
    internal const int SecurityNone = 1, SecurityVnc = 2, SecurityVeNCrypt = 19;
    internal const int VeNCryptX509None = 260, VeNCryptX509Vnc = 261, VeNCryptX509Plain = 262;

    private const int MaxSide = 8192;

    // Connects and signs in; returns when the server has told how large its desktop is and the first picture has been asked for.
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        limit.CancelAfter(_options.ConnectTimeout);
        try
        {
            _tcp = new System.Net.Sockets.TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(_options.Host, _options.Port, limit.Token);
            _stream = _tcp.GetStream();
            _reader = new RfbReader(_stream);
            // The handshake reads blocking; the registration closes the socket when the time is up or the caller gives up.
            using var abort = limit.Token.Register(Abort);
            await Task.Run(() => Handshake(limit.Token), CancellationToken.None);
            _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
            SendSetup();
        }
        catch (Exception exception) when (exception is not RdpException)
        {
            throw Explain(exception, cancellationToken);
        }
    }

    private void Handshake(CancellationToken cancellationToken)
    {
        var stream = _stream!;
        var reader = _reader!;
        var banner = reader.Bytes(12);
        var minor = ParseVersion(banner);
        ProtocolVersion = "3." + minor;
        stream.Write(Encoding.ASCII.GetBytes($"RFB 003.00{minor}\n"));
        stream.Flush();

        var types = ReadSecurityTypes(reader, minor);
        var type = ChooseSecurity(types, _options.Password.Length > 0);
        if (minor >= 7)
        {
            stream.WriteByte((byte)type);
            stream.Flush();
        }

        SecurityType = type;
        Authenticate(type, minor, cancellationToken);

        // ClientInit: whether the desktop may be shared with other viewers
        _stream!.WriteByte(_options.Exclusive ? (byte)0 : (byte)1);
        _stream.Flush();

        // ServerInit: size, the server's pixel format (replaced by ours right away), name
        var width = _reader!.U16();
        var height = _reader.U16();
        _reader.Skip(16);
        DesktopName = _reader.Text(256);
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide)
        {
            throw new RdpException($"The VNC desktop is {width} x {height}, which JulOS does not take.");
        }

        Allocate(width, height);
        _log?.Invoke($"VNC {ProtocolVersion}, security {type}/{SecuritySubType}, desktop \"{DesktopName}\" {width} x {height}");
    }

    // "RFB 003.008\n" -> the minor version to use: 8, 7 or 3 (versions in between, 3.4 to 3.6, are 3.3 by the specification; newer ones are 3.8).
    internal static int ParseVersion(ReadOnlySpan<byte> banner)
    {
        if (banner.Length != 12 || !banner[..4].SequenceEqual("RFB "u8) || banner[7] != (byte)'.' || banner[11] != (byte)'\n'
            || !int.TryParse(Encoding.ASCII.GetString(banner.Slice(4, 3)), out var major) || !int.TryParse(Encoding.ASCII.GetString(banner.Slice(8, 3)), out var minor))
        {
            throw new RdpException("The server did not answer like a VNC server.");
        }

        if (major < 3)
        {
            throw new RdpException("The VNC server is too old (protocol version " + major + "." + minor + ").");
        }

        if (major > 3 || minor >= 8)
        {
            return 8;
        }

        return minor == 7 ? 7 : 3;
    }

    private static byte[] ReadSecurityTypes(RfbReader reader, int minor)
    {
        if (minor == 3)
        {
            var single = reader.U32();
            if (single == 0)
            {
                throw new RdpException("The VNC server refused the connection: " + reader.Text());
            }

            return single > 255 ? [] : [(byte)single];
        }

        var count = reader.U8();
        if (count == 0)
        {
            throw new RdpException("The VNC server refused the connection: " + reader.Text());
        }

        return reader.Bytes(count);
    }

    // The most wanted of what the server offers: with a password the classic sign-in, then VeNCrypt (TLS), then none; without one none first.
    // A server that offers only what this client cannot do (RSA-AES, Apple's sign-in, ...) gets a message that names the types.
    internal static int ChooseSecurity(ReadOnlySpan<byte> offered, bool hasPassword)
    {
        var order = hasPassword ? new[] { SecurityVnc, SecurityVeNCrypt, SecurityNone } : new[] { SecurityNone, SecurityVeNCrypt };
        foreach (var wanted in order)
        {
            if (offered.Contains((byte)wanted))
            {
                return wanted;
            }
        }

        if (!hasPassword && offered.Contains((byte)SecurityVnc))
        {
            throw new RdpException("The VNC server asks for a password.", true) { IsCredentialsMissing = true };
        }

        throw new RdpException("The VNC server offers only sign-in types JulOS does not support (type " + string.Join(", ", offered.ToArray()) + "). Supported: no sign-in, VNC password, VeNCrypt with a certificate.");
    }

    private void Authenticate(int type, int minor, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case SecurityNone:
                // 3.8 answers with a result even for "none"; before that the session simply begins
                if (minor >= 8)
                {
                    ReadSecurityResult(minor);
                }

                return;
            case SecurityVnc:
                AnswerChallenge();
                ReadSecurityResult(minor);
                return;
            case SecurityVeNCrypt:
                VeNCrypt(minor, cancellationToken);
                return;
            default:
                throw new RdpException("The VNC security type " + type + " is not supported.");
        }
    }

    // The classic sign-in: 16 random bytes come, the DES of them with the password as the key goes back.
    private void AnswerChallenge()
    {
        var challenge = _reader!.Bytes(16);
        _stream!.Write(VncDes.Response(_options.Password, challenge));
        _stream.Flush();
    }

    private void ReadSecurityResult(int minor)
    {
        var result = _reader!.U32();
        if (result == 0)
        {
            return;
        }

        var reason = minor >= 8 ? _reader.Text() : "";
        throw new RdpException(reason.Length > 0 ? "The VNC server did not accept the sign-in: " + reason : result == 2 ? "The VNC server turned the sign-in away (too many failed tries)." : "The VNC server did not accept the password.", true);
    }

    // VeNCrypt 0.2: both sides name a sub-type; with a certificate the connection is wrapped in TLS before anything secret goes over it.
    // TLS without a certificate (anonymous Diffie-Hellman) cannot be done with the TLS of .NET and is reported as not supported.
    private void VeNCrypt(int minor, CancellationToken cancellationToken)
    {
        var stream = _stream!;
        var reader = _reader!;
        var major = reader.U8();
        var minorVersion = reader.U8();
        if (major == 0 && minorVersion < 2)
        {
            throw new RdpException("The VNC server speaks an old VeNCrypt (0." + minorVersion + "), which is not supported.");
        }

        stream.Write([0, 2]);
        stream.Flush();
        if (reader.U8() != 0)
        {
            throw new RdpException("The VNC server did not accept VeNCrypt 0.2.");
        }

        var count = reader.U8();
        var subtypes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            subtypes.Add(reader.I32());
        }

        var subtype = ChooseVeNCrypt(subtypes, _options.Password.Length > 0, _options.UserName.Length > 0);
        SecuritySubType = subtype;
        var choice = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(choice, subtype);
        stream.Write(choice);
        stream.Flush();
        if (reader.U8() != 1)
        {
            throw new RdpException("The VNC server did not accept the VeNCrypt sign-in type.");
        }

        // Nothing may be waiting in the reader: the next bytes of the server are TLS
        if (reader.Buffered != 0)
        {
            throw new VncProtocolException("data before the TLS handshake");
        }

        var ssl = new SslStream(stream, false, (_, certificate, _, errors) =>
        {
            if (_options.CertificateCheck is { } check)
            {
                if (certificate is null)
                {
                    _certificateRefused = true;
                    return false;
                }

                _presentedFingerprint = CertificateFingerprint.Of(certificate);
                if (check(_presentedFingerprint))
                {
                    return true;
                }

                _certificateDeclined = true;
                return false;
            }

            if (_options.IgnoreCertificate || errors == SslPolicyErrors.None)
            {
                return true;
            }

            _certificateRefused = true;
            return false;
        });
        _stream = ssl;
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = _options.Host,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });
        _reader = new RfbReader(ssl);

        switch (subtype)
        {
            case VeNCryptX509None:
                break;
            case VeNCryptX509Vnc:
                AnswerChallenge();
                break;
            default:
            {
                // Plain: user name and password, each with its length in front (over TLS)
                var user = Encoding.UTF8.GetBytes(_options.UserName);
                var password = Encoding.UTF8.GetBytes(_options.Password);
                var message = new byte[8 + user.Length + password.Length];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(message, user.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), password.Length);
                user.CopyTo(message, 8);
                password.CopyTo(message, 8 + user.Length);
                ssl.Write(message);
                ssl.Flush();
                break;
            }
        }

        ReadSecurityResult(minor);
    }

    internal static int ChooseVeNCrypt(IReadOnlyList<int> offered, bool hasPassword, bool hasUser)
    {
        if (hasPassword && hasUser && offered.Contains(VeNCryptX509Plain))
        {
            return VeNCryptX509Plain;
        }

        if (hasPassword && offered.Contains(VeNCryptX509Vnc))
        {
            return VeNCryptX509Vnc;
        }

        if (hasPassword && offered.Contains(VeNCryptX509Plain))
        {
            return VeNCryptX509Plain;
        }

        if (offered.Contains(VeNCryptX509None))
        {
            return VeNCryptX509None;
        }

        if (!hasPassword && (offered.Contains(VeNCryptX509Vnc) || offered.Contains(VeNCryptX509Plain)))
        {
            throw new RdpException("The VNC server asks for a password.", true) { IsCredentialsMissing = true };
        }

        throw new RdpException("The VNC server offers VeNCrypt only without a certificate (types " + string.Join(", ", offered) + "), which JulOS does not support. Turn on the X509 variants (X509Vnc or X509Plain) on the server, or use the plain VNC password.");
    }

    private void Allocate(int width, int height)
    {
        lock (FrameLock)
        {
            Width = width;
            Height = height;
            Framebuffer = new byte[checked(width * height * 4)];
        }

        _back = new RfbCanvas(width, height);
    }

    // SetPixelFormat (32 bits a pixel, 24 of colour, blue in the lowest byte, little endian: the framebuffer's BGRX), SetEncodings, and the first picture.
    private void SendSetup()
    {
        var format = new byte[20];
        format[0] = 0;                       // SetPixelFormat
        format[4] = 32;                      // bits per pixel
        format[5] = 24;                      // depth
        format[6] = 0;                       // little endian
        format[7] = 1;                       // true colour
        format[8] = 0; format[9] = 255;      // red max
        format[10] = 0; format[11] = 255;    // green max
        format[12] = 0; format[13] = 255;    // blue max
        format[14] = 16;                     // red shift
        format[15] = 8;                      // green shift
        format[16] = 0;                      // blue shift
        Send(format);

        var encodings = _options.Encodings ?? DefaultEncodings;
        var message = new byte[4 + encodings.Length * 4];
        message[0] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)encodings.Length);
        for (var i = 0; i < encodings.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4 + i * 4), encodings[i]);
        }

        Send(message);
        RequestUpdate(false);
    }

    internal const int EncodingCursor = -239, EncodingDesktopSize = -223, EncodingExtendedDesktopSize = -308, EncodingLastRect = -224;
    internal const int EncodingExtendedClipboard = unchecked((int)0xC0A1E5CE);

    // What is asked for, most wanted first. Tight is not among them (it needs JPEG and four zlib streams for little gain on a fast link).
    internal static readonly int[] DefaultEncodings =
    [
        VncDecoders.Zrle, VncDecoders.Hextile, VncDecoders.Zlib, VncDecoders.Rre, VncDecoders.CopyRect, VncDecoders.Raw,
        EncodingCursor, EncodingExtendedDesktopSize, EncodingDesktopSize, EncodingLastRect, EncodingExtendedClipboard,
    ];

    private void RequestUpdate(bool incremental)
    {
        var request = new byte[10];
        request[0] = 3;
        request[1] = incremental ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)Width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), (ushort)Height);
        Send(request);
    }
}
