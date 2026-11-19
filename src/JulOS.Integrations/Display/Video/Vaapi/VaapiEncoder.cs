using System.Runtime.InteropServices;

namespace JulOS.Integrations.Display.Video.Vaapi;

// H.264 and HEVC on a GPU through libva: Intel (QuickSync, driver iHD or i965) or anything else with a VAAPI encoder. One class for both
// codecs; what differs is the three parameter buffers and the packed headers, which come from H264Layout and HevcLayout.
//
// What it does, per picture, synchronously (nothing is queued, so the picture is out as soon as the GPU is done):
//   1. uploads what changed in the I420 frame into one NV12 surface (only the changed rectangle),
//   2. begins a picture on that surface, renders sequence parameters (key frames), rate control (key frames, or when the bit rate changed),
//      picture and slice parameters, with the packed headers the driver supports, ends the picture,
//   3. waits for the surface, maps the coded buffer and copies the bytes out.
// Key frames are IDR pictures made when the caller asks and for the first picture; every other picture is a P picture that refers to the one
// before. One slice, one reference, no B pictures, constant bit rate with no frame skipping and no stuffing.
//
// A picture that stands still is coded again as a P picture of the same frame: the rate control spends what is left on it. IsSharp is true when
// such a repeat came out as almost nothing.
internal sealed unsafe class VaapiEncoder : IVideoEncoder
{
    private const int SurfaceCount = 3;              // 0: the input, 1 and 2: the reconstructed pictures, one is the reference of the next
    private const int SkipBytes = 160;               // a repeat this small changed nothing
    private const int MaxRepeats = 8;                // after this many repeats the picture is as sharp as it gets

    private readonly VaDevice _device;
    private readonly nint _dpy;
    private readonly VaEncodeCaps _caps;
    private readonly int _framesPerSecond;
    private readonly H264Layout? _h264;
    private readonly HevcLayout? _hevc;
    private readonly uint _packed;                   // the packed headers the driver was asked for and gets from us
    private readonly uint[] _surfaces = new uint[SurfaceCount];
    private uint _config = LibVa.InvalidId;
    private uint _context = LibVa.InvalidId;
    private uint _coded = LibVa.InvalidId;
    private uint _stagingImage = LibVa.InvalidId;
    private readonly List<uint> _transient = [];
    private byte[] _output = new byte[256 * 1024];
    private int _kilobits;
    private bool _rateDirty;
    private bool _disposed;
    private bool _surfacesCreated;

    // The stream's state
    private long _frames;
    private int _frameNum;
    private int _poc;
    private int _idrPicId = -1;
    private int _current;                            // which reconstructed surface this picture is written to (1 or 2)
    private uint _reference = LibVa.InvalidSurface;
    private int _referenceFrameNum;
    private int _referencePoc;

    // Upload and sharpness
    private I420Frame? _lastFrame;
    private long _lastVersion;
    private int _repeats;
    private int _lastBytes = int.MaxValue;

    private VaapiEncoder(VaDevice device, VaEncodeCaps caps, EncoderRequest request, VideoCodec codec)
    {
        _device = device;
        _dpy = device.Display;
        _caps = caps;
        Codec = codec;
        Width = request.Width;
        Height = request.Height;
        _framesPerSecond = request.FramesPerSecond;
        _kilobits = request.KilobitsPerSecond;
        if (codec == VideoCodec.H264)
        {
            _h264 = new H264Layout(Width, Height, caps.Profile, H264Layout.PickLevel(Width, Height, _framesPerSecond));
            AlignedWidth = _h264.AlignedWidth;
            AlignedHeight = _h264.AlignedHeight;
        }
        else
        {
            _hevc = HevcLayoutFor(caps, Width, Height, _framesPerSecond);
            AlignedWidth = _hevc.AlignedWidth;
            AlignedHeight = _hevc.AlignedHeight;
        }

        // The packed headers we write ourselves, as far as the driver takes them; the driver makes the rest.
        _packed = caps.PackedHeaders & (LibVa.PackedHeaderSequence | LibVa.PackedHeaderPicture | LibVa.PackedHeaderSlice);
        Name = $"VAAPI {device.Vendor} ({caps.ProfileName}, {caps.EntrypointName})".Replace("  ", " ");
    }

