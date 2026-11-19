namespace JulOS.Integrations.Display.Video.Vaapi;

// What the H.264 hardware encoder is set up with, and the three headers a driver wants from the caller ("packed headers"): sequence
// parameter set, picture parameter set and slice header. They are written here, bit by bit, so that the stream says what we want it to say
// to the browser's decoder: the real picture size (cropped from the macroblock grid), the colour description of the software path, and in
// the VUI that no picture is reordered, so that a decoder shows every picture as soon as it has it.
//
// Fixed choices (a remote desktop, low delay): one slice per picture, one reference picture (every picture is one), no B pictures,
// picture order count type 0 with 8 bits, frame_num with 8 bits, deblocking on.
internal sealed record H264Layout(int Width, int Height, int Profile, int Level)
{
    public const int FrameNumBits = 8;
    public const int PocBits = 8;
    public const int InitQp = 26;

    public int AlignedWidth => (Width + 15) & ~15;

    public int AlignedHeight => (Height + 15) & ~15;

    public int MbWidth => AlignedWidth / 16;

    public int MbHeight => AlignedHeight / 16;

    public bool High => Profile == LibVa.ProfileH264High;

    // CABAC is the better entropy coder; the constrained baseline profile has only CAVLC.
    public bool Cabac => Profile != LibVa.ProfileH264ConstrainedBaseline;

    public bool Transform8x8 => High;

    public byte ProfileIdc => Profile switch { LibVa.ProfileH264High => 100, LibVa.ProfileH264Main => 77, _ => 66 };

    // constraint_set1_flag: constrained baseline
    public byte ConstraintFlags => Profile == LibVa.ProfileH264ConstrainedBaseline ? (byte)0x40 : (byte)0;

    // The smallest level the picture size and rate fit into, but at least 4.0 (what a decoder is happy with for a desktop at 30 pictures a second).
    public static int PickLevel(int width, int height, int framesPerSecond)
    {
        var macroblocks = ((width + 15) / 16) * ((height + 15) / 16);
        var rate = (long)macroblocks * Math.Max(1, framesPerSecond);
        (int Level, int MaxMbps, int MaxFs)[] levels =
        [
            (40, 245_760, 8_192), (42, 522_240, 8_704), (50, 589_824, 22_080), (51, 983_040, 36_864), (52, 2_073_600, 36_864),
        ];
        foreach (var l in levels)
        {
            if (macroblocks <= l.MaxFs && rate <= l.MaxMbps)
            {
                return l.Level;
            }
        }

        return 52;
    }

    public (byte[] Data, int Bits) Sps(bool emulationPrevention)
    {
        var w = new BitWriter();
        w.Bits(ProfileIdc, 8);
        w.Bits(ConstraintFlags, 8);
        w.Bits((uint)Level, 8);
        w.Ue(0);                                    // seq_parameter_set_id
        if (High)
        {
            w.Ue(1);                                // chroma_format_idc: 4:2:0
            w.Ue(0);                                // bit_depth_luma_minus8
            w.Ue(0);                                // bit_depth_chroma_minus8
            w.Bit(false);                           // qpprime_y_zero_transform_bypass_flag
            w.Bit(false);                           // seq_scaling_matrix_present_flag
        }

        w.Ue(FrameNumBits - 4);                     // log2_max_frame_num_minus4
        w.Ue(0);                                    // pic_order_cnt_type
        w.Ue(PocBits - 4);                          // log2_max_pic_order_cnt_lsb_minus4
        w.Ue(1);                                    // max_num_ref_frames
        w.Bit(false);                               // gaps_in_frame_num_value_allowed_flag
        w.Ue((uint)(MbWidth - 1));
        w.Ue((uint)(MbHeight - 1));
        w.Bit(true);                                // frame_mbs_only_flag
        w.Bit(true);                                // direct_8x8_inference_flag
        var cropRight = (AlignedWidth - Width) / 2;
        var cropBottom = (AlignedHeight - Height) / 2;
        if (cropRight != 0 || cropBottom != 0)
        {
            w.Bit(true);
            w.Ue(0);
            w.Ue((uint)cropRight);
            w.Ue(0);
            w.Ue((uint)cropBottom);
        }
        else
        {
            w.Bit(false);
        }

        w.Bit(true);                                // vui_parameters_present_flag
        w.Bit(false);                               // aspect_ratio_info_present_flag
        w.Bit(false);                               // overscan_info_present_flag
        w.Bit(true);                                // video_signal_type_present_flag
        w.Bits(5, 3);                               // video_format: unspecified
        w.Bit(false);                               // video_full_range_flag: limited range
        w.Bit(true);                                // colour_description_present_flag: what the software path says too
        w.Bits(1, 8);                               // BT.709 primaries
        w.Bits(13, 8);                              // sRGB transfer
        w.Bits(6, 8);                               // BT.601 matrix (the converter's)
        w.Bit(false);                               // chroma_loc_info_present_flag
        w.Bit(false);                               // timing_info_present_flag
        w.Bit(false);                               // nal_hrd_parameters_present_flag
        w.Bit(false);                               // vcl_hrd_parameters_present_flag
        w.Bit(false);                               // pic_struct_present_flag
        w.Bit(true);                                // bitstream_restriction_flag
        w.Bit(true);                                // motion_vectors_over_pic_boundaries_flag
        w.Ue(0);                                    // max_bytes_per_pic_denom
        w.Ue(0);                                    // max_bits_per_mb_denom
        w.Ue(15);                                   // log2_max_mv_length_horizontal
        w.Ue(15);                                   // log2_max_mv_length_vertical
        w.Ue(0);                                    // max_num_reorder_frames: nothing is reordered
        w.Ue(1);                                    // max_dec_frame_buffering
        w.Trailing();
        return w.ToNal([0x67], emulationPrevention);
    }

