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
    /// P/Invoke declarations for the dav1d AV1 decoder (libdav1d).
    /// Used by OMTAV1Codec for WAN/cloud AV1 video decoding.
    /// </summary>
    internal static class Dav1dUnmanaged
    {
        private const string DLLPATH = "dav1d";

        // ── Pixel layout ────────────────────────────────────────────────

        internal const int DAV1D_PIXEL_LAYOUT_I400 = 0;
        internal const int DAV1D_PIXEL_LAYOUT_I420 = 1;
        internal const int DAV1D_PIXEL_LAYOUT_I422 = 2;
        internal const int DAV1D_PIXEL_LAYOUT_I444 = 3;

        // ── Error codes ─────────────────────────────────────────────────

        // dav1d uses negative errno values for errors, 0 = success
        // EAGAIN (-11 on Linux) means "try again" (need more data)
        internal const int DAV1D_ERR_EAGAIN = -11;

        // ── Structs ─────────────────────────────────────────────────────

        /// <summary>
        /// dav1d memory allocator callbacks. We use defaults (all zeros).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dav1dMemAllocator
        {
            public IntPtr cookie;
            public IntPtr alloc_picture_callback;
            public IntPtr release_picture_callback;
        }

        /// <summary>
        /// dav1d logger callbacks. We use defaults (all zeros = no logging).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dav1dLogger
        {
            public IntPtr cookie;
            public IntPtr callback;
        }

        /// <summary>
        /// dav1d initialization settings.
        /// Must be initialized via dav1d_default_settings() before use.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dav1dSettings
        {
            public int n_threads;
            public int max_frame_delay;
            public int apply_grain;
            public int operating_point;
            public int all_layers;
            public uint frame_size_limit;
            public Dav1dMemAllocator allocator;
            public Dav1dLogger logger;
            public int strict_std_compliance;
            public int output_invisible_frames;
            public int inloop_filters;
            public int decode_frame_type;
            // Reserved for ABI compatibility
            public unsafe fixed byte reserved[16];
        }

        /// <summary>
        /// Properties for dav1d data packets.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dav1dDataProps
        {
            public long timestamp;
            public long duration;
            public long offset;
            public UIntPtr size;
            public IntPtr user_data_ref;
            public IntPtr user_data;
        }

        /// <summary>
        /// Input data buffer for dav1d decoder.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dav1dData
        {
            public IntPtr data;          // const uint8_t*
            public UIntPtr sz;           // size_t
            public IntPtr @ref;          // Dav1dRef*
            public Dav1dDataProps m;     // metadata
        }

        /// <summary>
        /// Picture parameters (width, height, layout, bit depth).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dav1dPictureParameters
        {
            public int w;               // width
            public int h;               // height
            public int layout;          // Dav1dPixelLayout (I420=1)
            public int bpc;             // bits per component (8 or 10)
        }

        /// <summary>
        /// Decoded picture output from dav1d.
        /// Contains Y/U/V plane pointers and stride information.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dav1dPicture
        {
            public IntPtr seq_hdr;       // Dav1dSequenceHeader*
            public IntPtr frame_hdr;     // Dav1dFrameHeader*

            // Plane data: [0]=Y, [1]=U, [2]=V
            public IntPtr data0;
            public IntPtr data1;
            public IntPtr data2;

            // Strides: [0]=luma, [1]=chroma
            public IntPtr stride0;       // ptrdiff_t
            public IntPtr stride1;       // ptrdiff_t

            public Dav1dPictureParameters p;

            public IntPtr m_dummy0;      // Dav1dDataProps fields (timestamp)
            public IntPtr m_dummy1;      // duration
            public IntPtr m_dummy2;      // offset
            public IntPtr m_dummy3;      // size
            public IntPtr m_user_data_ref;
            public IntPtr m_user_data;

            public IntPtr content_light;         // Dav1dContentLightLevel*
            public IntPtr mastering_display;     // Dav1dMasteringDisplay*
            public IntPtr itut_t35;              // Dav1dITUTT35*
            public UIntPtr n_itut_t35;

            // Internal references
            public IntPtr reserved0;
            public IntPtr reserved1;
            public IntPtr frame_hdr_ref;
            public IntPtr seq_hdr_ref;
            public IntPtr content_light_ref;
            public IntPtr mastering_display_ref;
            public IntPtr itut_t35_ref;
            public UIntPtr reserved_ref;

            public IntPtr @ref;          // Dav1dRef*
            public IntPtr allocator_data;
        }

        // ── Functions ───────────────────────────────────────────────────

        /// <summary>
        /// Initialize settings to defaults. Must be called before dav1d_open.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void dav1d_default_settings(ref Dav1dSettings s);

        /// <summary>
        /// Open a dav1d decoder context.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int dav1d_open(out IntPtr c, ref Dav1dSettings s);

        /// <summary>
        /// Feed compressed AV1 data to the decoder.
        /// Returns 0 on success, -EAGAIN if decoder needs to output frames first.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int dav1d_send_data(IntPtr c, ref Dav1dData @in);

        /// <summary>
        /// Retrieve a decoded picture from the decoder.
        /// Returns 0 on success, -EAGAIN if more data is needed.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int dav1d_get_picture(IntPtr c, ref Dav1dPicture @out);

        /// <summary>
        /// Release a decoded picture's resources.
        /// Must be called for every successfully retrieved picture.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void dav1d_picture_unref(ref Dav1dPicture p);

        /// <summary>
        /// Flush the decoder, discarding all buffered data.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void dav1d_flush(IntPtr c);

        /// <summary>
        /// Close the decoder and free all resources.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void dav1d_close(ref IntPtr c);

        /// <summary>
        /// Create a dav1d data buffer from user-provided memory.
        /// The data must remain valid until the decoder consumes it.
        /// </summary>
        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int dav1d_data_wrap(
            ref Dav1dData data,
            IntPtr buf,
            UIntPtr buf_sz,
            IntPtr free_callback,
            IntPtr cookie);
    }
}
#endif
