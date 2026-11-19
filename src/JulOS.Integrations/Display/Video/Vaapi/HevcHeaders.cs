namespace JulOS.Integrations.Display.Video.Vaapi;

// The same for HEVC (Main profile, 8 bit 4:2:0): the three parameter sets (VPS, SPS, PPS) and the slice header, written bit by bit.
// One slice per picture, one reference picture, no B pictures, no tiles, no SAO, no AMP, coding tree blocks as the driver allows
// (CtbLog2/MinCbLog2/... come from its VAConfigAttribEncHEVCBlockSizes), picture order count with 8 bits, no temporal MV prediction.
internal sealed record HevcLayout(int Width, int Height, int Level, int CtbLog2 = 5, int MinCbLog2 = 3, int MinTbLog2 = 2, int MaxTbLog2 = 5, int DepthInter = 2, int DepthIntra = 2)
{
    public const int PocBits = 8;
    public const int InitQp = 26;
    public const int MaxMergeCandidates = 5;

    // Coded size in whole minimum coding blocks, and at least a multiple of 16 so that every driver is content with it.
    public int AlignedWidth => (Width + 15) & ~15;

    public int AlignedHeight => (Height + 15) & ~15;

    public int Ctus => ((AlignedWidth + (1 << CtbLog2) - 1) >> CtbLog2) * ((AlignedHeight + (1 << CtbLog2) - 1) >> CtbLog2);

    // general_level_idc = 30 times the level: 4.0 is 120, 5.1 is 153.
    public static int PickLevel(int width, int height, int framesPerSecond)
    {
        var samples = (long)width * height;
        var rate = samples * Math.Max(1, framesPerSecond);
        (int Level, long MaxPs, long MaxSr)[] levels =
        [
            (120, 2_228_224, 66_846_720), (123, 2_228_224, 133_693_440), (150, 8_912_896, 267_386_880), (153, 8_912_896, 534_773_760),
            (156, 8_912_896, 1_069_547_520), (180, 35_651_584, 1_069_547_520),
        ];
        foreach (var l in levels)
        {
            if (samples <= l.MaxPs && rate <= l.MaxSr)
            {
                return l.Level;
            }
        }

        return 186;
    }

    private void ProfileTierLevel(BitWriter w)
    {
        w.Bits(0, 2);                               // general_profile_space
        w.Bit(false);                               // general_tier_flag: main tier
        w.Bits(1, 5);                               // general_profile_idc: Main
        w.Bits(0x60000000, 32);                     // general_profile_compatibility_flag: Main and Main 10 (bits 1 and 2)
        w.Bit(true);                                // general_progressive_source_flag
        w.Bit(false);                               // general_interlaced_source_flag
        w.Bit(false);                               // general_non_packed_constraint_flag
        w.Bit(true);                                // general_frame_only_constraint_flag
        w.Bits(0, 32);                              // general_reserved_zero_43bits (first 32) ...
        w.Bits(0, 11);                              // ... and the rest
        w.Bit(false);                               // general_reserved_zero_bit
        w.Bits((uint)Level, 8);                     // general_level_idc
    }

    public (byte[] Data, int Bits) Vps(bool emulationPrevention)
    {
        var w = new BitWriter();
        w.Bits(0, 4);                               // vps_video_parameter_set_id
        w.Bit(true);                                // vps_base_layer_internal_flag
        w.Bit(true);                                // vps_base_layer_available_flag
        w.Bits(0, 6);                               // vps_max_layers_minus1
        w.Bits(0, 3);                               // vps_max_sub_layers_minus1
        w.Bit(true);                                // vps_temporal_id_nesting_flag
        w.Bits(0xFFFF, 16);                         // vps_reserved_0xffff_16bits
        ProfileTierLevel(w);
        w.Bit(true);                                // vps_sub_layer_ordering_info_present_flag
        w.Ue(1);                                    // vps_max_dec_pic_buffering_minus1
        w.Ue(0);                                    // vps_max_num_reorder_pics
        w.Ue(0);                                    // vps_max_latency_increase_plus1
        w.Bits(0, 6);                               // vps_max_layer_id
        w.Ue(0);                                    // vps_num_layer_sets_minus1
        w.Bit(false);                               // vps_timing_info_present_flag
        w.Bit(false);                               // vps_extension_flag
        w.Trailing();
        return w.ToNal([0x40, 0x01], emulationPrevention);
    }

