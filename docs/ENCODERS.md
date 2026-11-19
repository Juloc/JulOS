# Picture encoders: GPU (VAAPI), H.265 and the software fallback

The stream engine (`stream`, see [ARCHITECTURE.md](ARCHITECTURE.md)) turns the pictures of the remote desktop into a video stream for the browser's
`VideoDecoder`. How that stream is coded is behind one interface, `IVideoEncoder` (`JulOS.Integrations/Display/Video`), with these implementations:

| Encoder | Codec | Where | Needs |
| --- | --- | --- | --- |
| `H264Encoder` | H.264 | CPU (libopenh264, BSD) | `libopenh264-7` (in the image). Always the way out. |
| `VaapiEncoder` | H.264 and HEVC (H.265) | GPU, through libva (MIT) | a render node (`/dev/dri/renderD*`) in the container, `libva2`, `libva-drm2` and a driver (`intel-media-va-driver`, all in the image) |

`ScreenStream` only talks to the interface. `EncoderSelector` picks the encoder for every session and **never lets a session fail because of the GPU**:
whatever is missing or throws is skipped (and logged), and the software H.264 encoder takes over. If the GPU gives up in the middle of a session
(driver crash, device gone), that session continues on the software encoder with a key frame, and in `auto` mode the GPU is left alone for the
following sessions until the server restarts.

Nothing GPL is involved: libva, its drivers and libopenh264 are MIT/BSD; there is no x264/x265 and no ffmpeg in the server. HEVC exists only on the
GPU (there is no software HEVC encoder here), so a machine without a GPU always streams H.264.

## Settings (environment of the server container)

| Variable | Values | Meaning |
| --- | --- | --- |
| `JULOS_ENCODER` | `auto` (default), `software`, `vaapi` | `auto`: GPU if it works, else CPU. `software`: never touch the GPU. `vaapi`: the GPU is wanted: it is tried for every session (a failed try is not remembered) and a failure is logged as an error; the session still runs on the CPU. |
| `JULOS_CODEC` | `auto` (default), `h264`, `h265` | `auto` and `h265`: HEVC when the browser and a GPU encoder both can, else H.264. `h264`: never HEVC. |
| `JULOS_VAAPI_DEVICE` | a path, e.g. `/dev/dri/renderD129` | Use this render node instead of the first `renderD*` that works (machines with two GPUs). |
| `LIBVA_DRIVER_NAME` | `iHD`, `i965`, `radeonsi`, ... | Forces libva's driver (normally found by itself). |
| `LIBVA_MESSAGING_LEVEL` | `0`, `1`, `2` | libva's own messages on stderr. The server sets 1 (errors only) unless you set it; `2` also prints which driver file libva loads, useful for a GPU that is not found. |

At start the server logs what it found (`Bildkodierer: ...`), and "Über JulOS" shows the engine ("Stream (GPU (VAAPI: H.264, H.265), sonst H.264 auf der CPU)").
Every session logs `Kodierer: <name> (H.265, GPU), 1920 x 1080` when it starts.

## How H.265 is agreed on

1. The browser asks `VideoDecoder.isConfigSupported` for `avc1.*` and for `hvc1.*` (Annex B, hardware preferred, see `wwwroot/codecs.js`) and puts the result in the
   address of the WebSocket: `.../api/remote/screen?ticket=...&codecs=3` (bit 0 H.264, bit 1 H.265; without it the server assumes H.264).
2. The server chooses HEVC only if the browser can, `JULOS_CODEC` allows it and a GPU encoder for it works; else H.264.
3. Every picture message says in its flags (bit 1) which codec it is. On a key frame the browser reads the codec string from the stream's SPS
   (`avc1.640028`, `hvc1.1.6.L123.90`) and configures the decoder from it; a change of codec reopens the decoder.
4. If the HEVC decoder fails or holds pictures back, the browser sends message 30 (codecs = H.264 only) and a key frame request; the server swaps to H.264
   with a key frame. The browser never falls back to Guacamole because of HEVC.

Messages: server to browser `30` encoder (codec, GPU yes/no, name; for display only), browser to server `30` codecs. Numbers 1 to 16 did not change. See `ScreenProtocol.cs`.

