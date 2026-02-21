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
    /// AV1 Encoder/Decoder for OMT using SVT-AV1 (encode) and dav1d (decode).
    /// Designed for WAN/cloud transport where bandwidth is limited.
    /// Mirrors the OMTVMX1Codec API for drop-in use in OMTSend/OMTQuicSend.
    ///
    /// VMX1 at 1080p uses 200+ Mbps; AV1 achieves similar quality at 5-12 Mbps.
    /// </summary>
    public class OMTAV1Codec : OMTBase
    {
        private readonly int width;
        private readonly int height;
        private readonly int framesPerSecond;
        private readonly int preset;
        private readonly int targetBitrate;

        // Encoder state
        private IntPtr encHandle;
        private IntPtr encConfigPtr;
        private bool encInitialized;
        private byte[] i420Y, i420U, i420V;

        // Decoder state
        private IntPtr decContext;
        private bool decInitialized;

        // Reusable unmanaged memory for encoder input
        private IntPtr ioFormatPtr;

        public OMTAV1Codec(int width, int height, int framesPerSecond,
            int preset = 10, int targetBitrateMbps = 6, int threads = 0)
        {
            this.width = width;
            this.height = height;
            this.framesPerSecond = framesPerSecond;
            this.preset = preset;
            this.targetBitrate = targetBitrateMbps * 1_000_000;

            int ySize = width * height;
            int uvSize = (width >> 1) * (height >> 1);
            i420Y = new byte[ySize];
            i420U = new byte[uvSize];
            i420V = new byte[uvSize];

            InitEncoder(threads);
            InitDecoder(threads);
        }

        private unsafe void InitEncoder(int threads)
        {
            // Allocate config struct in unmanaged memory - must be large enough for the
            // real EbSvtAv1EncConfiguration (typically ~1200-1600 bytes depending on version)
            int configSize = 4096; // Over-allocate for safety across SVT-AV1 versions
            encConfigPtr = Marshal.AllocHGlobal(configSize);
            {
                byte* p = (byte*)encConfigPtr;
                for (int i = 0; i < configSize; i++) p[i] = 0;
            }

            // init_handle allocates encoder, fills config with defaults
            int ret = SvtAv1Unmanaged.svt_av1_enc_init_handle(out encHandle, IntPtr.Zero, encConfigPtr);
            if (ret != SvtAv1Unmanaged.EB_ErrorNone)
                throw new Exception($"SVT-AV1 init_handle failed: 0x{ret:X8}");

            // Modify config fields at their known byte offsets
            // Offsets verified against SVT-AV1 v1.7.0 EbSvtAv1EncConfiguration using offsetof()
            byte* cfg = (byte*)encConfigPtr;

            // === Core settings ===
            cfg[0] = (byte)(sbyte)preset;                      // enc_mode (offset 0)
            *(int*)(cfg + 4) = framesPerSecond * 2;            // intra_period_length (offset 4): 2-second GOP
            *(uint*)(cfg + 12) = 0;                            // hierarchical_levels (offset 12): flat
            cfg[16] = 1;                                       // pred_structure (offset 16): low-delay
            *(uint*)(cfg + 20) = (uint)width;                  // source_width (offset 20)
            *(uint*)(cfg + 24) = (uint)height;                 // source_height (offset 24)
            *(uint*)(cfg + 36) = (uint)framesPerSecond;        // frame_rate_numerator (offset 36)
            *(uint*)(cfg + 40) = 1;                            // frame_rate_denominator (offset 40)
            *(uint*)(cfg + 44) = 8;                            // encoder_bit_depth (offset 44)
            *(int*)(cfg + 48) = SvtAv1Unmanaged.EB_YUV420;    // encoder_color_format (offset 48)
            *(int*)(cfg + 52) = 0;                             // profile (offset 52)

            // === Rate control: CBR for lowest latency ===
            cfg[120] = 2;                                      // rate_control_mode (offset 120): CBR
            *(uint*)(cfg + 132) = (uint)targetBitrate;         // target_bit_rate (offset 132)

            // === Low-latency optimizations ===
            *(int*)(cfg + 300) = 1;                            // enable_dlf_flag (offset 300): keep deblocking
            *(int*)(cfg + 304) = 0;                            // film_grain_denoise_strength (offset 304): off
            *(int*)(cfg + 312) = 0;                            // cdef_level (offset 312): disable CDEF
            *(int*)(cfg + 316) = 0;                            // enable_restoration_filtering (offset 316): disable
            *(int*)(cfg + 320) = 0;                            // enable_mfmv (offset 320): disable
            *(int*)(cfg + 324) = 0;                            // scene_change_detection (offset 324): off
            *(int*)(cfg + 332) = 1;                            // tile_columns (offset 332): 2 tile columns
            *(int*)(cfg + 336) = 0;                            // tile_rows (offset 336): 1 tile row
            *(uint*)(cfg + 340) = 0;                           // look_ahead_distance (offset 340): minimum
            cfg[344] = 0;                                      // enable_tpl_la (offset 344): disable
            *(uint*)(cfg + 348) = 0;                           // recode_loop (offset 348): no re-encoding
            *(uint*)(cfg + 352) = 0;                           // screen_content_mode (offset 352): off
            cfg[357] = 0;                                      // enable_tf (offset 357): disable temporal filter
            cfg[366] = 1;                                      // fast_decode (offset 366): ease decoder burden

            if (threads > 0)
                *(uint*)(cfg + 384) = (uint)threads;           // logical_processors (offset 384)

            ret = SvtAv1Unmanaged.svt_av1_enc_set_parameter(encHandle, encConfigPtr);
            if (ret != SvtAv1Unmanaged.EB_ErrorNone)
                throw new Exception($"SVT-AV1 set_parameter failed: 0x{ret:X8}");

            ret = SvtAv1Unmanaged.svt_av1_enc_init(encHandle);
            if (ret != SvtAv1Unmanaged.EB_ErrorNone)
                throw new Exception($"SVT-AV1 enc_init failed: 0x{ret:X8}");

            encInitialized = true;

            // Allocate SvtIOFormat struct in unmanaged memory (zeroed)
            int ioSize = Marshal.SizeOf<SvtAv1Unmanaged.SvtIOFormat>();
            ioFormatPtr = Marshal.AllocHGlobal(ioSize);
            {
                byte* z = (byte*)ioFormatPtr;
                for (int i = 0; i < ioSize; i++) z[i] = 0;
            }
        }

        private void InitDecoder(int threads)
        {
            var settings = new Dav1dUnmanaged.Dav1dSettings();
            Dav1dUnmanaged.dav1d_default_settings(ref settings);
            settings.n_threads = threads > 0 ? threads : Math.Min(4, Environment.ProcessorCount);
            settings.max_frame_delay = 1; // No frame pipeline delay
            settings.apply_grain = 0;     // Skip film grain synthesis

            int ret = Dav1dUnmanaged.dav1d_open(out decContext, ref settings);
            if (ret != 0)
                throw new Exception($"dav1d_open failed: {ret}");

            decInitialized = true;
        }

        /// <summary>
        /// Encode a video frame to AV1. Converts from the input pixel format to I420,
        /// then feeds to SVT-AV1. Returns encoded byte count (0 if encoder is buffering).
        /// </summary>
        public unsafe int Encode(VMXImageType itype, IntPtr src, int srcStride, byte[] dst, bool interlaced)
        {
            if (!encInitialized || src == IntPtr.Zero) return 0;

            // Convert input format to I420
            int halfW = width >> 1;
            int halfH = height >> 1;

            fixed (byte* pY = i420Y)
            fixed (byte* pU = i420U)
            fixed (byte* pV = i420V)
            {
                byte* srcPtr = (byte*)src;

                switch (itype)
                {
                    case VMXImageType.UYVY:
                        OMTPixelConverter.UYVYToI420(srcPtr, srcStride, width, height,
                            pY, width, pU, halfW, pV, halfW);
                        break;
                    case VMXImageType.YUY2:
                        OMTPixelConverter.YUY2ToI420(srcPtr, srcStride, width, height,
                            pY, width, pU, halfW, pV, halfW);
                        break;
                    case VMXImageType.NV12:
                        OMTPixelConverter.NV12ToI420(srcPtr, srcStride, width, height,
                            pY, width, pU, halfW, pV, halfW);
                        break;
                    case VMXImageType.YV12:
                        OMTPixelConverter.YV12ToI420(srcPtr, srcStride, width, height,
                            pY, width, pU, halfW, pV, halfW);
                        break;
                    case VMXImageType.BGRA:
                    case VMXImageType.BGRX:
                        OMTPixelConverter.BGRAToI420(srcPtr, srcStride, width, height,
                            pY, width, pU, halfW, pV, halfW);
                        break;
                    default:
                        return 0;
                }

                // Set up SvtIOFormat with plane pointers
                ref var ioFormat = ref *(SvtAv1Unmanaged.SvtIOFormat*)ioFormatPtr;
                ioFormat.luma = (IntPtr)pY;
                ioFormat.cb = (IntPtr)pU;
                ioFormat.cr = (IntPtr)pV;
                ioFormat.luma_ext = IntPtr.Zero;
                ioFormat.cb_ext = IntPtr.Zero;
                ioFormat.cr_ext = IntPtr.Zero;
                ioFormat.y_stride = (uint)width;
                ioFormat.cr_stride = (uint)halfW;
                ioFormat.cb_stride = (uint)halfW;
                ioFormat.width = (uint)width;
                ioFormat.height = (uint)height;
                ioFormat.org_x = 0;
                ioFormat.org_y = 0;
                ioFormat.color_fmt = SvtAv1Unmanaged.EB_YUV420;
                ioFormat.bit_depth = 8; // EB_EIGHT_BIT = 8

                // Create input buffer header
                uint yuvSize = (uint)(width * height + 2 * halfW * halfH);
                var inputHeader = new SvtAv1Unmanaged.EbBufferHeaderType();
                inputHeader.size = (uint)Marshal.SizeOf<SvtAv1Unmanaged.EbBufferHeaderType>();
                inputHeader.p_buffer = ioFormatPtr;
                inputHeader.n_filled_len = yuvSize;
                inputHeader.n_alloc_len = yuvSize;
                inputHeader.flags = 0;
                inputHeader.pts = 0;
                inputHeader.dts = 0;
                inputHeader.p_app_private = IntPtr.Zero;
                inputHeader.wrapper_ptr = IntPtr.Zero;
                inputHeader.metadata = IntPtr.Zero;

                int ret = SvtAv1Unmanaged.svt_av1_enc_send_picture(encHandle, ref inputHeader);
                if (ret != SvtAv1Unmanaged.EB_ErrorNone) return 0;
            }

            // Try to get encoded output
            return DrainEncoder(dst);
        }

        private unsafe int DrainEncoder(byte[] dst)
        {
            IntPtr outputPtr = IntPtr.Zero;
            int ret = SvtAv1Unmanaged.svt_av1_enc_get_packet(encHandle, out outputPtr, 0);

            if (ret == SvtAv1Unmanaged.EB_ErrorNone && outputPtr != IntPtr.Zero)
            {
                ref var output = ref *(SvtAv1Unmanaged.EbBufferHeaderType*)outputPtr;
                int len = (int)output.n_filled_len;

                if (len > 0 && len <= dst.Length)
                {
                    Marshal.Copy(output.p_buffer, dst, 0, len);
                }
                else
                {
                    len = 0;
                }

                SvtAv1Unmanaged.svt_av1_enc_release_out_buffer(ref outputPtr);
                return len;
            }

            return 0; // Encoder is buffering frames (pipeline fill)
        }

        /// <summary>
        /// Decode an AV1 frame. Feeds compressed data to dav1d and converts
        /// the output I420 to the requested pixel format.
        /// </summary>
        public unsafe bool Decode(VMXImageType itype, byte[] src, int srcLen, ref byte[] dst, int dstStride)
        {
            if (!decInitialized || src == null || srcLen <= 0) return false;

            // Pin source data and wrap for dav1d
            fixed (byte* srcPtr = src)
            {
                var data = new Dav1dUnmanaged.Dav1dData();
                int ret = Dav1dUnmanaged.dav1d_data_wrap(
                    ref data, (IntPtr)srcPtr, (UIntPtr)srcLen,
                    IntPtr.Zero, IntPtr.Zero);

                if (ret != 0) return false;

                ret = Dav1dUnmanaged.dav1d_send_data(decContext, ref data);
                // -EAGAIN means decoder has output ready, which is fine
                if (ret != 0 && ret != Dav1dUnmanaged.DAV1D_ERR_EAGAIN) return false;
            }

            // Try to get decoded picture
            var pic = new Dav1dUnmanaged.Dav1dPicture();
            int getRet = Dav1dUnmanaged.dav1d_get_picture(decContext, ref pic);
            if (getRet != 0) return false;

            try
            {
                int picW = pic.p.w;
                int picH = pic.p.h;
                int yStride = (int)(long)pic.stride0;
                int uvStride = (int)(long)pic.stride1;

                byte* srcY = (byte*)pic.data0;
                byte* srcU = (byte*)pic.data1;
                byte* srcV = (byte*)pic.data2;

                // Ensure output buffer is large enough
                int requiredSize;
                switch (itype)
                {
                    case VMXImageType.UYVY:
                    case VMXImageType.YUY2:
                        requiredSize = dstStride * picH;
                        break;
                    case VMXImageType.BGRA:
                    case VMXImageType.BGRX:
                        requiredSize = dstStride * picH;
                        break;
                    default:
                        requiredSize = dstStride * picH;
                        break;
                }

                if (dst == null || dst.Length < requiredSize)
                    dst = new byte[requiredSize];

                fixed (byte* dstPtr = dst)
                {
                    switch (itype)
                    {
                        case VMXImageType.UYVY:
                            OMTPixelConverter.I420ToUYVY(srcY, yStride, srcU, uvStride, srcV, uvStride,
                                picW, picH, dstPtr, dstStride);
                            break;
                        case VMXImageType.BGRA:
                        case VMXImageType.BGRX:
                            OMTPixelConverter.I420ToBGRA(srcY, yStride, srcU, uvStride, srcV, uvStride,
                                picW, picH, dstPtr, dstStride);
                            break;
                        default:
                            return false;
                    }
                }

                return true;
            }
            finally
            {
                Dav1dUnmanaged.dav1d_picture_unref(ref pic);
            }
        }

        protected override void DisposeInternal()
        {
            if (encInitialized)
            {
                // Send EOS to flush encoder
                var eosHeader = new SvtAv1Unmanaged.EbBufferHeaderType();
                eosHeader.size = (uint)Marshal.SizeOf<SvtAv1Unmanaged.EbBufferHeaderType>();
                eosHeader.flags = 1; // EB_BUFFERFLAG_EOS
                eosHeader.n_filled_len = 0;
                eosHeader.p_buffer = IntPtr.Zero;
                SvtAv1Unmanaged.svt_av1_enc_send_picture(encHandle, ref eosHeader);

                SvtAv1Unmanaged.svt_av1_enc_deinit(encHandle);
                SvtAv1Unmanaged.svt_av1_enc_deinit_handle(encHandle);
                encHandle = IntPtr.Zero;
                encInitialized = false;
            }

            if (ioFormatPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ioFormatPtr);
                ioFormatPtr = IntPtr.Zero;
            }

            if (encConfigPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(encConfigPtr);
                encConfigPtr = IntPtr.Zero;
            }

            if (decInitialized)
            {
                Dav1dUnmanaged.dav1d_close(ref decContext);
                decInitialized = false;
            }

            base.DisposeInternal();
        }

        public int Width => width;
        public int Height => height;
        public int FramesPerSecond => framesPerSecond;
        public int Preset => preset;
        public int TargetBitrate => targetBitrate;
    }
}
#endif