    public (byte[] Data, int Bits) Sps(bool emulationPrevention)
    {
        var w = new BitWriter();
        w.Bits(0, 4);                               // sps_video_parameter_set_id
        w.Bits(0, 3);                               // sps_max_sub_layers_minus1
        w.Bit(true);                                // sps_temporal_id_nesting_flag
        ProfileTierLevel(w);
        w.Ue(0);                                    // sps_seq_parameter_set_id
        w.Ue(1);                                    // chroma_format_idc: 4:2:0
        w.Ue((uint)AlignedWidth);
        w.Ue((uint)AlignedHeight);
        var cropRight = (AlignedWidth - Width) / 2;
        var cropBottom = (AlignedHeight - Height) / 2;
        if (cropRight != 0 || cropBottom != 0)
        {
            w.Bit(true);                            // conformance_window_flag (units of two samples)
            w.Ue(0);
            w.Ue((uint)cropRight);
            w.Ue(0);
            w.Ue((uint)cropBottom);
        }
        else
        {
            w.Bit(false);
        }

        w.Ue(0);                                    // bit_depth_luma_minus8
        w.Ue(0);                                    // bit_depth_chroma_minus8
        w.Ue(PocBits - 4);                          // log2_max_pic_order_cnt_lsb_minus4
        w.Bit(true);                                // sps_sub_layer_ordering_info_present_flag
        w.Ue(1);                                    // sps_max_dec_pic_buffering_minus1
        w.Ue(0);                                    // sps_max_num_reorder_pics
        w.Ue(0);                                    // sps_max_latency_increase_plus1
        w.Ue((uint)(MinCbLog2 - 3));                // log2_min_luma_coding_block_size_minus3
        w.Ue((uint)(CtbLog2 - MinCbLog2));          // log2_diff_max_min_luma_coding_block_size
        w.Ue((uint)(MinTbLog2 - 2));                // log2_min_luma_transform_block_size_minus2
        w.Ue((uint)(MaxTbLog2 - MinTbLog2));        // log2_diff_max_min_luma_transform_block_size
        w.Ue((uint)DepthInter);                     // max_transform_hierarchy_depth_inter
        w.Ue((uint)DepthIntra);                     // max_transform_hierarchy_depth_intra
        w.Bit(false);                               // scaling_list_enabled_flag
        w.Bit(false);                               // amp_enabled_flag
        w.Bit(false);                               // sample_adaptive_offset_enabled_flag
        w.Bit(false);                               // pcm_enabled_flag
        w.Ue(1);                                    // num_short_term_ref_pic_sets
        w.Ue(1);                                    // st_ref_pic_set(0): num_negative_pics
        w.Ue(0);                                    //   num_positive_pics
        w.Ue(0);                                    //   delta_poc_s0_minus1[0]: the picture before
        w.Bit(true);                                //   used_by_curr_pic_s0_flag[0]
        w.Bit(false);                               // long_term_ref_pics_present_flag
        w.Bit(false);                               // sps_temporal_mvp_enabled_flag
        w.Bit(true);                                // strong_intra_smoothing_enabled_flag
        w.Bit(true);                                // vui_parameters_present_flag
        w.Bit(false);                               // aspect_ratio_info_present_flag
        w.Bit(false);                               // overscan_info_present_flag
        w.Bit(true);                                // video_signal_type_present_flag
        w.Bits(5, 3);                               // video_format: unspecified
        w.Bit(false);                               // video_full_range_flag: limited range
        w.Bit(true);                                // colour_description_present_flag
        w.Bits(1, 8);                               // BT.709 primaries
        w.Bits(13, 8);                              // sRGB transfer
        w.Bits(6, 8);                               // BT.601 matrix
        w.Bit(false);                               // chroma_loc_info_present_flag
        w.Bit(false);                               // neutral_chroma_indication_flag
        w.Bit(false);                               // field_seq_flag
        w.Bit(false);                               // frame_field_info_present_flag
        w.Bit(false);                               // default_display_window_flag
        w.Bit(false);                               // vui_timing_info_present_flag
        w.Bit(true);                                // bitstream_restriction_flag
        w.Bit(false);                               // tiles_fixed_structure_flag
        w.Bit(true);                                // motion_vectors_over_pic_boundaries_flag
        w.Bit(true);                                // restricted_ref_pic_lists_flag
        w.Ue(0);                                    // min_spatial_segmentation_idc
        w.Ue(2);                                    // max_bytes_per_pic_denom
        w.Ue(1);                                    // max_bits_per_min_cu_denom
        w.Ue(15);                                   // log2_max_mv_length_horizontal
        w.Ue(15);                                   // log2_max_mv_length_vertical
        w.Bit(false);                               // sps_extension_present_flag
        w.Trailing();
        return w.ToNal([0x42, 0x01], emulationPrevention);
    }

