#pragma warning disable IDE1006, CA1707, CA1051, SA1307 // the field names are libva's own: LayoutTests compares them with the C headers
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JulOS.Integrations.Display.Video.Vaapi;

// libva (MIT licence) through P/Invoke: the plain C functions of libva.so.2 and libva-drm.so.2, no wrapper library. Every structure here
// is a copy of one in libva's headers (va.h, va_enc_h264.h, va_enc_hevc.h), with the same field names; tests/JulOS.Display.Tests/Vaapi/layout.c
// prints their sizes and offsets from the real headers and LayoutTests compares them with these. Bit fields of the C unions are plain
// unsigned integers here (see BitPacker).
internal static unsafe partial class LibVa
{
    private const string Va = "libva.so.2";
    private const string VaDrm = "libva-drm.so.2";

    // ---- constants (values printed by layout.c) ----
    public const int StatusSuccess = 0;
    public const uint InvalidId = 0xFFFFFFFF;
    public const uint InvalidSurface = 0xFFFFFFFF;
    public const uint Nv12 = 0x3231564E;                 // VA_FOURCC_NV12
    public const uint RtFormatYuv420 = 1;
    public const uint Progressive = 1;
    public const uint LsbFirst = 1;                      // VA_LSB_FIRST
    public const uint AttribNotSupported = 0x80000000;

    public const uint RcCbr = 2, RcVbr = 4, RcCqp = 16;
    public const uint PackedHeaderSequence = 1, PackedHeaderPicture = 2, PackedHeaderSlice = 4;

    // VAProfile
    public const int ProfileH264Main = 6, ProfileH264High = 7, ProfileH264ConstrainedBaseline = 13, ProfileHevcMain = 17;

    // VAEntrypoint
    public const int EntrypointEncSlice = 6, EntrypointEncSliceLP = 8;

    // VAConfigAttribType
    public const int AttribRtFormat = 0, AttribRateControl = 5, AttribEncPackedHeaders = 10, AttribEncMaxRefFrames = 13, AttribEncMaxSlices = 14,
        AttribMaxPictureWidth = 18, AttribMaxPictureHeight = 19, AttribEncQualityRange = 21, AttribEncHevcBlockSizes = 51;

    // VABufferType
    public const int BufferCoded = 21, BufferSequence = 22, BufferPicture = 23, BufferSlice = 24, BufferPackedHeaderParameter = 25,
        BufferPackedHeaderData = 26, BufferMiscParameter = 27;

    // VAEncMiscParameterType
    public const uint MiscFrameRate = 0, MiscRateControl = 1, MiscHrd = 5;

    // VAEncPackedHeaderType
    public const uint PackedSequence = 1, PackedPicture = 2, PackedSlice = 3;

    // VAPictureH264.flags and VAPictureHEVC.flags
    public const uint PictureH264Invalid = 1, PictureH264ShortTermReference = 8, PictureHevcInvalid = 1, PictureHevcRpsStCurrBefore = 16;