    public VideoCodec Codec { get; }

    public string Name { get; }

    public bool IsHardware => true;

    public int Width { get; }

    public int Height { get; }

    public int AlignedWidth { get; }

    public int AlignedHeight { get; }

    public int KilobitsPerSecond => _kilobits;

    public bool IsSharp => _repeats >= MaxRepeats || (_repeats >= 1 && _lastBytes <= SkipBytes);

    // The GPU's own rate control keeps the bit rate; nothing is asked of the caller.
    public int SuggestedInterval(long changedPixels) => 0;

    public bool TrySetBitrate(int kilobitsPerSecond)
    {
        _kilobits = Math.Max(1, kilobitsPerSecond);
        _rateDirty = true;
        return true;
    }

    private static HevcLayout HevcLayoutFor(VaEncodeCaps caps, int width, int height, int fps)
    {
        var level = HevcLayout.PickLevel(width, height, fps);
        var layout = new HevcLayout(width, height, level);
        if (caps.HevcBlockSizes == 0)
        {
            return layout;
        }

        // VAConfigAttribEncHEVCBlockSizes: two bits each, lowest first - log2_max_coding_tree_block_size_minus3, log2_min_coding_tree_block_size_minus3,
        // log2_min_luma_coding_block_size_minus3, log2_max_luma_transform_block_size_minus2, log2_min_luma_transform_block_size_minus2,
        // max_max_transform_hierarchy_depth_inter, min_max_transform_hierarchy_depth_inter, max_max_transform_hierarchy_depth_intra, min_...
        var v = caps.HevcBlockSizes;
        int Field(int i) => (int)((v >> (2 * i)) & 3);
        var ctbMax = Field(0) + 3;
        var ctbMin = Field(1) + 3;
        var cbMin = Field(2) + 3;
        var tbMax = Field(3) + 2;
        var tbMin = Field(4) + 2;
        var ctb = Math.Clamp(5, ctbMin, ctbMax);
        var minCb = Math.Max(3, cbMin);
        return layout with
        {
            CtbLog2 = Math.Max(ctb, minCb),
            MinCbLog2 = minCb,
            MinTbLog2 = Math.Max(2, tbMin),
            MaxTbLog2 = Math.Min(Math.Max(ctb, minCb), Math.Max(tbMax, Math.Max(2, tbMin))),
            DepthInter = Math.Clamp(2, Field(6), Field(5)),
            DepthIntra = Math.Clamp(2, Field(8), Field(7)),
        };
    }

    /// <summary>Opens the device and builds the encoder, and makes sure it works with one test picture. Throws <see cref="VaapiException"/> otherwise.</summary>
    public static VaapiEncoder Create(string devicePath, VaEncodeCaps caps, EncoderRequest request, VideoCodec codec, bool testPicture = true)
    {
        if (request.Width > caps.MaxWidth || request.Height > caps.MaxHeight)
        {
            throw new VaapiException($"{request.Width} x {request.Height} is larger than the driver takes ({caps.MaxWidth} x {caps.MaxHeight}).");
        }

        var device = VaDevice.Open(devicePath, out var problem) ?? throw new VaapiException(problem ?? "the device cannot be opened");
        VaapiEncoder? encoder = null;
        try
        {
            encoder = new VaapiEncoder(device, caps, request, codec);
            encoder.Setup();
            if (testPicture)
            {
                encoder.TestPicture();
            }

            return encoder;
        }
        catch
        {
            if (encoder is not null)
            {
                encoder.Dispose();
            }
            else
            {
                device.Dispose();
            }

            throw;
        }
    }