    public (byte[] Data, int Bits) Pps(bool emulationPrevention)
    {
        var w = new BitWriter();
        w.Ue(0);                                    // pps_pic_parameter_set_id
        w.Ue(0);                                    // pps_seq_parameter_set_id
        w.Bit(false);                               // dependent_slice_segments_enabled_flag
        w.Bit(false);                               // output_flag_present_flag
        w.Bits(0, 3);                               // num_extra_slice_header_bits
        w.Bit(false);                               // sign_data_hiding_enabled_flag
        w.Bit(false);                               // cabac_init_present_flag
        w.Ue(0);                                    // num_ref_idx_l0_default_active_minus1
        w.Ue(0);                                    // num_ref_idx_l1_default_active_minus1
        w.Se(InitQp - 26);                          // init_qp_minus26
        w.Bit(false);                               // constrained_intra_pred_flag
        w.Bit(false);                               // transform_skip_enabled_flag
        w.Bit(true);                                // cu_qp_delta_enabled_flag: the rate control varies the quantiser inside a picture
        w.Ue(0);                                    // diff_cu_qp_delta_depth
        w.Se(0);                                    // pps_cb_qp_offset
        w.Se(0);                                    // pps_cr_qp_offset
        w.Bit(false);                               // pps_slice_chroma_qp_offsets_present_flag
        w.Bit(false);                               // weighted_pred_flag
        w.Bit(false);                               // weighted_bipred_flag
        w.Bit(false);                               // transquant_bypass_enabled_flag
        w.Bit(false);                               // tiles_enabled_flag
        w.Bit(false);                               // entropy_coding_sync_enabled_flag
        w.Bit(true);                                // pps_loop_filter_across_slices_enabled_flag
        w.Bit(true);                                // deblocking_filter_control_present_flag
        w.Bit(false);                               //   deblocking_filter_override_enabled_flag
        w.Bit(false);                               //   pps_deblocking_filter_disabled_flag
        w.Se(0);                                    //   pps_beta_offset_div2 (present because deblocking is not disabled)
        w.Se(0);                                    //   pps_tc_offset_div2
        w.Bit(false);                               // pps_scaling_list_data_present_flag
        w.Bit(false);                               // lists_modification_present_flag
        w.Ue(0);                                    // log2_parallel_merge_level_minus2
        w.Bit(false);                               // slice_segment_header_extension_present_flag
        w.Bit(false);                               // pps_extension_present_flag
        w.Trailing();
        return w.ToNal([0x44, 0x01], emulationPrevention);
    }

    // One slice segment that is the whole picture. IDR_W_RADL (19) for a key frame, TRAIL_R (1) for the others. The header ends with
    // byte_alignment(), so the slice data starts on a byte boundary.
    public (byte[] Data, int Bits) SliceHeader(bool idr, int pocLsb)
    {
        var w = new BitWriter();
        w.Bit(true);                                // first_slice_segment_in_pic_flag
        if (idr)
        {
            w.Bit(false);                           // no_output_of_prior_pics_flag
        }

        w.Ue(0);                                    // slice_pic_parameter_set_id
        w.Ue(idr ? 2u : 1u);                        // slice_type: I or P
        if (!idr)
        {
            w.Bits((uint)pocLsb, PocBits);          // slice_pic_order_cnt_lsb
            w.Bit(true);                            // short_term_ref_pic_set_sps_flag: set 0 (one set, so no index follows)
        }

        if (!idr)
        {
            w.Bit(false);                           // num_ref_idx_active_override_flag
            w.Ue((uint)(5 - MaxMergeCandidates));   // five_minus_max_num_merge_cand
        }

        w.Se(0);                                    // slice_qp_delta (the rate control sets the quantiser; the driver writes it)
        w.Bit(true);                                // slice_loop_filter_across_slices_enabled_flag
        w.Trailing();                               // byte_alignment()
        return w.ToNal(idr ? [0x26, 0x01] : [0x02, 0x01], false);
    }
}