    // libva writes "libva info: ..." lines to stderr by itself; the server's log has its own words for what happens. Errors stay (level 1) unless
    // the operator sets LIBVA_MESSAGING_LEVEL. libva reads the variable from the real process environment, which .NET's own SetEnvironmentVariable
    // does not touch on Linux, hence setenv.
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);

    public static void QuietenMessages()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBVA_MESSAGING_LEVEL")) && OperatingSystem.IsLinux())
        {
            try { setenv("LIBVA_MESSAGING_LEVEL", "1", 0); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { /* only the messages stay as they are */ }
        }
    }

    // ---- functions ----
    [LibraryImport(VaDrm)] public static partial nint vaGetDisplayDRM(int fd);

    [LibraryImport(Va)] public static partial int vaInitialize(nint dpy, out int major, out int minor);
    [LibraryImport(Va)] public static partial int vaTerminate(nint dpy);
    [LibraryImport(Va)] public static partial nint vaQueryVendorString(nint dpy);
    [LibraryImport(Va)] public static partial nint vaErrorStr(int status);
    [LibraryImport(Va)] public static partial int vaMaxNumProfiles(nint dpy);
    [LibraryImport(Va)] public static partial int vaMaxNumEntrypoints(nint dpy);
    [LibraryImport(Va)] public static partial int vaQueryConfigProfiles(nint dpy, int* profiles, out int count);
    [LibraryImport(Va)] public static partial int vaQueryConfigEntrypoints(nint dpy, int profile, int* entrypoints, out int count);
    [LibraryImport(Va)] public static partial int vaGetConfigAttributes(nint dpy, int profile, int entrypoint, VAConfigAttrib* attribs, int count);
    [LibraryImport(Va)] public static partial int vaCreateConfig(nint dpy, int profile, int entrypoint, VAConfigAttrib* attribs, int count, out uint config);
    [LibraryImport(Va)] public static partial int vaDestroyConfig(nint dpy, uint config);
    [LibraryImport(Va)] public static partial int vaCreateSurfaces(nint dpy, uint format, uint width, uint height, uint* surfaces, uint count, void* attribs, uint attribCount);
    [LibraryImport(Va)] public static partial int vaDestroySurfaces(nint dpy, uint* surfaces, int count);
    [LibraryImport(Va)] public static partial int vaCreateContext(nint dpy, uint config, int width, int height, int flag, uint* targets, int count, out uint context);
    [LibraryImport(Va)] public static partial int vaDestroyContext(nint dpy, uint context);
    [LibraryImport(Va)] public static partial int vaCreateBuffer(nint dpy, uint context, int type, uint size, uint count, void* data, out uint buffer);
    [LibraryImport(Va)] public static partial int vaDestroyBuffer(nint dpy, uint buffer);
    [LibraryImport(Va)] public static partial int vaMapBuffer(nint dpy, uint buffer, void** data);
    [LibraryImport(Va)] public static partial int vaUnmapBuffer(nint dpy, uint buffer);
    [LibraryImport(Va)] public static partial int vaBeginPicture(nint dpy, uint context, uint target);
    [LibraryImport(Va)] public static partial int vaRenderPicture(nint dpy, uint context, uint* buffers, int count);
    [LibraryImport(Va)] public static partial int vaEndPicture(nint dpy, uint context);
    [LibraryImport(Va)] public static partial int vaSyncSurface(nint dpy, uint surface);
    [LibraryImport(Va)] public static partial int vaDeriveImage(nint dpy, uint surface, VAImage* image);
    [LibraryImport(Va)] public static partial int vaDestroyImage(nint dpy, uint image);
    [LibraryImport(Va)] public static partial int vaCreateImage(nint dpy, VAImageFormat* format, int width, int height, VAImage* image);
    [LibraryImport(Va)] public static partial int vaPutImage(nint dpy, uint surface, uint image, int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY, uint destWidth, uint destHeight);

    public static string Error(int status)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(vaErrorStr(status));
            return $"{text ?? "?"} (0x{status:x})";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return $"0x{status:x}";
        }
    }

    /// <summary>Throws a <see cref="VaapiException"/> naming the call when libva did not answer success.</summary>
    public static void Check(int status, string what)
    {
        if (status != StatusSuccess)
        {
            throw new VaapiException($"{what}: {Error(status)}");
        }
    }
}

/// <summary>Anything that goes wrong in the GPU path; the message is meant for the log and the diagnostics.</summary>
internal sealed class VaapiException(string message) : Exception(message);

// ---- fixed-size arrays inside the structures ----
[InlineArray(4)] internal struct Reserved4 { private uint _e; }
[InlineArray(6)] internal struct Reserved6 { private uint _e; }
[InlineArray(7)] internal struct Reserved7 { private uint _e; }
[InlineArray(15)] internal struct Reserved15 { private uint _e; }
[InlineArray(16)] internal struct Reserved16 { private uint _e; }
[InlineArray(256)] internal struct Ints256 { private int _e; }
[InlineArray(16)] internal struct PicturesH264x16 { private VAPictureH264 _e; }
[InlineArray(32)] internal struct PicturesH264x32 { private VAPictureH264 _e; }
[InlineArray(15)] internal struct PicturesHevcx15 { private VAPictureHEVC _e; }
[InlineArray(32)] internal struct Shorts32 { private short _e; }
[InlineArray(64)] internal struct Shorts64 { private short _e; }
[InlineArray(15)] internal struct SBytes15 { private sbyte _e; }
[InlineArray(30)] internal struct SBytes30 { private sbyte _e; }
[InlineArray(19)] internal struct Bytes19 { private byte _e; }
[InlineArray(21)] internal struct Bytes21 { private byte _e; }

