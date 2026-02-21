#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;
using System.Runtime.InteropServices;

namespace libomtnet.codecs
{
    /// <summary>
    /// P/Invoke declarations for the SVT-AV1 encoder (libSvtAv1Enc).
    /// Used by OMTAV1Codec for WAN/cloud AV1 video encoding.
    /// </summary>
    internal static class SvtAv1Unmanaged
    {
        private const string DLLPATH = "SvtAv1Enc";

        // ── Error codes ──────────────────────────────────────────────

        internal const int EB_ErrorNone = 0;
        internal const int EB_ErrorInsufficientResources = unchecked((int)0x80001000);
        internal const int EB_ErrorUndefined = unchecked((int)0x80001001);
        internal const int EB_ErrorInvalidComponent = unchecked((int)0x80001004);
        internal const int EB_ErrorBadParameter = unchecked((int)0x80001005);
        internal const int EB_ErrorNoMoreFrames = unchecked((int)0x80001009);
        internal const int EB_NoErrorEmptyQueue = 0x00000002;

        // ── Color format ─────────────────────────────────────────────

        internal const int EB_YUV420 = 1;
        internal const int EB_YUV422 = 2;
        internal const int EB_YUV444 = 3;

        // ── Temporal layers max ──────────────────────────────────────

        internal const int EB_MAX_TEMPORAL_LAYERS = 6;