## What the GPU encoder does (and what it does not)

`VaapiEncoder` is a direct use of libva through P/Invoke (no wrapper library). Per picture, synchronously: the changed rectangle of the I420 frame is written
into one NV12 surface (`vaDeriveImage`, else `vaCreateImage` + `vaPutImage`), one picture is encoded and read from the coded buffer.

- **Stream structure:** an IDR picture for the first picture and whenever the browser asks for a key frame; every other picture is a P picture with one
  reference (the picture before), one slice, no B pictures. No periodic key frames (the transport is reliable).
- **Rate control:** constant bit rate at the stream's ceiling (16 Mbit/s), half a second of buffer, **no frame skipping, no bit stuffing** (a still screen is
  not padded up to the rate), quantiser down to 14. A still picture is encoded again as a P picture of the same frame, the way the software encoder does
  to make it sharp; it counts as sharp when such a repeat comes out almost empty (or after 8 repeats).
- **Headers:** SPS, PPS (HEVC: VPS, SPS, PPS) and the slice header are written by the server (`H264Headers.cs`, `HevcHeaders.cs`) and handed to the driver as
  packed headers, as far as the driver supports them (`VAConfigAttribEncPackedHeaders`); otherwise the driver makes them. That way the real picture size is
  cropped out of the macroblock grid, the colour description is the software path's (BT.709 primaries, sRGB transfer, BT.601 matrix) and the VUI says that
  no picture is reordered, so decoders show a picture as soon as they have it. If a key frame comes back without parameter sets, ours are put in front.
- **Profiles:** H.264 High, else Main, else Constrained Baseline (CABAC unless Baseline); HEVC Main (8 bit 4:2:0). Entry point: the full pipeline
  (`EncSlice`) before the low-power one (`EncSliceLP`); only entry points with CBR are used.
- **Not done:** B pictures, multiple references, intra refresh, 10 bit, 4:4:4, SAO, tiles, several slices, dynamic resolution without rebuilding the encoder
  (a resize makes a new encoder, as with the software one). Pictures larger than the driver takes (`VAConfigAttribMaxPictureWidth/Height`) are encoded in
  software.

A picture the GPU cannot take at all is not a failure of the session: a test picture is encoded when the encoder is made, and any error in `Create` or
in a picture moves on to the next profile, then to the software encoder.

## Trying it on the Docker VM (Cubi)

Not verified: whether the VM has a GPU at all. Check on the Docker VM (not in the container):

```bash
ls -l /dev/dri
lspci -nn | grep -iE "vga|display|3d"
```

