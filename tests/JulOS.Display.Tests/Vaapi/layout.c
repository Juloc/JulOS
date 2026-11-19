/* Prints the sizes and field offsets of the libva structures the VAAPI encoder uses, and the constants it uses.
 * LayoutTests.cs holds the same numbers; when libva changes, run this again:
 *   gcc -I<path to libva headers> layout.c -o layout && ./layout
 * (libva is MIT licensed; the headers are in the package libva-dev.) */
#include <stdio.h>
#include <stddef.h>
#include <va/va.h>
#include <va/va_enc_h264.h>
#include <va/va_enc_hevc.h>

#define S(t) printf("S %s %zu\n", #t, sizeof(t))
#define O(t, f) printf("O %s.%s %zu\n", #t, #f, offsetof(t, f))
#define C(n) printf("C %s %lld\n", #n, (long long)(n))

int main(void)
{
    S(VAConfigAttrib); O(VAConfigAttrib, value);
    S(VAPictureH264); O(VAPictureH264, frame_idx); O(VAPictureH264, flags); O(VAPictureH264, TopFieldOrderCnt); O(VAPictureH264, BottomFieldOrderCnt);
    S(VAPictureHEVC); O(VAPictureHEVC, pic_order_cnt); O(VAPictureHEVC, flags);
    S(VAEncSequenceParameterBufferH264);
    O(VAEncSequenceParameterBufferH264, level_idc); O(VAEncSequenceParameterBufferH264, intra_period); O(VAEncSequenceParameterBufferH264, intra_idr_period);
    O(VAEncSequenceParameterBufferH264, ip_period); O(VAEncSequenceParameterBufferH264, bits_per_second); O(VAEncSequenceParameterBufferH264, max_num_ref_frames);
    O(VAEncSequenceParameterBufferH264, picture_width_in_mbs); O(VAEncSequenceParameterBufferH264, picture_height_in_mbs); O(VAEncSequenceParameterBufferH264, seq_fields);
    O(VAEncSequenceParameterBufferH264, bit_depth_luma_minus8); O(VAEncSequenceParameterBufferH264, offset_for_ref_frame); O(VAEncSequenceParameterBufferH264, frame_cropping_flag);
    O(VAEncSequenceParameterBufferH264, frame_crop_left_offset); O(VAEncSequenceParameterBufferH264, frame_crop_bottom_offset);
    O(VAEncSequenceParameterBufferH264, vui_parameters_present_flag); O(VAEncSequenceParameterBufferH264, vui_fields); O(VAEncSequenceParameterBufferH264, aspect_ratio_idc);
    O(VAEncSequenceParameterBufferH264, num_units_in_tick); O(VAEncSequenceParameterBufferH264, time_scale);
    S(VAEncPictureParameterBufferH264);
    O(VAEncPictureParameterBufferH264, ReferenceFrames); O(VAEncPictureParameterBufferH264, coded_buf); O(VAEncPictureParameterBufferH264, pic_parameter_set_id);
    O(VAEncPictureParameterBufferH264, last_picture); O(VAEncPictureParameterBufferH264, frame_num); O(VAEncPictureParameterBufferH264, pic_init_qp);
    O(VAEncPictureParameterBufferH264, num_ref_idx_l0_active_minus1); O(VAEncPictureParameterBufferH264, chroma_qp_index_offset); O(VAEncPictureParameterBufferH264, pic_fields);
    S(VAEncSliceParameterBufferH264);
    O(VAEncSliceParameterBufferH264, num_macroblocks); O(VAEncSliceParameterBufferH264, macroblock_info); O(VAEncSliceParameterBufferH264, slice_type);
    O(VAEncSliceParameterBufferH264, idr_pic_id); O(VAEncSliceParameterBufferH264, pic_order_cnt_lsb); O(VAEncSliceParameterBufferH264, delta_pic_order_cnt);
    O(VAEncSliceParameterBufferH264, direct_spatial_mv_pred_flag); O(VAEncSliceParameterBufferH264, num_ref_idx_l0_active_minus1);
    O(VAEncSliceParameterBufferH264, RefPicList0); O(VAEncSliceParameterBufferH264, RefPicList1); O(VAEncSliceParameterBufferH264, luma_log2_weight_denom);
    O(VAEncSliceParameterBufferH264, cabac_init_idc); O(VAEncSliceParameterBufferH264, slice_qp_delta); O(VAEncSliceParameterBufferH264, disable_deblocking_filter_idc);
    S(VAEncSequenceParameterBufferHEVC);
    O(VAEncSequenceParameterBufferHEVC, general_tier_flag); O(VAEncSequenceParameterBufferHEVC, intra_period); O(VAEncSequenceParameterBufferHEVC, ip_period);
    O(VAEncSequenceParameterBufferHEVC, bits_per_second); O(VAEncSequenceParameterBufferHEVC, pic_width_in_luma_samples); O(VAEncSequenceParameterBufferHEVC, seq_fields);
    O(VAEncSequenceParameterBufferHEVC, log2_min_luma_coding_block_size_minus3); O(VAEncSequenceParameterBufferHEVC, max_transform_hierarchy_depth_intra);
    O(VAEncSequenceParameterBufferHEVC, pcm_sample_bit_depth_luma_minus1); O(VAEncSequenceParameterBufferHEVC, vui_parameters_present_flag);
    O(VAEncSequenceParameterBufferHEVC, vui_fields); O(VAEncSequenceParameterBufferHEVC, vui_time_scale); O(VAEncSequenceParameterBufferHEVC, min_spatial_segmentation_idc);
    O(VAEncSequenceParameterBufferHEVC, scc_fields);
    S(VAEncPictureParameterBufferHEVC);
    O(VAEncPictureParameterBufferHEVC, reference_frames); O(VAEncPictureParameterBufferHEVC, coded_buf); O(VAEncPictureParameterBufferHEVC, collocated_ref_pic_index);
    O(VAEncPictureParameterBufferHEVC, pic_init_qp); O(VAEncPictureParameterBufferHEVC, num_tile_columns_minus1); O(VAEncPictureParameterBufferHEVC, log2_parallel_merge_level_minus2);
    O(VAEncPictureParameterBufferHEVC, slice_pic_parameter_set_id); O(VAEncPictureParameterBufferHEVC, nal_unit_type); O(VAEncPictureParameterBufferHEVC, pic_fields);
    O(VAEncPictureParameterBufferHEVC, hierarchical_level_plus1); O(VAEncPictureParameterBufferHEVC, scc_fields);
    S(VAEncSliceParameterBufferHEVC);
    O(VAEncSliceParameterBufferHEVC, num_ctu_in_slice); O(VAEncSliceParameterBufferHEVC, slice_type); O(VAEncSliceParameterBufferHEVC, ref_pic_list0);
    O(VAEncSliceParameterBufferHEVC, ref_pic_list1); O(VAEncSliceParameterBufferHEVC, luma_log2_weight_denom); O(VAEncSliceParameterBufferHEVC, max_num_merge_cand);
    O(VAEncSliceParameterBufferHEVC, slice_qp_delta); O(VAEncSliceParameterBufferHEVC, slice_tc_offset_div2); O(VAEncSliceParameterBufferHEVC, slice_fields);
    O(VAEncSliceParameterBufferHEVC, pred_weight_table_bit_offset);
    S(VAEncPackedHeaderParameterBuffer); O(VAEncPackedHeaderParameterBuffer, bit_length); O(VAEncPackedHeaderParameterBuffer, has_emulation_bytes);
    S(VAEncMiscParameterBuffer); S(VAEncMiscParameterRateControl); O(VAEncMiscParameterRateControl, window_size); O(VAEncMiscParameterRateControl, initial_qp);
    O(VAEncMiscParameterRateControl, min_qp); O(VAEncMiscParameterRateControl, basic_unit_size); O(VAEncMiscParameterRateControl, rc_flags); O(VAEncMiscParameterRateControl, ICQ_quality_factor);
    O(VAEncMiscParameterRateControl, max_qp); O(VAEncMiscParameterRateControl, quality_factor);
    S(VAEncMiscParameterFrameRate); S(VAEncMiscParameterHRD); O(VAEncMiscParameterHRD, buffer_size);
    S(VAImageFormat); S(VAImage); O(VAImage, format); O(VAImage, buf); O(VAImage, width); O(VAImage, data_size); O(VAImage, num_planes); O(VAImage, pitches); O(VAImage, offsets);
    S(VACodedBufferSegment); O(VACodedBufferSegment, bit_offset); O(VACodedBufferSegment, status); O(VACodedBufferSegment, buf); O(VACodedBufferSegment, next);
    C(VA_PADDING_LOW); C(VA_PADDING_MEDIUM); C(VA_PADDING_HIGH);
    C(VAProfileH264ConstrainedBaseline); C(VAProfileH264Main); C(VAProfileH264High); C(VAProfileHEVCMain);
    C(VAEntrypointEncSlice); C(VAEntrypointEncSliceLP);
    C(VAConfigAttribRTFormat); C(VAConfigAttribRateControl); C(VAConfigAttribEncPackedHeaders); C(VAConfigAttribEncMaxRefFrames); C(VAConfigAttribEncMaxSlices);
    C(VAConfigAttribEncQualityRange); C(VAConfigAttribEncIntraRefresh); C(VAConfigAttribEncSliceStructure);
    C(VA_RT_FORMAT_YUV420); C(VA_RC_CBR); C(VA_RC_VBR); C(VA_RC_CQP); C(VA_ATTRIB_NOT_SUPPORTED);
    C(VA_ENC_PACKED_HEADER_SEQUENCE); C(VA_ENC_PACKED_HEADER_PICTURE); C(VA_ENC_PACKED_HEADER_SLICE);
    C(VAEncSequenceParameterBufferType); C(VAEncPictureParameterBufferType); C(VAEncSliceParameterBufferType); C(VAEncPackedHeaderParameterBufferType);
    C(VAEncPackedHeaderDataBufferType); C(VAEncMiscParameterBufferType); C(VAEncCodedBufferType);
    C(VAEncMiscParameterTypeFrameRate); C(VAEncMiscParameterTypeRateControl); C(VAEncMiscParameterTypeHRD); C(VAEncMiscParameterTypeQualityLevel);
    C(VAEncPackedHeaderSequence); C(VAEncPackedHeaderPicture); C(VAEncPackedHeaderSlice);
    C(VA_PICTURE_H264_INVALID); C(VA_PICTURE_H264_SHORT_TERM_REFERENCE); C(VA_PICTURE_HEVC_INVALID); C(VA_PICTURE_HEVC_RPS_ST_CURR_BEFORE);
    C(VA_INVALID_ID); C(VA_INVALID_SURFACE); C(VA_FOURCC_NV12); C(VA_STATUS_SUCCESS); C(VA_PROGRESSIVE);
    C(VA_CODED_BUF_STATUS_SLICE_OVERFLOW_MASK); C(VA_CODED_BUF_STATUS_SINGLE_NALU);
    C(VA_EXPORT_SURFACE_READ_ONLY);
    return 0;
}