    private void Setup()
    {
        var attributes = stackalloc VAConfigAttrib[3];
        attributes[0] = new VAConfigAttrib { type = LibVa.AttribRtFormat, value = LibVa.RtFormatYuv420 };
        attributes[1] = new VAConfigAttrib { type = LibVa.AttribRateControl, value = LibVa.RcCbr };
        attributes[2] = new VAConfigAttrib { type = LibVa.AttribEncPackedHeaders, value = _packed };
        LibVa.Check(LibVa.vaCreateConfig(_dpy, _caps.Profile, _caps.Entrypoint, attributes, 3, out _config), "vaCreateConfig");

        fixed (uint* surfaces = _surfaces)
        {
            LibVa.Check(LibVa.vaCreateSurfaces(_dpy, LibVa.RtFormatYuv420, (uint)AlignedWidth, (uint)AlignedHeight, surfaces, SurfaceCount, null, 0), "vaCreateSurfaces");
            _surfacesCreated = true;
            LibVa.Check(LibVa.vaCreateContext(_dpy, _config, AlignedWidth, AlignedHeight, (int)LibVa.Progressive, surfaces, SurfaceCount, out _context), "vaCreateContext");
        }

        var size = (uint)Math.Max(256 * 1024, AlignedWidth * AlignedHeight * 3 / 2);
        LibVa.Check(LibVa.vaCreateBuffer(_dpy, _context, LibVa.BufferCoded, size, 1, null, out _coded), "vaCreateBuffer (coded)");
        _current = 1;

        // The input surface: black, and also the padding beyond the picture where the aligned size is larger.
        FillBlack();
    }

    // One picture of black to see that the whole path works (a driver that takes the config can still fail at the first picture), then back to the start.
    private void TestPicture()
    {
        using var frame = new I420Frame(Width, Height);
        frame.MarkChanged();
        var data = Encode(frame, true, out var key);
        if (data.IsEmpty || !key)
        {
            throw new VaapiException("the test picture came out empty");
        }

        ResetStream();
    }

    private void ResetStream()
    {
        _frames = 0;
        _reference = LibVa.InvalidSurface;
        _lastFrame = null;
        _repeats = 0;
        _lastBytes = int.MaxValue;
        _current = 1;
    }

    public ReadOnlyMemory<byte> Encode(I420Frame frame, bool forceKey, out bool isKey)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(frame.IsDisposed, frame);
        if (frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException($"The picture is {frame.Width} x {frame.Height}, the encoder was made for {Width} x {Height}.", nameof(frame));
        }

        var idr = forceKey || _frames == 0 || _reference == LibVa.InvalidSurface;
        var known = ReferenceEquals(frame, _lastFrame);
        var unchanged = known && frame.Version == _lastVersion;
        if (frame.TakeChanged(known ? _lastVersion : -1, out var left, out var top, out var right, out var bottom) || !known)
        {
            if (!known)
            {
                (left, top, right, bottom) = (0, 0, Width, Height);
            }

            Upload(frame, left, top, right, bottom);
        }

        _lastFrame = frame;
        _lastVersion = frame.Version;
        _repeats = idr || !unchanged ? 0 : _repeats + 1;