[StructLayout(LayoutKind.Sequential)]
internal struct VAConfigAttrib
{
    public int type;
    public uint value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAPictureH264
{
    public uint picture_id;
    public uint frame_idx;
    public uint flags;
    public int TopFieldOrderCnt;
    public int BottomFieldOrderCnt;
    public Reserved4 va_reserved;

    public static VAPictureH264 Invalid => new() { picture_id = LibVa.InvalidSurface, flags = LibVa.PictureH264Invalid };
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAPictureHEVC
{
    public uint picture_id;
    public int pic_order_cnt;
    public uint flags;
    public Reserved4 va_reserved;

    public static VAPictureHEVC Invalid => new() { picture_id = LibVa.InvalidSurface, flags = LibVa.PictureHevcInvalid };
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncSequenceParameterBufferH264
{
    public byte seq_parameter_set_id;
    public byte level_idc;
    public uint intra_period;
    public uint intra_idr_period;
    public uint ip_period;
    public uint bits_per_second;
    public uint max_num_ref_frames;
    public ushort picture_width_in_mbs;
    public ushort picture_height_in_mbs;
    public uint seq_fields;
    public byte bit_depth_luma_minus8;
    public byte bit_depth_chroma_minus8;
    public byte num_ref_frames_in_pic_order_cnt_cycle;
    public int offset_for_non_ref_pic;
    public int offset_for_top_to_bottom_field;
    public Ints256 offset_for_ref_frame;
    public byte frame_cropping_flag;
    public uint frame_crop_left_offset;
    public uint frame_crop_right_offset;
    public uint frame_crop_top_offset;
    public uint frame_crop_bottom_offset;
    public byte vui_parameters_present_flag;
    public uint vui_fields;
    public byte aspect_ratio_idc;
    public uint sar_width;
    public uint sar_height;
    public uint num_units_in_tick;
    public uint time_scale;
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncPictureParameterBufferH264
{
    public VAPictureH264 CurrPic;
    public PicturesH264x16 ReferenceFrames;
    public uint coded_buf;
    public byte pic_parameter_set_id;
    public byte seq_parameter_set_id;
    public byte last_picture;
    public ushort frame_num;
    public byte pic_init_qp;
    public byte num_ref_idx_l0_active_minus1;
    public byte num_ref_idx_l1_active_minus1;
    public sbyte chroma_qp_index_offset;
    public sbyte second_chroma_qp_index_offset;
    public uint pic_fields;
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncSliceParameterBufferH264
{
    public uint macroblock_address;
    public uint num_macroblocks;
    public uint macroblock_info;
    public byte slice_type;
    public byte pic_parameter_set_id;
    public ushort idr_pic_id;
    public ushort pic_order_cnt_lsb;
    public int delta_pic_order_cnt_bottom;
    public int delta_pic_order_cnt_0;
    public int delta_pic_order_cnt_1;
    public byte direct_spatial_mv_pred_flag;
    public byte num_ref_idx_active_override_flag;
    public byte num_ref_idx_l0_active_minus1;
    public byte num_ref_idx_l1_active_minus1;
    public PicturesH264x32 RefPicList0;
    public PicturesH264x32 RefPicList1;
    public byte luma_log2_weight_denom;
    public byte chroma_log2_weight_denom;
    public byte luma_weight_l0_flag;
    public Shorts32 luma_weight_l0;
    public Shorts32 luma_offset_l0;
    public byte chroma_weight_l0_flag;
    public Shorts64 chroma_weight_l0;
    public Shorts64 chroma_offset_l0;
    public byte luma_weight_l1_flag;
    public Shorts32 luma_weight_l1;
    public Shorts32 luma_offset_l1;
    public byte chroma_weight_l1_flag;
    public Shorts64 chroma_weight_l1;
    public Shorts64 chroma_offset_l1;
    public byte cabac_init_idc;
    public sbyte slice_qp_delta;
    public byte disable_deblocking_filter_idc;
    public sbyte slice_alpha_c0_offset_div2;
    public sbyte slice_beta_offset_div2;
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncSequenceParameterBufferHEVC
{
    public byte general_profile_idc;
    public byte general_level_idc;
    public byte general_tier_flag;
    public uint intra_period;
    public uint intra_idr_period;
    public uint ip_period;
    public uint bits_per_second;
    public ushort pic_width_in_luma_samples;
    public ushort pic_height_in_luma_samples;
    public uint seq_fields;
    public byte log2_min_luma_coding_block_size_minus3;
    public byte log2_diff_max_min_luma_coding_block_size;
    public byte log2_min_transform_block_size_minus2;
    public byte log2_diff_max_min_transform_block_size;
    public byte max_transform_hierarchy_depth_inter;
    public byte max_transform_hierarchy_depth_intra;
    public uint pcm_sample_bit_depth_luma_minus1;
    public uint pcm_sample_bit_depth_chroma_minus1;
    public uint log2_min_pcm_luma_coding_block_size_minus3;
    public uint log2_max_pcm_luma_coding_block_size_minus3;
    public byte vui_parameters_present_flag;
    public uint vui_fields;
    public byte aspect_ratio_idc;
    public uint sar_width;
    public uint sar_height;
    public uint vui_num_units_in_tick;
    public uint vui_time_scale;
    public ushort min_spatial_segmentation_idc;
    public byte max_bytes_per_pic_denom;
    public byte max_bits_per_min_cu_denom;
    public uint scc_fields;
    public Reserved7 va_reserved;       // VA_PADDING_MEDIUM - 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncPictureParameterBufferHEVC
{
    public VAPictureHEVC decoded_curr_pic;
    public PicturesHevcx15 reference_frames;
    public uint coded_buf;
    public byte collocated_ref_pic_index;
    public byte last_picture;
    public byte pic_init_qp;
    public byte diff_cu_qp_delta_depth;
    public sbyte pps_cb_qp_offset;
    public sbyte pps_cr_qp_offset;
    public byte num_tile_columns_minus1;
    public byte num_tile_rows_minus1;
    public Bytes19 column_width_minus1;
    public Bytes21 row_height_minus1;
    public byte log2_parallel_merge_level_minus2;
    public byte ctu_max_bitsize_allowed;
    public byte num_ref_idx_l0_default_active_minus1;
    public byte num_ref_idx_l1_default_active_minus1;
    public byte slice_pic_parameter_set_id;
    public byte nal_unit_type;
    public uint pic_fields;
    public byte hierarchical_level_plus1;
    public byte va_byte_reserved;
    public ushort scc_fields;
    public Reserved15 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncSliceParameterBufferHEVC
{
    public uint slice_segment_address;
    public uint num_ctu_in_slice;
    public byte slice_type;
    public byte slice_pic_parameter_set_id;
    public byte num_ref_idx_l0_active_minus1;
    public byte num_ref_idx_l1_active_minus1;
    public PicturesHevcx15 ref_pic_list0;
    public PicturesHevcx15 ref_pic_list1;
    public byte luma_log2_weight_denom;
    public sbyte delta_chroma_log2_weight_denom;
    public SBytes15 delta_luma_weight_l0;
    public SBytes15 luma_offset_l0;
    public SBytes30 delta_chroma_weight_l0;
    public SBytes30 chroma_offset_l0;
    public SBytes15 delta_luma_weight_l1;
    public SBytes15 luma_offset_l1;
    public SBytes30 delta_chroma_weight_l1;
    public SBytes30 chroma_offset_l1;
    public byte max_num_merge_cand;
    public sbyte slice_qp_delta;
    public sbyte slice_cb_qp_offset;
    public sbyte slice_cr_qp_offset;
    public sbyte slice_beta_offset_div2;
    public sbyte slice_tc_offset_div2;
    public uint slice_fields;
    public uint pred_weight_table_bit_offset;
    public uint pred_weight_table_bit_length;
    public Reserved6 va_reserved;       // VA_PADDING_MEDIUM - 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncPackedHeaderParameterBuffer
{
    public uint type;
    public uint bit_length;
    public byte has_emulation_bytes;
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncMiscParameterBuffer
{
    public uint type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncMiscParameterRateControl
{
    public uint bits_per_second;
    public uint target_percentage;
    public uint window_size;
    public uint initial_qp;
    public uint min_qp;
    public uint basic_unit_size;
    public uint rc_flags;
    public uint ICQ_quality_factor;
    public uint max_qp;
    public uint quality_factor;
    public uint target_frame_size;
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncMiscParameterFrameRate
{
    public uint framerate;
    public uint framerate_flags;
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAEncMiscParameterHRD
{
    public uint initial_buffer_fullness;
    public uint buffer_size;
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VAImageFormat
{
    public uint fourcc;
    public uint byte_order;
    public uint bits_per_pixel;
    public uint depth;
    public uint red_mask;
    public uint green_mask;
    public uint blue_mask;
    public uint alpha_mask;
    public Reserved4 va_reserved;
}

[InlineArray(3)] internal struct Uints3 { private uint _e; }

[StructLayout(LayoutKind.Sequential)]
internal struct VAImage
{
    public uint image_id;
    public VAImageFormat format;
    public uint buf;
    public ushort width;
    public ushort height;
    public uint data_size;
    public uint num_planes;
    public Uints3 pitches;
    public Uints3 offsets;
    public int num_palette_entries;
    public int entry_bytes;
    public int component_order;     // int8_t[4]
    public Reserved4 va_reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VACodedBufferSegment
{
    public uint size;
    public uint bit_offset;
    public uint status;
    public uint reserved;
    public nint buf;
    public nint next;
    public Reserved4 va_reserved;
}

/// <summary>Packs the bit fields of a libva union from the lowest bit up, in the order the header declares them.</summary>
internal ref struct BitPacker
{
    private uint _value;
    private int _at;

    public readonly uint Value => _value;

    public BitPacker Add(int width, uint value)
    {
        _value |= (value & ((1u << width) - 1)) << _at;
        _at += width;
        return this;
    }

    public BitPacker Add(int width, bool value) => Add(width, value ? 1u : 0u);
}
