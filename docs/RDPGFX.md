# RDPGFX: passing the server's own H.264 on

Normally the stream engine (`ScreenStream`, see [ARCHITECTURE.md](ARCHITECTURE.md)) takes the bitmaps and drawing orders of the RDP server, keeps the
desktop in a framebuffer and encodes it again for the browser ([ENCODERS.md](ENCODERS.md)). Some RDP servers can code the desktop as H.264 themselves
and send it through the **graphics pipeline extension** (RDPGFX, MS-RDPEGFX). With this option the stream engine takes that H.264, puts it into the
browser's `VideoDecoder` as it is, and does not encode anything: less CPU on the JulOS server, one coding step less (so no second loss of quality) and
the delay of the encoder is gone.

**It is off by default** and optional. It helps only if the RDP server really delivers H.264 through the graphics pipeline, and the common one does
not (see "Limits"). Whatever goes wrong, the session falls back to the engine's own encoder.

## Switch

| Variable | Values | Meaning |
| --- | --- | --- |
| `JULOS_RDP_GFX` | unset (default) or anything else: off; `on` (also `1`, `true`, `yes`): on | Offer the graphics pipeline with H.264 to the RDP server in every stream session. |

The variable is read when the server starts. In the dev stack `DEV_RDP_GFX=on` in the environment of `deploy/dev/up.sh` sets it. The same variable also
switches the graphics pipeline of the Guacamole engine on (`guacd`, only the value `on`); the two do not depend on each other.

Where the setting is not on, the RDP client does not even announce the pipeline and behaves exactly as before.

## How it works

1. With the option on, the client tells the server in its connect data that it supports the graphics pipeline and registers the dynamic channel
   `Microsoft::Windows::RDS::Graphics` (`GfxChannel`, next to display control and microphone on the `drdynvc` channel).
2. A server that has the pipeline opens the channel. The client advertises **one** capability set: version 8.1 with H.264 (AVC420) enabled. Older sets
   are not offered on purpose: a server that falls back to one would send pictures that cannot be passed on. The server confirms a set, resets the
   graphics (desktop size), creates a surface of the desktop's size and maps it to the origin.
3. The server sends frames: "start frame", one or more "wire to surface" messages with an AVC420 picture (a list of rectangles, quantiser values and the
   H.264 bitstream in Annex B form) and "end frame". `GfxChannel` takes the bitstream out (it is the picture of the whole surface, the rectangles only say
   which parts changed) and checks whether it holds an IDR slice (a key frame).
4. `ScreenStream` waits for the first key frame. With it the session **switches**: the own encoder and the lossless refinement stop, the browser is told
   the size and the encoder name "Server (RDPGFX)" (message 30; the numbers in the app then show it), and every picture of the server goes out as a
   normal picture message (message 1) in the order it came. The browser does not notice a difference: it reads the codec from the SPS of the key frame.
   Until then (and always, if the server never opens the channel) the session runs as before, so a server without the pipeline is not slowed down.
5. **Acknowledgements:** the server may only send further frames when the client has acknowledged earlier ones. The channel does *not* acknowledge
   when the picture arrives but when the browser says (message 1 of the browser) that it has drawn it. So the browser sets the pace, as it does for the
   own encoder, and a slow link or phone slows the server down instead of piling pictures up. Frames that contain nothing for the screen are acknowledged
   at once.
6. A new desktop size (the window changed, display control) comes as a new reset and a new surface; the browser gets the new size and the following key
   frame from the server.

## When the session gives it up

The pipeline can carry only what is described above. Anything else ends it, and `ScreenStream` then **reconnects the same RDP session without the
pipeline** (the session on the server is kept; the browser sees the size message and a key frame of the own encoder). Reasons, all logged as
`RDPGFX: ...; die Sitzung verbindet neu und kodiert selbst`:

- the server chose a capability set without H.264, or sends pictures in another codec (planar, progressive, AVC444, ...);
- the server paints into the surface (solid fill, copies, cache) instead of only sending pictures, or sends H.264 for a surface that is not the desktop,
  or the desktop is more than one picture (a surface not at 0,0);
- the server compresses its messages with the RDP 8 bulk compression ("ZGFX", which Windows servers do): not unpacked, see "Limits";
- the browser asks for a key frame (decoder error, lost thread, reopened decoder): only the own encoder can make one on demand, the RDP server has no
  such request;
