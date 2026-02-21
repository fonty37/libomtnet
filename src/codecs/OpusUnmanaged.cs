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
    /// P/Invoke declarations for the Opus audio codec (libopus).
    /// Used by OMTOpusCodec for WAN/cloud audio compression.
    /// </summary>
    internal static class OpusUnmanaged
    {
        private const string DLLPATH = "opus";

        // ── Application types ───────────────────────────────────────
        internal const int OPUS_APPLICATION_VOIP = 2048;
        internal const int OPUS_APPLICATION_AUDIO = 2049;
        internal const int OPUS_APPLICATION_RESTRICTED_LOWDELAY = 2051;

        // ── Error codes ─────────────────────────────────────────────
        internal const int OPUS_OK = 0;
        internal const int OPUS_BAD_ARG = -1;
        internal const int OPUS_BUFFER_TOO_SMALL = -2;
        internal const int OPUS_INTERNAL_ERROR = -3;
        internal const int OPUS_INVALID_PACKET = -4;
        internal const int OPUS_UNIMPLEMENTED = -5;
        internal const int OPUS_INVALID_STATE = -6;
        internal const int OPUS_ALLOC_FAIL = -7;

        // ── CTL request codes ───────────────────────────────────────
        internal const int OPUS_SET_BITRATE_REQUEST = 4002;
        internal const int OPUS_GET_BITRATE_REQUEST = 4003;
        internal const int OPUS_SET_COMPLEXITY_REQUEST = 4010;
        internal const int OPUS_SET_SIGNAL_REQUEST = 4024;
        internal const int OPUS_SET_LSB_DEPTH_REQUEST = 4036;

        // ── Signal types ────────────────────────────────────────────
        internal const int OPUS_SIGNAL_MUSIC = 3002;

        // ── Encoder ─────────────────────────────────────────────────

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr opus_encoder_create(
            int Fs, int channels, int application, out int error);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void opus_encoder_destroy(IntPtr encoder);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int opus_encode_float(
            IntPtr encoder, float* pcm, int frame_size,
            byte* data, int max_data_bytes);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_ctl")]
        internal static extern int opus_encoder_ctl_set(
            IntPtr encoder, int request, int value);

        // ── Decoder ─────────────────────────────────────────────────

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr opus_decoder_create(
            int Fs, int channels, out int error);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void opus_decoder_destroy(IntPtr decoder);

        [DllImport(DLLPATH, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int opus_decode_float(
            IntPtr decoder, byte* data, int len,
            float* pcm, int frame_size, int decode_fec);
    }
}
#endif
