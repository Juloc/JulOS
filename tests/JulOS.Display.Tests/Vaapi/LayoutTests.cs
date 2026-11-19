#pragma warning disable CA1861
using System.Runtime.InteropServices;
using JulOS.Integrations.Display.Video.Vaapi;

namespace JulOS.Integrations.Display.Video.Tests;

// The libva structures in LibVa.cs against the C headers: sizes and field offsets as tests/JulOS.Display.Tests/Vaapi/layout.c printed them
// (libva 2.22 headers), and the constants the encoder uses. A wrong offset here would hand the driver garbage on a machine with a GPU,
// which no test on a machine without one can see otherwise.
public sealed class LayoutTests
{
    public static TheoryData<string, int> Sizes => new()
    {
        { "VAConfigAttrib", 8 },
        { "VAPictureH264", 36 },
        { "VAPictureHEVC", 28 },
        { "VAEncSequenceParameterBufferH264", 1132 },
        { "VAEncPictureParameterBufferH264", 648 },
        { "VAEncSliceParameterBufferH264", 3140 },
        { "VAEncSequenceParameterBufferHEVC", 116 },
        { "VAEncPictureParameterBufferHEVC", 576 },
        { "VAEncSliceParameterBufferHEVC", 1076 },
        { "VAEncPackedHeaderParameterBuffer", 28 },
        { "VAEncMiscParameterBuffer", 4 },
        { "VAEncMiscParameterRateControl", 60 },
        { "VAEncMiscParameterFrameRate", 24 },
        { "VAEncMiscParameterHRD", 24 },
        { "VAImageFormat", 48 },
        { "VAImage", 120 },
        { "VACodedBufferSegment", 48 },
    };