    public (byte[] Data, int Bits) Pps(bool emulationPrevention)
    {
        var w = new BitWriter();
        w.Ue(0);                                    // pic_parameter_set_id
        w.Ue(0);                                    // seq_parameter_set_id
        w.Bit(Cabac);                               // entropy_coding_mode_flag
        w.Bit(false);                               // bottom_field_pic_order_in_frame_present_flag
        w.Ue(0);                                    // num_slice_groups_minus1
        w.Ue(0);                                    // num_ref_idx_l0_default_active_minus1
        w.Ue(0);                                    // num_ref_idx_l1_default_active_minus1
        w.Bit(false);                               // weighted_pred_flag
        w.Bits(0, 2);                               // weighted_bipred_idc
        w.Se(InitQp - 26);                          // pic_init_qp_minus26
        w.Se(0);                                    // pic_init_qs_minus26
        w.Se(0);                                    // chroma_qp_index_offset
        w.Bit(true);                                // deblocking_filter_control_present_flag
        w.Bit(false);                               // constrained_intra_pred_flag
        w.Bit(false);                               // redundant_pic_cnt_present_flag
        if (High)
        {
            w.Bit(Transform8x8);                    // transform_8x8_mode_flag
            w.Bit(false);                           // pic_scaling_matrix_present_flag
            w.Se(0);                                // second_chroma_qp_index_offset
        }

        w.Trailing();
        return w.ToNal([0x68], emulationPrevention);
    }

    // The slice header of a picture that is one slice. Always raw (the driver inserts the emulation prevention bytes after its own slice data
    // is attached); the bit length is exact. With CABAC the header is padded with one bits to a byte boundary, which is where the slice data begins.
    public (byte[] Data, int Bits) SliceHeader(bool idr, int frameNum, int idrPicId, int pocLsb)
    {
        var w = new BitWriter();
        w.Ue(0);                                    // first_mb_in_slice
        w.Ue(idr ? 2u : 0u);                        // slice_type: I or P
        w.Ue(0);                                    // pic_parameter_set_id
        w.Bits((uint)frameNum, FrameNumBits);
        if (idr)
        {
            w.Ue((uint)idrPicId);
        }

        w.Bits((uint)pocLsb, PocBits);
        if (!idr)
        {
            w.Bit(false);                           // num_ref_idx_active_override_flag
            w.Bit(false);                           // ref_pic_list_modification_flag_l0
        }

        if (idr)
        {
            w.Bit(false);                           // no_output_of_prior_pics_flag
            w.Bit(false);                           // long_term_reference_flag
        }
        else
        {
            w.Bit(false);                           // adaptive_ref_pic_marking_mode_flag
        }

        if (Cabac && !idr)
        {
            w.Ue(0);                                // cabac_init_idc
        }

        w.Se(0);                                    // slice_qp_delta (the rate control sets the quantiser; the driver writes it)
        w.Ue(0);                                    // disable_deblocking_filter_idc: on
        w.Se(0);                                    // slice_alpha_c0_offset_div2
        w.Se(0);                                    // slice_beta_offset_div2
        if (Cabac)
        {
            while (!w.IsAligned)
            {
                w.Bit(true);                        // cabac_alignment_one_bit
            }
        }

        return w.ToNal([idr ? (byte)0x65 : (byte)0x61], false);
    }
}