        // ── Structs ──────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct SvtAv1FixedBuf
        {
            public IntPtr buf;
            public UIntPtr sz;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EbContentLightLevel
        {
            public ushort max_cll;
            public ushort max_fall;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EbSvtAv1MasteringDisplayInfo
        {
            public ushort r_x, r_y;
            public ushort g_x, g_y;
            public ushort b_x, b_y;
            public ushort wp_x, wp_y;
            public uint max_luma;
            public uint min_luma;
        }

        /// <summary>
        /// SVT-AV1 encoder configuration. Fields must be in exact order
        /// matching the C struct for correct marshaling.
        /// We define the commonly-used fields and pad the rest.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct EbSvtAv1EncConfiguration
        {
            public sbyte enc_mode;                   // Preset 0-13 (10-12 for real-time)
            public int intra_period_length;          // GOP size (-2 to 2^31-2, -2=default, -1=infinite)
            public int intra_refresh_type;           // 1=CRA, 2=IDR
            public uint hierarchical_levels;         // Temporal layers (2-5)
            public byte pred_structure;              // 0=low-delay-P, 1=low-delay-B, 2=random-access
            public uint source_width;
            public uint source_height;
            public uint forced_max_frame_width;
            public uint forced_max_frame_height;
            public uint frame_rate_numerator;
            public uint frame_rate_denominator;
            public uint encoder_bit_depth;           // 8 or 10
            public int encoder_color_format;         // EB_YUV420, etc.
            public int profile;                      // 0=main, 1=high, 2=professional
            public uint tier;
            public uint level;
            public int color_primaries;
            public int transfer_characteristics;
            public int matrix_coefficients;
            public int color_range;
            public EbSvtAv1MasteringDisplayInfo mastering_display;
            public EbContentLightLevel content_light_level;
            public int chroma_sample_position;

            // Rate control
            public byte rate_control_mode;           // 0=CQP, 1=VBR, 2=CBR, 3=CRF
            public uint qp;                          // QP for CQP mode (1-63)
            public byte use_qp_file;
            public uint target_bit_rate;             // bits/sec for VBR/CBR
            public uint max_bit_rate;
            public uint max_qp_allowed;
            public uint min_qp_allowed;
            public uint vbr_min_section_pct;
            public uint vbr_max_section_pct;
            public uint under_shoot_pct;
            public uint over_shoot_pct;
            public uint mbr_over_shoot_pct;
            public long starting_buffer_level_ms;
            public long optimal_buffer_level_ms;
            public long maximum_buffer_size_ms;
            public SvtAv1FixedBuf rc_stats_buffer;
            public int pass;
            public byte use_fixed_qindex_offsets;
            public fixed int qindex_offsets[EB_MAX_TEMPORAL_LAYERS];
            public int key_frame_chroma_qindex_offset;
            public int key_frame_qindex_offset;
            public fixed int chroma_qindex_offsets[EB_MAX_TEMPORAL_LAYERS];
            public int luma_y_dc_qindex_offset;
            public int chroma_u_dc_qindex_offset;
            public int chroma_u_ac_qindex_offset;
            public int chroma_v_dc_qindex_offset;
            public int chroma_v_ac_qindex_offset;

            // Filtering
            public byte enable_dlf_flag;
            public uint film_grain_denoise_strength;
            public byte film_grain_denoise_apply;
            public int cdef_level;
            public int enable_restoration_filtering;
            public int enable_mfmv;
            public int enable_redundant_blk;
            public int enable_spatial_sse_full_loop_level;
            public int enable_over_bndry_blk;
            public int enable_new_nearest_comb_inject;
            public int enable_inter_intra_compound;
            public int enable_paeth;
            public int enable_smooth;
            public int enable_global_motion;
            public int enable_warped_motion;
            public int enable_obmc;
            public int enable_filter_intra;
            public int enable_intra_edge_filter;
            public int pic_based_rate_est;
            public int ext_block_flag;

            // Threading
            public uint logical_processors;
            public int pin_threads;
            public uint target_socket;

            // Misc
            public byte tile_columns;
            public byte tile_rows;

            // Pad remaining fields to ensure struct is large enough
            // The actual struct has many more fields + 128-byte ABI padding
            public fixed byte _reserved[512];
        }

        /// <summary>
        /// Input/output buffer for SVT-AV1 encoder.
        /// Used for both sending pictures and receiving encoded packets.
        /// Must match the exact C struct layout from EbSvtAv1.h v1.7.0.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EbBufferHeaderType
        {
            public uint size;                  // struct size
            public IntPtr p_buffer;            // data pointer
            public uint n_filled_len;          // valid data length
            public uint n_alloc_len;           // allocated size
            public IntPtr p_app_private;       // user data
            public IntPtr wrapper_ptr;         // internal
            public uint n_tick_count;          // tick count
            public long dts;                   // decode timestamp
            public long pts;                   // presentation timestamp
            public uint qp;                    // quantization parameter
            public int pic_type;               // EbAv1PictureType
            public ulong luma_sse;             // luma SSE
            public ulong cr_sse;               // Cr SSE
            public ulong cb_sse;               // Cb SSE
            public uint flags;                 // buffer flags
            public double luma_ssim;           // luma SSIM
            public double cr_ssim;             // Cr SSIM
            public double cb_ssim;             // Cb SSIM
            public IntPtr metadata;            // SvtMetadataArray*
        }

        /// <summary>
        /// I420 plane layout for SVT-AV1 input pictures.
        /// Matches the C struct EbSvtIOFormat exactly, including 10-bit extension planes.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SvtIOFormat
        {
            public IntPtr luma;                // Y plane pointer (uint8_t*)
            public IntPtr cb;                  // U plane pointer (uint8_t*)
            public IntPtr cr;                  // V plane pointer (uint8_t*)
            public IntPtr luma_ext;            // Y plane 10-bit extension (void*, null for 8-bit)
            public IntPtr cb_ext;              // U plane 10-bit extension (void*, null for 8-bit)
            public IntPtr cr_ext;              // V plane 10-bit extension (void*, null for 8-bit)
            public uint y_stride;              // Y plane stride
            public uint cr_stride;             // V plane stride (NOTE: cr before cb in SVT-AV1)
            public uint cb_stride;             // U plane stride
            public uint width;
            public uint height;
            public uint org_x;                 // origin X
            public uint org_y;                 // origin Y
            public int color_fmt;              // EbColorFormat enum
            public int bit_depth;              // EbBitDepth enum
        }

        // ── Functions ────────────────────────────────────────────────

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int svt_av1_enc_init_handle(
            out IntPtr enc_handle,
            IntPtr caller_handle,
            IntPtr config_ptr);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int svt_av1_enc_set_parameter(
            IntPtr enc_handle,
            IntPtr config_ptr);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int svt_av1_enc_init(IntPtr enc_handle);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int svt_av1_enc_send_picture(
            IntPtr enc_handle,
            ref EbBufferHeaderType p_buffer);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int svt_av1_enc_get_packet(
            IntPtr enc_handle,
            out IntPtr p_buffer,
            byte pic_send_done);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void svt_av1_enc_release_out_buffer(
            ref IntPtr p_buffer);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int svt_av1_enc_deinit(IntPtr enc_handle);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int svt_av1_enc_deinit_handle(IntPtr enc_handle);
    }
}
#endif