    public static TheoryData<string, string, int> Offsets => new()
    {
        { "VAConfigAttrib", "value", 4 },
        { "VAPictureH264", "frame_idx", 4 },
        { "VAPictureH264", "flags", 8 },
        { "VAPictureH264", "TopFieldOrderCnt", 12 },
        { "VAPictureH264", "BottomFieldOrderCnt", 16 },
        { "VAPictureHEVC", "pic_order_cnt", 4 },
        { "VAPictureHEVC", "flags", 8 },
        { "VAEncSequenceParameterBufferH264", "level_idc", 1 },
        { "VAEncSequenceParameterBufferH264", "intra_period", 4 },
        { "VAEncSequenceParameterBufferH264", "intra_idr_period", 8 },
        { "VAEncSequenceParameterBufferH264", "ip_period", 12 },
        { "VAEncSequenceParameterBufferH264", "bits_per_second", 16 },
        { "VAEncSequenceParameterBufferH264", "max_num_ref_frames", 20 },
        { "VAEncSequenceParameterBufferH264", "picture_width_in_mbs", 24 },
        { "VAEncSequenceParameterBufferH264", "picture_height_in_mbs", 26 },
        { "VAEncSequenceParameterBufferH264", "seq_fields", 28 },
        { "VAEncSequenceParameterBufferH264", "bit_depth_luma_minus8", 32 },
        { "VAEncSequenceParameterBufferH264", "offset_for_ref_frame", 44 },
        { "VAEncSequenceParameterBufferH264", "frame_cropping_flag", 1068 },
        { "VAEncSequenceParameterBufferH264", "frame_crop_left_offset", 1072 },
        { "VAEncSequenceParameterBufferH264", "frame_crop_bottom_offset", 1084 },
        { "VAEncSequenceParameterBufferH264", "vui_parameters_present_flag", 1088 },
        { "VAEncSequenceParameterBufferH264", "vui_fields", 1092 },
        { "VAEncSequenceParameterBufferH264", "aspect_ratio_idc", 1096 },
        { "VAEncSequenceParameterBufferH264", "num_units_in_tick", 1108 },
        { "VAEncSequenceParameterBufferH264", "time_scale", 1112 },
        { "VAEncPictureParameterBufferH264", "ReferenceFrames", 36 },
        { "VAEncPictureParameterBufferH264", "coded_buf", 612 },
        { "VAEncPictureParameterBufferH264", "pic_parameter_set_id", 616 },
        { "VAEncPictureParameterBufferH264", "last_picture", 618 },
        { "VAEncPictureParameterBufferH264", "frame_num", 620 },
        { "VAEncPictureParameterBufferH264", "pic_init_qp", 622 },
        { "VAEncPictureParameterBufferH264", "num_ref_idx_l0_active_minus1", 623 },
        { "VAEncPictureParameterBufferH264", "chroma_qp_index_offset", 625 },
        { "VAEncPictureParameterBufferH264", "pic_fields", 628 },
        { "VAEncSliceParameterBufferH264", "num_macroblocks", 4 },
        { "VAEncSliceParameterBufferH264", "macroblock_info", 8 },
        { "VAEncSliceParameterBufferH264", "slice_type", 12 },
        { "VAEncSliceParameterBufferH264", "idr_pic_id", 14 },
        { "VAEncSliceParameterBufferH264", "pic_order_cnt_lsb", 16 },
        { "VAEncSliceParameterBufferH264", "delta_pic_order_cnt_0", 24 },
        { "VAEncSliceParameterBufferH264", "direct_spatial_mv_pred_flag", 32 },
        { "VAEncSliceParameterBufferH264", "num_ref_idx_l0_active_minus1", 34 },
        { "VAEncSliceParameterBufferH264", "RefPicList0", 36 },
        { "VAEncSliceParameterBufferH264", "RefPicList1", 1188 },
        { "VAEncSliceParameterBufferH264", "luma_log2_weight_denom", 2340 },
        { "VAEncSliceParameterBufferH264", "cabac_init_idc", 3118 },
        { "VAEncSliceParameterBufferH264", "slice_qp_delta", 3119 },
        { "VAEncSliceParameterBufferH264", "disable_deblocking_filter_idc", 3120 },
        { "VAEncSequenceParameterBufferHEVC", "general_tier_flag", 2 },
        { "VAEncSequenceParameterBufferHEVC", "intra_period", 4 },
        { "VAEncSequenceParameterBufferHEVC", "ip_period", 12 },
        { "VAEncSequenceParameterBufferHEVC", "bits_per_second", 16 },
        { "VAEncSequenceParameterBufferHEVC", "pic_width_in_luma_samples", 20 },
        { "VAEncSequenceParameterBufferHEVC", "seq_fields", 24 },
        { "VAEncSequenceParameterBufferHEVC", "log2_min_luma_coding_block_size_minus3", 28 },
        { "VAEncSequenceParameterBufferHEVC", "max_transform_hierarchy_depth_intra", 33 },
        { "VAEncSequenceParameterBufferHEVC", "pcm_sample_bit_depth_luma_minus1", 36 },
        { "VAEncSequenceParameterBufferHEVC", "vui_parameters_present_flag", 52 },
        { "VAEncSequenceParameterBufferHEVC", "vui_fields", 56 },
        { "VAEncSequenceParameterBufferHEVC", "vui_time_scale", 76 },
        { "VAEncSequenceParameterBufferHEVC", "min_spatial_segmentation_idc", 80 },
        { "VAEncSequenceParameterBufferHEVC", "scc_fields", 84 },
        { "VAEncPictureParameterBufferHEVC", "reference_frames", 28 },
        { "VAEncPictureParameterBufferHEVC", "coded_buf", 448 },
        { "VAEncPictureParameterBufferHEVC", "collocated_ref_pic_index", 452 },
        { "VAEncPictureParameterBufferHEVC", "pic_init_qp", 454 },
        { "VAEncPictureParameterBufferHEVC", "num_tile_columns_minus1", 458 },
        { "VAEncPictureParameterBufferHEVC", "log2_parallel_merge_level_minus2", 500 },
        { "VAEncPictureParameterBufferHEVC", "slice_pic_parameter_set_id", 504 },
        { "VAEncPictureParameterBufferHEVC", "nal_unit_type", 505 },
        { "VAEncPictureParameterBufferHEVC", "pic_fields", 508 },
        { "VAEncPictureParameterBufferHEVC", "hierarchical_level_plus1", 512 },
        { "VAEncPictureParameterBufferHEVC", "scc_fields", 514 },
        { "VAEncSliceParameterBufferHEVC", "num_ctu_in_slice", 4 },
        { "VAEncSliceParameterBufferHEVC", "slice_type", 8 },
        { "VAEncSliceParameterBufferHEVC", "ref_pic_list0", 12 },
        { "VAEncSliceParameterBufferHEVC", "ref_pic_list1", 432 },
        { "VAEncSliceParameterBufferHEVC", "luma_log2_weight_denom", 852 },
        { "VAEncSliceParameterBufferHEVC", "max_num_merge_cand", 1034 },
        { "VAEncSliceParameterBufferHEVC", "slice_qp_delta", 1035 },
        { "VAEncSliceParameterBufferHEVC", "slice_tc_offset_div2", 1039 },
        { "VAEncSliceParameterBufferHEVC", "slice_fields", 1040 },
        { "VAEncSliceParameterBufferHEVC", "pred_weight_table_bit_offset", 1044 },
        { "VAEncPackedHeaderParameterBuffer", "bit_length", 4 },
        { "VAEncPackedHeaderParameterBuffer", "has_emulation_bytes", 8 },
        { "VAEncMiscParameterRateControl", "window_size", 8 },
        { "VAEncMiscParameterRateControl", "initial_qp", 12 },
        { "VAEncMiscParameterRateControl", "min_qp", 16 },
        { "VAEncMiscParameterRateControl", "basic_unit_size", 20 },
        { "VAEncMiscParameterRateControl", "rc_flags", 24 },
        { "VAEncMiscParameterRateControl", "ICQ_quality_factor", 28 },
        { "VAEncMiscParameterRateControl", "max_qp", 32 },
        { "VAEncMiscParameterRateControl", "quality_factor", 36 },
        { "VAEncMiscParameterHRD", "buffer_size", 4 },
        { "VAImage", "format", 4 },
        { "VAImage", "buf", 52 },
        { "VAImage", "width", 56 },
        { "VAImage", "data_size", 60 },
        { "VAImage", "num_planes", 64 },
        { "VAImage", "pitches", 68 },
        { "VAImage", "offsets", 80 },
        { "VACodedBufferSegment", "bit_offset", 4 },
        { "VACodedBufferSegment", "status", 8 },
        { "VACodedBufferSegment", "buf", 16 },
        { "VACodedBufferSegment", "next", 24 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_structure_is_as_large_as_in_the_header(string name, int expected)
    {
        var type = typeof(LibVa).Assembly.GetType("JulOS.Integrations.Display.Video.Vaapi." + name, throwOnError: true)!;
        Assert.Equal(expected, Marshal.SizeOf(type));
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_field_is_where_the_header_has_it(string name, string field, int expected)
    {
        var type = typeof(LibVa).Assembly.GetType("JulOS.Integrations.Display.Video.Vaapi." + name, throwOnError: true)!;
        Assert.Equal(expected, (int)Marshal.OffsetOf(type, field));
    }

    [Fact]
    public void The_constants_are_the_ones_of_the_header()
    {
        Assert.Equal(13u, (uint)LibVa.ProfileH264ConstrainedBaseline);   // VAProfileH264ConstrainedBaseline
        Assert.Equal(6u, (uint)LibVa.ProfileH264Main);   // VAProfileH264Main
        Assert.Equal(7u, (uint)LibVa.ProfileH264High);   // VAProfileH264High
        Assert.Equal(17u, (uint)LibVa.ProfileHevcMain);   // VAProfileHEVCMain
        Assert.Equal(6u, (uint)LibVa.EntrypointEncSlice);   // VAEntrypointEncSlice
        Assert.Equal(8u, (uint)LibVa.EntrypointEncSliceLP);   // VAEntrypointEncSliceLP
        Assert.Equal(0u, (uint)LibVa.AttribRtFormat);   // VAConfigAttribRTFormat
        Assert.Equal(5u, (uint)LibVa.AttribRateControl);   // VAConfigAttribRateControl
        Assert.Equal(10u, (uint)LibVa.AttribEncPackedHeaders);   // VAConfigAttribEncPackedHeaders
        Assert.Equal(13u, (uint)LibVa.AttribEncMaxRefFrames);   // VAConfigAttribEncMaxRefFrames
        Assert.Equal(14u, (uint)LibVa.AttribEncMaxSlices);   // VAConfigAttribEncMaxSlices
        Assert.Equal(21u, (uint)LibVa.AttribEncQualityRange);   // VAConfigAttribEncQualityRange
        Assert.Equal(1u, (uint)LibVa.RtFormatYuv420);   // VA_RT_FORMAT_YUV420
        Assert.Equal(2u, (uint)LibVa.RcCbr);   // VA_RC_CBR
        Assert.Equal(4u, (uint)LibVa.RcVbr);   // VA_RC_VBR
        Assert.Equal(16u, (uint)LibVa.RcCqp);   // VA_RC_CQP
        Assert.Equal(2147483648u, (uint)LibVa.AttribNotSupported);   // VA_ATTRIB_NOT_SUPPORTED
        Assert.Equal(1u, (uint)LibVa.PackedHeaderSequence);   // VA_ENC_PACKED_HEADER_SEQUENCE
        Assert.Equal(2u, (uint)LibVa.PackedHeaderPicture);   // VA_ENC_PACKED_HEADER_PICTURE
        Assert.Equal(4u, (uint)LibVa.PackedHeaderSlice);   // VA_ENC_PACKED_HEADER_SLICE
        Assert.Equal(22u, (uint)LibVa.BufferSequence);   // VAEncSequenceParameterBufferType
        Assert.Equal(23u, (uint)LibVa.BufferPicture);   // VAEncPictureParameterBufferType
        Assert.Equal(24u, (uint)LibVa.BufferSlice);   // VAEncSliceParameterBufferType
        Assert.Equal(25u, (uint)LibVa.BufferPackedHeaderParameter);   // VAEncPackedHeaderParameterBufferType
        Assert.Equal(26u, (uint)LibVa.BufferPackedHeaderData);   // VAEncPackedHeaderDataBufferType
        Assert.Equal(27u, (uint)LibVa.BufferMiscParameter);   // VAEncMiscParameterBufferType
        Assert.Equal(21u, (uint)LibVa.BufferCoded);   // VAEncCodedBufferType
        Assert.Equal(0u, (uint)LibVa.MiscFrameRate);   // VAEncMiscParameterTypeFrameRate
        Assert.Equal(1u, (uint)LibVa.MiscRateControl);   // VAEncMiscParameterTypeRateControl
        Assert.Equal(5u, (uint)LibVa.MiscHrd);   // VAEncMiscParameterTypeHRD
        Assert.Equal(1u, (uint)LibVa.PackedSequence);   // VAEncPackedHeaderSequence
        Assert.Equal(2u, (uint)LibVa.PackedPicture);   // VAEncPackedHeaderPicture
        Assert.Equal(3u, (uint)LibVa.PackedSlice);   // VAEncPackedHeaderSlice
        Assert.Equal(1u, (uint)LibVa.PictureH264Invalid);   // VA_PICTURE_H264_INVALID
        Assert.Equal(8u, (uint)LibVa.PictureH264ShortTermReference);   // VA_PICTURE_H264_SHORT_TERM_REFERENCE
        Assert.Equal(1u, (uint)LibVa.PictureHevcInvalid);   // VA_PICTURE_HEVC_INVALID
        Assert.Equal(16u, (uint)LibVa.PictureHevcRpsStCurrBefore);   // VA_PICTURE_HEVC_RPS_ST_CURR_BEFORE
        Assert.Equal(4294967295u, (uint)LibVa.InvalidId);   // VA_INVALID_ID
        Assert.Equal(4294967295u, (uint)LibVa.InvalidSurface);   // VA_INVALID_SURFACE
        Assert.Equal(842094158u, (uint)LibVa.Nv12);   // VA_FOURCC_NV12
        Assert.Equal(0u, (uint)LibVa.StatusSuccess);   // VA_STATUS_SUCCESS
        Assert.Equal(1u, (uint)LibVa.Progressive);   // VA_PROGRESSIVE
        Assert.Equal(2u, LibVa.RcCbr);
        Assert.Equal(19, LibVa.AttribMaxPictureHeight);        // VAConfigAttribMaxPictureHeight
        Assert.Equal(51, LibVa.AttribEncHevcBlockSizes);       // VAConfigAttribEncHEVCBlockSizes
        Assert.Equal(1u, LibVa.LsbFirst);                      // VA_LSB_FIRST
        Assert.Equal(6u, new BitPacker().Add(1, false).Add(1, true).Add(1, true).Value);
    }
}