        try
        {
            var data = EncodePicture(idr);
            _lastBytes = data.Length;
            isKey = idr;
            return data;
        }
        finally
        {
            DestroyTransient();
        }
    }

    private ReadOnlyMemory<byte> EncodePicture(bool idr)
    {
        // Numbers of this picture
        if (idr)
        {
            _frameNum = 0;
            _poc = 0;
            _idrPicId = (_idrPicId + 1) & 0xFFFF;
        }
        else
        {
            _frameNum = (_referenceFrameNum + 1) & ((1 << H264Layout.FrameNumBits) - 1);
            _poc = _referencePoc + (Codec == VideoCodec.H264 ? 2 : 1);
        }

        var target = _surfaces[_current];
        var sync = _surfaces[0];
        LibVa.Check(LibVa.vaBeginPicture(_dpy, _context, sync), "vaBeginPicture");
        var open = true;
        try
        {
            if (idr)
            {
                RenderSequence();
                RenderRate();
                _rateDirty = false;
            }
            else if (_rateDirty)
            {
                RenderRate();
                _rateDirty = false;
            }

            if (Codec == VideoCodec.H264)
            {
                RenderH264Picture(idr, target);
            }
            else
            {
                RenderHevcPicture(idr, target);
            }

            open = false;
            LibVa.Check(LibVa.vaEndPicture(_dpy, _context), "vaEndPicture");
        }
        finally
        {
            if (open)
            {
                LibVa.vaEndPicture(_dpy, _context);
            }
        }

        LibVa.Check(LibVa.vaSyncSurface(_dpy, sync), "vaSyncSurface");
        var length = ReadCoded();
        var result = Finish(length, idr);

        _reference = target;
        _referenceFrameNum = _frameNum;
        _referencePoc = _poc;
        _current = _current == 1 ? 2 : 1;
        _frames++;
        return result;
    }

    // ---- parameter buffers ----

    private void RenderSequence()
    {
        if (_h264 is { } h)
        {
            var seq = new VAEncSequenceParameterBufferH264
            {
                seq_parameter_set_id = 0,
                level_idc = (byte)h.Level,
                intra_period = 0,
                intra_idr_period = 0,
                ip_period = 1,
                bits_per_second = (uint)_kilobits * 1000,
                max_num_ref_frames = 1,
                picture_width_in_mbs = (ushort)h.MbWidth,
                picture_height_in_mbs = (ushort)h.MbHeight,
                seq_fields = new BitPacker().Add(2, 1u).Add(1, true).Add(1, false).Add(1, false).Add(1, true)
                    .Add(4, (uint)(H264Layout.FrameNumBits - 4)).Add(2, 0u).Add(4, (uint)(H264Layout.PocBits - 4)).Add(1, false).Value,
                frame_cropping_flag = (byte)(h.AlignedWidth != Width || h.AlignedHeight != Height ? 1 : 0),
                frame_crop_right_offset = (uint)((h.AlignedWidth - Width) / 2),
                frame_crop_bottom_offset = (uint)((h.AlignedHeight - Height) / 2),
            };
            Render(LibVa.BufferSequence, &seq, (uint)sizeof(VAEncSequenceParameterBufferH264));
            if ((_packed & LibVa.PackedHeaderSequence) != 0)
            {
                RenderPacked(LibVa.PackedSequence, h.Sps(false));
                if ((_packed & LibVa.PackedHeaderPicture) != 0)
                {
                    RenderPacked(LibVa.PackedPicture, h.Pps(false));
                }
            }

            return;
        }

        var x = _hevc!;
        var hevc = new VAEncSequenceParameterBufferHEVC
        {
            general_profile_idc = 1,
            general_level_idc = (byte)x.Level,
            general_tier_flag = 0,
            intra_period = 0,
            intra_idr_period = 0,
            ip_period = 1,
            bits_per_second = (uint)_kilobits * 1000,
            pic_width_in_luma_samples = (ushort)x.AlignedWidth,
            pic_height_in_luma_samples = (ushort)x.AlignedHeight,
            seq_fields = new BitPacker().Add(2, 1u).Add(1, false).Add(3, 0u).Add(3, 0u).Add(1, false).Add(1, true).Add(1, false).Add(1, false)
                .Add(1, false).Add(1, false).Add(1, false).Add(1, true).Add(1, false).Value,
            log2_min_luma_coding_block_size_minus3 = (byte)(x.MinCbLog2 - 3),
            log2_diff_max_min_luma_coding_block_size = (byte)(x.CtbLog2 - x.MinCbLog2),
            log2_min_transform_block_size_minus2 = (byte)(x.MinTbLog2 - 2),
            log2_diff_max_min_transform_block_size = (byte)(x.MaxTbLog2 - x.MinTbLog2),
            max_transform_hierarchy_depth_inter = (byte)x.DepthInter,
            max_transform_hierarchy_depth_intra = (byte)x.DepthIntra,
        };
        Render(LibVa.BufferSequence, &hevc, (uint)sizeof(VAEncSequenceParameterBufferHEVC));
        if ((_packed & LibVa.PackedHeaderSequence) != 0)
        {
            RenderPacked(LibVa.PackedSequence, x.Vps(false));
            RenderPacked(LibVa.PackedSequence, x.Sps(false));
            if ((_packed & LibVa.PackedHeaderPicture) != 0)
            {
                RenderPacked(LibVa.PackedPicture, x.Pps(false));
            }
        }
    }

    // Constant bit rate, half a second of buffer, no skipped pictures and no stuffing: a picture is always sent, and a still screen does not
    // get padded up to the rate. The quantiser may go as fine as 14, which is what makes a screen of text sharp when little else is going on.
    private void RenderRate()
    {
        var bitsPerSecond = (uint)_kilobits * 1000;
        var rate = new VAEncMiscParameterRateControl
        {
            bits_per_second = bitsPerSecond,
            target_percentage = 100,
            window_size = 500,
            initial_qp = H264Layout.InitQp,
            min_qp = 14,
            basic_unit_size = 0,
            rc_flags = new BitPacker().Add(1, false).Add(1, true).Add(1, true).Value,    // reset 0, disable_frame_skip 1, disable_bit_stuffing 1
        };
        RenderMisc(LibVa.MiscRateControl, &rate, (uint)sizeof(VAEncMiscParameterRateControl));
        var hrd = new VAEncMiscParameterHRD { buffer_size = bitsPerSecond / 2, initial_buffer_fullness = bitsPerSecond / 2 * 3 / 4 };
        RenderMisc(LibVa.MiscHrd, &hrd, (uint)sizeof(VAEncMiscParameterHRD));
        var fps = new VAEncMiscParameterFrameRate { framerate = (uint)_framesPerSecond };
        RenderMisc(LibVa.MiscFrameRate, &fps, (uint)sizeof(VAEncMiscParameterFrameRate));
    }

    private void RenderH264Picture(bool idr, uint target)
    {
        var h = _h264!;
        var picture = new VAEncPictureParameterBufferH264
        {
            CurrPic = new VAPictureH264 { picture_id = target, frame_idx = (uint)_frameNum, flags = 0, TopFieldOrderCnt = _poc, BottomFieldOrderCnt = _poc },
            coded_buf = _coded,
            pic_parameter_set_id = 0,
            seq_parameter_set_id = 0,
            last_picture = 0,
            frame_num = (ushort)_frameNum,
            pic_init_qp = H264Layout.InitQp,
            num_ref_idx_l0_active_minus1 = 0,
            num_ref_idx_l1_active_minus1 = 0,
            pic_fields = new BitPacker().Add(1, idr).Add(2, 1u).Add(1, h.Cabac).Add(1, false).Add(2, 0u).Add(1, false).Add(1, h.Transform8x8)
                .Add(1, true).Add(1, false).Add(1, false).Add(1, false).Value,
        };
        for (var i = 0; i < 16; i++)
        {
            picture.ReferenceFrames[i] = VAPictureH264.Invalid;
        }

        if (!idr)
        {
            picture.ReferenceFrames[0] = new VAPictureH264
            {
                picture_id = _reference, frame_idx = (uint)_referenceFrameNum, flags = LibVa.PictureH264ShortTermReference,
                TopFieldOrderCnt = _referencePoc, BottomFieldOrderCnt = _referencePoc,
            };
        }

        Render(LibVa.BufferPicture, &picture, (uint)sizeof(VAEncPictureParameterBufferH264));

        var slice = new VAEncSliceParameterBufferH264
        {
            macroblock_address = 0,
            num_macroblocks = (uint)(h.MbWidth * h.MbHeight),
            macroblock_info = LibVa.InvalidId,
            slice_type = (byte)(idr ? 2 : 0),
            pic_parameter_set_id = 0,
            idr_pic_id = (ushort)Math.Max(0, _idrPicId),
            pic_order_cnt_lsb = (ushort)(_poc & ((1 << H264Layout.PocBits) - 1)),
            cabac_init_idc = 0,
            slice_qp_delta = 0,
            disable_deblocking_filter_idc = 0,
        };
        for (var i = 0; i < 32; i++)
        {
            slice.RefPicList0[i] = VAPictureH264.Invalid;
            slice.RefPicList1[i] = VAPictureH264.Invalid;
        }

        if (!idr)
        {
            slice.RefPicList0[0] = picture.ReferenceFrames[0];
        }

        if ((_packed & LibVa.PackedHeaderSlice) != 0)
        {
            RenderPacked(LibVa.PackedSlice, h.SliceHeader(idr, _frameNum, Math.Max(0, _idrPicId), _poc & ((1 << H264Layout.PocBits) - 1)));
        }

        Render(LibVa.BufferSlice, &slice, (uint)sizeof(VAEncSliceParameterBufferH264));
    }

    private void RenderHevcPicture(bool idr, uint target)
    {
        var x = _hevc!;
        var picture = new VAEncPictureParameterBufferHEVC
        {
            decoded_curr_pic = new VAPictureHEVC { picture_id = target, pic_order_cnt = _poc, flags = 0 },
            coded_buf = _coded,
            collocated_ref_pic_index = 0xFF,
            last_picture = 0,
            pic_init_qp = HevcLayout.InitQp,
            diff_cu_qp_delta_depth = 0,
            log2_parallel_merge_level_minus2 = 0,
            slice_pic_parameter_set_id = 0,
            nal_unit_type = (byte)(idr ? 19 : 1),
            pic_fields = new BitPacker().Add(1, idr).Add(3, idr ? 1u : 2u).Add(1, true).Add(1, false).Add(1, false).Add(1, false).Add(1, false)
                .Add(1, true).Add(1, false).Add(1, false).Add(1, false).Add(1, false).Add(1, false).Add(1, false).Add(1, true).Add(1, false)
                .Add(1, false).Add(1, false).Add(1, false).Value,
        };
        for (var i = 0; i < 15; i++)
        {
            picture.reference_frames[i] = VAPictureHEVC.Invalid;
        }

        if (!idr)
        {
            picture.reference_frames[0] = new VAPictureHEVC { picture_id = _reference, pic_order_cnt = _referencePoc, flags = LibVa.PictureHevcRpsStCurrBefore };
        }

        Render(LibVa.BufferPicture, &picture, (uint)sizeof(VAEncPictureParameterBufferHEVC));

        var slice = new VAEncSliceParameterBufferHEVC
        {
            slice_segment_address = 0,
            num_ctu_in_slice = (uint)x.Ctus,
            slice_type = (byte)(idr ? 2 : 1),
            slice_pic_parameter_set_id = 0,
            num_ref_idx_l0_active_minus1 = 0,
            num_ref_idx_l1_active_minus1 = 0,
            max_num_merge_cand = HevcLayout.MaxMergeCandidates,
            slice_qp_delta = 0,
            slice_fields = new BitPacker().Add(1, true).Add(1, false).Add(2, 0u).Add(1, false).Add(1, false).Add(1, false).Add(1, false).Add(1, false)
                .Add(1, false).Add(2, 0u).Add(1, true).Add(1, !idr).Value,
        };
        for (var i = 0; i < 15; i++)
        {
            slice.ref_pic_list0[i] = VAPictureHEVC.Invalid;
            slice.ref_pic_list1[i] = VAPictureHEVC.Invalid;
        }

        if (!idr)
        {
            slice.ref_pic_list0[0] = picture.reference_frames[0];
        }

        if ((_packed & LibVa.PackedHeaderSlice) != 0)
        {
            RenderPacked(LibVa.PackedSlice, x.SliceHeader(idr, _poc & ((1 << HevcLayout.PocBits) - 1)));
        }

        Render(LibVa.BufferSlice, &slice, (uint)sizeof(VAEncSliceParameterBufferHEVC));
    }

    // ---- buffers ----

    private uint CreateBuffer(int type, uint size, void* data)
    {
        LibVa.Check(LibVa.vaCreateBuffer(_dpy, _context, type, size, 1, data, out var id), $"vaCreateBuffer (type {type})");
        _transient.Add(id);
        return id;
    }

    private void Render(int type, void* data, uint size)
    {
        var id = CreateBuffer(type, size, data);
        LibVa.Check(LibVa.vaRenderPicture(_dpy, _context, &id, 1), $"vaRenderPicture (type {type})");
    }

    private void RenderMisc(uint miscType, void* payload, uint payloadSize)
    {
        var size = (uint)sizeof(VAEncMiscParameterBuffer) + payloadSize;
        var buffer = stackalloc byte[(int)size];
        ((VAEncMiscParameterBuffer*)buffer)->type = miscType;
        Buffer.MemoryCopy(payload, buffer + sizeof(VAEncMiscParameterBuffer), payloadSize, payloadSize);
        Render(LibVa.BufferMiscParameter, buffer, size);
    }

    // A packed header is two buffers that belong together: its description (type and length in bits) and the bytes.
    private void RenderPacked(uint type, (byte[] Data, int Bits) header)
    {
        var parameter = new VAEncPackedHeaderParameterBuffer { type = type, bit_length = (uint)header.Bits, has_emulation_bytes = 0 };
        var ids = stackalloc uint[2];
        ids[0] = CreateBuffer(LibVa.BufferPackedHeaderParameter, (uint)sizeof(VAEncPackedHeaderParameterBuffer), &parameter);
        fixed (byte* data = header.Data)
        {
            ids[1] = CreateBuffer(LibVa.BufferPackedHeaderData, (uint)header.Data.Length, data);
        }

        LibVa.Check(LibVa.vaRenderPicture(_dpy, _context, ids, 2), "vaRenderPicture (packed header)");
    }

    private void DestroyTransient()
    {
        foreach (var id in _transient)
        {
            LibVa.vaDestroyBuffer(_dpy, id);
        }

        _transient.Clear();
    }

    // ---- the result ----

    private int ReadCoded()
    {
        void* mapped;
        LibVa.Check(LibVa.vaMapBuffer(_dpy, _coded, &mapped), "vaMapBuffer (coded)");
        try
        {
            var total = 0;
            for (var segment = (VACodedBufferSegment*)mapped; segment != null; segment = (VACodedBufferSegment*)segment->next)
            {
                if ((segment->status & 512) != 0)               // VA_CODED_BUF_STATUS_SLICE_OVERFLOW_MASK
                {
                    throw new VaapiException("the coded buffer was too small for the picture");
                }

                if (total + (int)segment->size > _output.Length)
                {
                    Array.Resize(ref _output, Math.Max(_output.Length * 2, total + (int)segment->size));
                }

                new ReadOnlySpan<byte>((void*)segment->buf, (int)segment->size).CopyTo(_output.AsSpan(total));
                total += (int)segment->size;
            }

            return total;
        }
        finally
        {
            LibVa.vaUnmapBuffer(_dpy, _coded);
        }
    }

    // Makes sure the bytes are an Annex B access unit and that a key frame carries its parameter sets: a driver that was given packed headers
    // writes them into the coded buffer, one that made its own does too; if neither is there (it left them out), ours go in front.
    private ReadOnlyMemory<byte> Finish(int length, bool idr)
    {
        if (length < 5 || !(_output[0] == 0 && _output[1] == 0 && (_output[2] == 1 || (_output[2] == 0 && _output[3] == 1))))
        {
            throw new VaapiException(length == 0 ? "the driver returned no data" : "the driver's output is not Annex B (no start code)");
        }

        if (!idr || HasParameterSets(_output.AsSpan(0, length), Codec))
        {
            return new ReadOnlyMemory<byte>(_output, 0, length);
        }

        var sets = new List<byte>(256);
        if (_h264 is { } h)
        {
            sets.AddRange(h.Sps(true).Data);
            sets.AddRange(h.Pps(true).Data);
        }
        else
        {
            sets.AddRange(_hevc!.Vps(true).Data);
            sets.AddRange(_hevc.Sps(true).Data);
            sets.AddRange(_hevc.Pps(true).Data);
        }

        var combined = new byte[sets.Count + length];
        sets.CopyTo(combined);
        Buffer.BlockCopy(_output, 0, combined, sets.Count, length);
        return combined;
    }

    // Whether the bytes up to the first slice contain an SPS (and for HEVC a VPS).
    internal static bool HasParameterSets(ReadOnlySpan<byte> data, VideoCodec codec)
    {
        var sps = false;
        var vps = codec == VideoCodec.H264;
        for (var i = 0; i + 4 < data.Length && i < 1024; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
            {
                continue;
            }

            var type = codec == VideoCodec.H264 ? data[i + 3] & 0x1F : (data[i + 3] >> 1) & 0x3F;
            if (codec == VideoCodec.H264)
            {
                sps |= type == 7;
                if (type is 1 or 5)
                {
                    break;
                }
            }
            else
            {
                sps |= type == 33;
                vps |= type == 32;
                if (type < 32)
                {
                    break;
                }
            }
        }

        return sps && vps;
    }

    // ---- the picture into the GPU ----

    private void FillBlack()
    {
        using var black = new I420Frame(AlignedWidth, AlignedHeight);
        UploadPlanes(black.PointerY, black.StrideY, black.PointerU, black.PointerV, black.StrideU, 0, 0, AlignedWidth, AlignedHeight);
    }

    private void Upload(I420Frame frame, int left, int top, int right, int bottom)
    {
        left &= ~1;
        top &= ~1;
        right = Math.Min(Width, (right + 1) & ~1);
        bottom = Math.Min(Height, (bottom + 1) & ~1);
        UploadPlanes(frame.PointerY, frame.StrideY, frame.PointerU, frame.PointerV, frame.StrideU, left, top, right, bottom);
    }

    // Writes the rectangle into the input surface as NV12 (the planar U and V of the frame are interleaved on the way). The surface keeps what was
    // written before, so a small change is a small copy. First through vaDeriveImage (the surface's own memory, mapped); where the driver cannot,
    // through a linear image of ours that is put on the surface whole.
    private void UploadPlanes(byte* y, int strideY, byte* u, byte* v, int strideC, int left, int top, int right, int bottom)
    {
        var input = _surfaces[0];
        VAImage image;
        if (LibVa.vaDeriveImage(_dpy, input, &image) == LibVa.StatusSuccess)
        {
            try
            {
                CopyToImage(image, y, strideY, u, v, strideC, left, top, right, bottom);
            }
            finally
            {
                LibVa.vaDestroyImage(_dpy, image.image_id);
            }

            return;
        }

        if (_stagingImage == LibVa.InvalidId)
        {
            var format = new VAImageFormat { fourcc = LibVa.Nv12, byte_order = LibVa.LsbFirst, bits_per_pixel = 12 };
            LibVa.Check(LibVa.vaCreateImage(_dpy, &format, AlignedWidth, AlignedHeight, &image), "vaCreateImage");
            _stagingImage = image.image_id;
            _stagingInfo = image;
        }

        CopyToImage(_stagingInfo, y, strideY, u, v, strideC, left, top, right, bottom);
        LibVa.Check(LibVa.vaPutImage(_dpy, input, _stagingImage, 0, 0, (uint)AlignedWidth, (uint)AlignedHeight, 0, 0, (uint)AlignedWidth, (uint)AlignedHeight), "vaPutImage");
    }

    private VAImage _stagingInfo;

    private void CopyToImage(VAImage image, byte* y, int strideY, byte* u, byte* v, int strideC, int left, int top, int right, int bottom)
    {
        if (image.format.fourcc != LibVa.Nv12 || image.num_planes < 2)
        {
            throw new VaapiException($"the surface is not NV12 (fourcc 0x{image.format.fourcc:x}, {image.num_planes} planes)");
        }

        void* mapped;
        LibVa.Check(LibVa.vaMapBuffer(_dpy, image.buf, &mapped), "vaMapBuffer (image)");
        try
        {
            var basePointer = (byte*)mapped;
            var pitchY = (int)image.pitches[0];
            var pitchUv = (int)image.pitches[1];
            var planeY = basePointer + image.offsets[0];
            var planeUv = basePointer + image.offsets[1];
            var width = right - left;
            for (var row = top; row < bottom; row++)
            {
                Buffer.MemoryCopy(y + (long)row * strideY + left, planeY + (long)row * pitchY + left, width, width);
            }

            for (var row = top / 2; row < bottom / 2; row++)
            {
                Interleave(u + (long)row * strideC + left / 2, v + (long)row * strideC + left / 2, planeUv + (long)row * pitchUv + left, width / 2);
            }
        }
        finally
        {
            LibVa.vaUnmapBuffer(_dpy, image.buf);
        }
    }

    // UVUVUV... from two planes
    private static void Interleave(byte* u, byte* v, byte* target, int count)
    {
        for (var i = 0; i < count; i++)
        {
            target[2 * i] = u[i];
            target[2 * i + 1] = v[i];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyTransient();
        try
        {
            if (_stagingImage != LibVa.InvalidId)
            {
                LibVa.vaDestroyImage(_dpy, _stagingImage);
            }

            if (_coded != LibVa.InvalidId)
            {
                LibVa.vaDestroyBuffer(_dpy, _coded);
            }

            if (_context != LibVa.InvalidId)
            {
                LibVa.vaDestroyContext(_dpy, _context);
            }

            fixed (uint* surfaces = _surfaces)
            {
                if (_surfacesCreated)
                {
                    LibVa.vaDestroySurfaces(_dpy, surfaces, SurfaceCount);
                }
            }

            if (_config != LibVa.InvalidId)
            {
                LibVa.vaDestroyConfig(_dpy, _config);
            }
        }
        finally
        {
            _device.Dispose();
        }
    }
}
