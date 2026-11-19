# VNC

A connection of kind **Desktop (VNC)** (stored kind `vnc`) shows a machine that runs a VNC server (RFB, RFC 6143), for example TigerVNC, x11vnc, a macOS screen sharing server or a Raspberry Pi. It runs through the same stream engine as RDP: JulOS has its own RFB client on the server (`src/JulOS.Integrations/Display/Vnc`), the picture goes into the same framebuffer pipeline (dirty rectangles, H.264 encoder, lossless refinement of still parts, the status bar, session keep after a dropped browser connection), so the browser, the protocol (`ScreenProtocol`) and the status bar do not know which of the two they show. Nothing about RDP changed: both clients implement `IScreenSource` and report to `IScreenHandler`.

## What works

| Area | Support |
| --- | --- |
| Protocol versions | RFB 3.3, 3.7 and 3.8 (3.4 to 3.6 count as 3.3, newer ones as 3.8) |
| Sign-in | none, **VNC password**, **VeNCrypt with a certificate** (X509None, X509Vnc, X509Plain) |
| Encodings | Raw, CopyRect, RRE, Hextile, Zlib, ZRLE (asked for in this order of preference, ZRLE first) |
| Pseudo encodings | Cursor (the server's pointer shape, shown by the browser), DesktopSize and ExtendedDesktopSize (desktop size changes), LastRect, Extended Clipboard |
| Size | the picture follows the browser window where the server speaks ExtendedDesktopSize (TigerVNC does, x11vnc and most others do not: the picture is then scaled in the window) |
| Clipboard | text both ways: UTF-8 with the extended clipboard, otherwise Latin-1 (characters beyond it become `?`); up to 1 MiB |
| Keyboard | the browser sends the keysym of the character that was typed (message 18), so the layout of the target does not matter for text; special keys, F keys, modifiers and key combinations (Ctrl+C, Ctrl+Alt+Del from the key bar) work as on a real keyboard. Without the keysym message the server maps scancodes with the German layout (`VncKeys`) |
| Mouse | position, left, middle, right and back; the wheel as buttons 4 and 5 (vertical) and 6 and 7 (horizontal), one click for every 120 units (fine touchpad steps add up) |
| Sessions | like RDP: several at once, replace-only-yourself slots, keep after a dropped connection, certificate remembered on first use |

## Sign-in and its limits

- **Only eight characters count in a VNC password.** That is the protocol (DES with an 8 byte key), not JulOS: a longer password is cut after the eighth character, a shorter one is padded with zero bytes. The sign-in itself is a challenge and response, so the password does not cross the wire, but it is **weak** against guessing and everything that follows (picture, keys, clipboard) is **not encrypted**. Use it only inside a network you trust, or put the VNC server behind SSH or a VPN, or use VeNCrypt with a certificate.
- **A server without sign-in** (security type "none") needs no password: leave the password field empty (the ticket accepts an empty password for VNC only; every other kind still needs one). If the server then asks for one, the session asks in a dialog (a password only, no user or domain).
- **VeNCrypt** (security type 19, version 0.2): TLS, then the sub-type X509None (certificate only), **X509Vnc** (VNC password inside TLS) or **X509Plain** (user name and password inside TLS, the user name field of the connection; the password has no length limit). Everything afterwards is encrypted. The certificate is remembered on first use like the RDP certificate (fingerprint "SHA256:..."); if it changes the connection stops before anything secret is sent and the same warning as for RDP asks you to confirm it.
- **Not supported** (reported by name in the error, never silently ignored): VeNCrypt **without a certificate** (TLSNone, TLSVnc, TLSPlain, anonymous Diffie-Hellman; the TLS of .NET cannot do it, so enable the X509 variants on the server), unencrypted VeNCrypt (Plain), Apple Remote Desktop sign-in (type 30), RSA-AES (types 5, 6), Tight, Ultra and other proprietary security types.
- When a server offers several types JulOS takes the VNC password if you gave one, then VeNCrypt, then none; without a password: none first, then VeNCrypt. A server that offers both a password and VeNCrypt is therefore used with the password; to force TLS turn the plain password off on the server.

## Limits

- No files or images in the clipboard (RDP only): the browser is told so when it connects, a file or image drop answers with a short message.
- No sound, no microphone, no shared drive, no application windows (RDP and agent only).
- One monitor (the whole screen of the server). The picture is encoded as H.264 like RDP, which is 4:2:0; still parts are refined losslessly afterwards, so text stays pixel exact.
- Back is a button (8) but forward cannot be sent: the pointer message has a single byte for buttons.
- Tight (the preferred encoding of many servers) is not implemented; the server then falls back to ZRLE, Hextile or Raw, which costs more bandwidth on a slow link.
- The desktop size must be 1 to 8192 pixels each way.
- A server with a different pixel format is no problem: the client always asks for 32 bits (blue lowest) and the server converts.

## Setting up a target

TigerVNC with its own X server is the best case (it can change the desktop size and has the extended clipboard):

```
sudo apt install tigervnc-standalone-server tigervnc-tools
vncpasswd                                  # the password, only 8 characters count
vncserver :1 -geometry 1280x800 -localhost no
```

For an existing X session `x11vnc -display :0 -rfbauth ~/.vnc/passwd -forever` works (no resizing). For TLS with TigerVNC: `-SecurityTypes X509Vnc -X509Cert cert.pem -X509Key key.pem` (a self-signed certificate is fine, JulOS remembers it).

In JulOS: add a connection, kind "Desktop (VNC)", the address, the port (default 5900; display :1 is 5901) and the password (or none). Quick Connect takes `vnc://host:port`. Like every target it must be allowed by `JULOS_ALLOWED_HOSTS` if that is set; the connection rules and the ticket check are the same as for RDP.

## Where things are

- `Display/Vnc/VncClient*.cs`: handshake and security, the read loop (one thread, blocking reads), input, clipboard; `VncDecoders.cs` the encodings; `VncDes.cs` the DES of the password sign-in (own implementation, .NET's DES is a legacy provider on OpenSSL 3); `VncKeys.cs` keysyms.
- `Display/IScreenSource.cs`: what `ScreenStream` needs from RDP or VNC; `ScreenStream` has a second constructor for VNC and `ScreenTarget.ForVnc`.
- `ScreenProtocol` message 18 (browser to server): `keysym(4) down(1)`, only on VNC sessions; message 14 (limits) is sent as zeros for VNC.
- Database migration `012_connection_vnc.sql` (the kind check of `connections`).

## Tests

- Unit tests (no network needed beyond loopback) in `tests/JulOS.Display.Tests/Vnc`: DES against the published test vectors and OpenSSL, every decoder with hand built rectangles (also damaged and hostile ones), the client against a small in-process server (versions 3.3, 3.7, 3.8, password right and wrong, VeNCrypt with a real TLS handshake, pictures, pointer, size, clipboard, mouse, wheel and keys), keysyms.
- `deploy/dev/test-vnc.sh` starts two real TigerVNC servers (image `deploy/dev/vnc-target`: Xvnc, openbox, xterm; one with the password, one with VeNCrypt and a certificate) in their own container network, runs the tests against them (every encoding, TLS and the fingerprint check, clicks, wheel and keys that a window in the desktop logs, clipboard in both directions with characters beyond Latin-1, desktop resize, and the whole way through `ScreenStream` to H.264) and removes containers and network afterwards. It does not touch the test instance.