- the server sends no key frame for 4 s although pictures arrive, the browser acknowledges nothing for 5 s while pictures wait, or more than 45 pictures
  wait for the browser (they cannot be dropped, every one depends on the one before; on a slow link the own encoder with its pacing is the better way).

If the reason is that the *server* does not deliver usable H.264 (the first two), the JulOS server remembers the host and port for 30 minutes and does not
offer the pipeline to it again until then (or until it restarts): otherwise every session would pay a failed first connection. This memory is only in the
process.

## Limits (what this is not)

- **The common server does not deliver H.264 this way.** `xrdp` 0.10 from the Debian 13 package, which is the Linux VM and the dev container, opens the
  pipeline, confirms 8.1 with H.264 and then sends the desktop as planar tiles (its log says "starting gfx rfx pro codec session"): the package is built
  without x264. With it the option only costs one extra connection per half hour. It pays off with an xrdp built with `--enable-x264` (and its
  `gfx.toml`), or a Windows server (see the next point).
- **Windows servers are not verified here and probably do not work yet.** They compress their pipeline messages with ZGFX; the client does not unpack them
  and gives up on the first one. (A decompressor is in [IDEAS.md](IDEAS.md).)
- **AVC420 only**, no AVC444 (4:4:4 would be sharper, but needs another decoder in the browser), and no other codec. One surface, no multi-monitor.
- **No key frame on demand.** A reconnect in the same session stands in for it (see above), which takes about as long as the first connection.
- **No lossless refinement** (`Display/Refine`): the engine has no framebuffer to take the tiles from while the pictures come from the server. Text is as sharp as
  the server's encoder (and 4:2:0) makes it.
- **The browser's pause** (background session) does not stop the server here: the pictures keep coming, because the browser could not resume
  without a key frame. Only a session that has not switched yet (own encoder) stops encoding while paused.
- **The bit rate and the quality are the server's**: JulOS sets none of them, and the link pacer does not apply (the acknowledgements still pace the server).
- Pointer, clipboard, sound and keyboard are untouched: they do not go through the pipeline.

## How it was tested

- **Unit tests** (`tests/JulOS.Display.Tests`, no server needed): `GfxPduTests` (reading and building the messages from the specification's tables,
  containers, rubbish), `GfxChannelTests` (the channel as a state machine over a server's messages, including bytes captured from a real xrdp 0.10.1,
  frames of many pieces through the dynamic channel, every reason to give up, random garbage), `GfxPassthroughTests` (the switch in `ScreenStream`, the
  order of pictures, the acknowledgements following the browser, key frame request, new size).
- **Against the xrdp of the dev stack** (`deploy/dev/test-display.sh`, as for the other display tests): `GfxStreamTests` checks that a server which offers
  the pipeline but sends planar makes the session fall back to its own encoder and still deliver a picture, and that the host is remembered. This ran
  against xrdp 0.10.1 and showed that exactly: pipeline negotiated, planar tiles, reconnect, picture.
- **Against a server that really sends H.264** (`JULOS_TEST_RDP_GFX_HOST`, the second test of `GfxStreamTests`, needs libopenh264 as the other display tests):
  skipped unless that variable names a server with user `ubuntu` / `ubuntu` that delivers AVC420 (then also set `JULOS_TEST_RDP_GFX_H264=1`, which skips the
  fallback test above); **not run for this change** (no such server was
  available: the dev container's xrdp has no x264, and there is no Windows server). So the passing on of real H.264 to a real browser decoder is
  **not verified**; the unit tests feed hand-built (not decodable) bitstreams with the right NAL units.

## Trying it

Needs a server that delivers H.264 through RDPGFX (see "Limits"). With the dev stack:

```bash
DEV_RDP_GFX=on ./deploy/dev/up.sh
```

then open a stream connection to that server. In the server log `RDPGFX: die H.264-Bilder des Servers werden durchgereicht` says it works (with debug
logging `RDPGFX: ...` lines show the negotiation), and the picture statistics in the browser show the encoder `Server (RDPGFX)`. If the log says
`RDPGFX: ...; die Sitzung verbindet neu`, the reason follows.

For the real-server test, point `JULOS_TEST_RDP_GFX_HOST` at the server and run `deploy/dev/test-display.sh --filter-class "*GfxStreamTests"` from a
machine whose container network reaches it.