If `/dev/dri` is missing or shows only `card0` of a virtual display, the GPU is not passed through to the VM (Proxmox: PCI passthrough of the iGPU to the
VM, or run JulOS on the Proxmox host's own Docker instead); no JulOS setting can change that. If there is `renderD128`, look at its group:

```bash
stat -c '%G (gid %g) %a' /dev/dri/renderD128
```

Then give the container the device and that group (numeric id, the container has no `render` group of its own). Add to the `julos` service of the compose file:

```yaml
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "105"            # the numeric gid printed above (an example; yours differs)
    environment:
      JULOS_ENCODER: auto         # or vaapi, to see errors in the log if the GPU is not used
      JULOS_CODEC: auto           # h264 to keep HEVC away
```

(The same lines are in `deploy/gpu/compose.gpu.yaml` as an override file: `docker compose -f docker-compose.yml -f deploy/gpu/compose.gpu.yaml up -d`.)
Rebuild or pull an image that contains the VAAPI packages (they are in the `Dockerfile` now: `libva2`, `libva-drm2`, `intel-media-va-driver`).

Then ask the server what it can do (the exact name of the container is the one in `docker ps`):

```bash
docker exec <julos-container> dotnet JulOS.dll diag encoders
```

It prints the software encoder with a test picture, `/dev/dri` and the permissions of every render node, the driver libva found, every H.264 and HEVC profile
with its entry points, rate controls and packed headers, a test encode (two pictures) per profile, and which encoder a browser would get. Exit code 0 when the
software encoder works (the GPU is a bonus), 1 when not even that does. It reads `JULOS_ENCODER` and `JULOS_CODEC` from the environment like the server.

Typical outcomes and what to do:

| Output | Meaning |
| --- | --- |
| `/dev/dri: FEHLT` or `Kein Render-Knoten` | the container has no device: `devices:` missing, or the VM has no GPU |
| `Zugriff: NEIN (keine Berechtigung ...)` | `group_add` missing or the wrong gid |
| `libva ist nicht installiert` | the image is older than the VAAPI packages |
| `Problem: ...: vaInitialize: ...` | no driver for this GPU (`intel-media-va-driver` handles Intel from Broadwell/Gen8 on; older: build with `VAAPI_PACKAGES="libva2 libva-drm2 i965-va-driver"`; AMD: `mesa-va-drivers`) |
| the profile table has no `CBR` | the driver cannot do constant bit rate for that entry point (low power encoding on some Intel generations needs the HuC firmware to be loaded by the kernel: look at `dmesg | grep -i huc` on the VM) |
| `FEHLER: ...` in the test encode of one profile | the message says which libva call failed; send it with the table |
| all `OK` | `JULOS_ENCODER=auto` now uses the GPU; check the server log for `Kodierer: ... (H.265, GPU)` after connecting |

The non-free variant `intel-media-va-driver-non-free` (Ubuntu multiverse) is not installed by default; it only matters if the table above stays empty on an
Intel GPU of generation 9 or later (build with `VAAPI_PACKAGES="libva2 libva-drm2 intel-media-va-driver-non-free"` and decide yourself whether you accept its licence).

To go back at any time: `JULOS_ENCODER=software` (or remove the device lines). Nothing else changes.

## What is verified, and what is not

Verified without a GPU (the development VM has none):

- The interface and the selection with fake encoders (`EncoderSelectorTests`): GPU used, software fallback for missing, failing and mid-session failing GPU,
  `software`/`vaapi` modes, HEVC only for a browser that can, settings parsing.
- The software path unchanged through the interface: all display tests against xrdp (`deploy/dev/test-display.sh`, including two `ScreenStream` tests with a fake
  GPU that fails in the middle of a session and one that offers HEVC and is told to go back), the backend tests, and the stream in a real browser against the
  test desktop (engine "Stream", picture drawn, the encoder message arrives). Idle click latency measured with `deploy/dev/bench` on a quiet machine (load below 3,
  no video load, 20 clicks each): this branch median 15 ms (p90 17), the unchanged `main` median 16 ms (p90 27).
- `stream.js` against a played browser (`tests/frontend/stream-codecs.test.mjs`): codecs in the address, an hvc1 decoder from the SPS, the fall back to H.264 after
  a failed H.265 decoder.
- Every libva structure in `LibVa.cs` against the sizes and field offsets of the real C headers (libva 2.22, `LayoutTests`, generated by
  `tests/JulOS.Display.Tests/Vaapi/layout.c`), and the constants.
- The headers the GPU encoder writes: a bit reader that follows the syntax reads them back (`HeadersTests`), and **ffmpeg's parser reads them without a
  complaint** (SPS, PPS, VPS and slice headers of all profiles, with made-up slice data behind them; the test runs where ffmpeg exists).
- The codec strings (`CodecStringTests` on the server, `tests/frontend/codecs.test.mjs` in the browser logic, same bytes), the negotiation messages, the old message numbers.
- The diagnostics on a machine without a GPU (prints the reasons and exits 0 when the software encoder works).

**Needs the real GPU (unverified):** that the driver accepts the parameter buffers and packed headers, that the coded buffer comes back as expected, that
the rate control behaves (no stuffing, no skipping, sharp still pictures), that the browser's hardware HEVC decoder is happy with the stream, and the latency and
CPU numbers. The first thing to look at if it does not work is the output of `diag encoders`: it names the failing libva call. One suspect worth knowing: with
packed slice headers the driver has to write the slice QP itself under rate control (`slice_qp_delta` is 0 in our header); if a driver rejects that, the
fix is to leave `PackedHeaderSlice` out of `_packed` in `VaapiEncoder` so the driver makes the slice header.
