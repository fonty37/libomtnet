#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;

namespace libomtnet.codecs
{
    /// <summary>
    /// Pixel format converter for AV1 encode/decode pipeline.
    /// Converts between OMT pixel formats (UYVY, BGRA, NV12, etc.) and I420
    /// planar YUV required by SVT-AV1 and output by dav1d.
    /// </summary>
    internal static class OMTPixelConverter
    {
        // ── Encode-side: various formats → I420 ─────────────────────────

        /// <summary>
        /// Convert UYVY (packed 4:2:2) to I420 (planar 4:2:0).
        /// UYVY layout: [U0 Y0 V0 Y1] per 2 pixels.
        /// </summary>
        public static unsafe void UYVYToI420(
            byte* src, int srcStride, int width, int height,
            byte* dstY, int yStride, byte* dstU, int uStride, byte* dstV, int vStride)
        {
            int halfW = width >> 1;
            int halfH = height >> 1;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = src + y * srcStride;
                byte* yRow = dstY + y * yStride;

                for (int x = 0; x < halfW; x++)
                {
                    int srcIdx = x * 4;
                    yRow[x * 2] = srcRow[srcIdx + 1];
                    yRow[x * 2 + 1] = srcRow[srcIdx + 3];
                }

                if ((y & 1) == 0 && (y >> 1) < halfH)
                {
                    byte* srcRow2 = (y + 1 < height) ? src + (y + 1) * srcStride : srcRow;
                    byte* uRow = dstU + (y >> 1) * uStride;
                    byte* vRow = dstV + (y >> 1) * vStride;

                    for (int x = 0; x < halfW; x++)
                    {
                        int srcIdx = x * 4;
                        uRow[x] = (byte)((srcRow[srcIdx] + srcRow2[srcIdx] + 1) >> 1);
                        vRow[x] = (byte)((srcRow[srcIdx + 2] + srcRow2[srcIdx + 2] + 1) >> 1);
                    }
                }
            }
        }

        /// <summary>
        /// Convert YUY2 (packed 4:2:2) to I420 (planar 4:2:0).
        /// YUY2 layout: [Y0 U0 Y1 V0] per 2 pixels.
        /// </summary>
        public static unsafe void YUY2ToI420(
            byte* src, int srcStride, int width, int height,
            byte* dstY, int yStride, byte* dstU, int uStride, byte* dstV, int vStride)
        {
            int halfW = width >> 1;
            int halfH = height >> 1;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = src + y * srcStride;
                byte* yRow = dstY + y * yStride;

                for (int x = 0; x < halfW; x++)
                {
                    int srcIdx = x * 4;
                    yRow[x * 2] = srcRow[srcIdx];
                    yRow[x * 2 + 1] = srcRow[srcIdx + 2];
                }

                if ((y & 1) == 0 && (y >> 1) < halfH)
                {
                    byte* srcRow2 = (y + 1 < height) ? src + (y + 1) * srcStride : srcRow;
                    byte* uRow = dstU + (y >> 1) * uStride;
                    byte* vRow = dstV + (y >> 1) * vStride;

                    for (int x = 0; x < halfW; x++)
                    {
                        int srcIdx = x * 4;
                        uRow[x] = (byte)((srcRow[srcIdx + 1] + srcRow2[srcIdx + 1] + 1) >> 1);
                        vRow[x] = (byte)((srcRow[srcIdx + 3] + srcRow2[srcIdx + 3] + 1) >> 1);
                    }
                }
            }
        }

        /// <summary>
        /// Convert NV12 (semi-planar 4:2:0) to I420 (planar 4:2:0).
        /// NV12: Y plane followed by interleaved UV plane.
        /// </summary>
        public static unsafe void NV12ToI420(
            byte* src, int srcStride, int width, int height,
            byte* dstY, int yStride, byte* dstU, int uStride, byte* dstV, int vStride)
        {
            int halfW = width >> 1;
            int halfH = height >> 1;

            // Copy Y plane
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(src + y * srcStride, dstY + y * yStride, width, width);
            }

            // De-interleave UV plane
            byte* uvPlane = src + height * srcStride;
            for (int y = 0; y < halfH; y++)
            {
                byte* uvRow = uvPlane + y * srcStride;
                byte* uRow = dstU + y * uStride;
                byte* vRow = dstV + y * vStride;

                for (int x = 0; x < halfW; x++)
                {
                    uRow[x] = uvRow[x * 2];
                    vRow[x] = uvRow[x * 2 + 1];
                }
            }
        }

        /// <summary>
        /// Convert YV12 (planar 4:2:0, V before U) to I420 (planar 4:2:0, U before V).
        /// Only difference is U/V plane order.
        /// </summary>
        public static unsafe void YV12ToI420(
            byte* src, int srcStride, int width, int height,
            byte* dstY, int yStride, byte* dstU, int uStride, byte* dstV, int vStride)
        {
            int halfW = width >> 1;
            int halfH = height >> 1;
            int uvStride = srcStride >> 1;

            // Copy Y plane
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(src + y * srcStride, dstY + y * yStride, width, width);
            }

            // YV12: V plane first, then U plane
            byte* srcV = src + height * srcStride;
            byte* srcU = srcV + halfH * uvStride;

            for (int y = 0; y < halfH; y++)
            {
                Buffer.MemoryCopy(srcU + y * uvStride, dstU + y * uStride, halfW, halfW);
                Buffer.MemoryCopy(srcV + y * uvStride, dstV + y * vStride, halfW, halfW);
            }
        }

        /// <summary>
        /// Convert BGRA (packed 32-bit) to I420 (planar 4:2:0).
        /// Uses BT.709 color matrix for HD content.
        /// </summary>
        public static unsafe void BGRAToI420(
            byte* src, int srcStride, int width, int height,
            byte* dstY, int yStride, byte* dstU, int uStride, byte* dstV, int vStride)
        {
            int halfW = width >> 1;
            int halfH = height >> 1;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = src + y * srcStride;
                byte* yRow = dstY + y * yStride;

                for (int x = 0; x < width; x++)
                {
                    int si = x * 4;
                    int b = srcRow[si];
                    int g = srcRow[si + 1];
                    int r = srcRow[si + 2];

                    // BT.709: Y = 0.2126*R + 0.7152*G + 0.0722*B
                    yRow[x] = (byte)Clamp((54 * r + 183 * g + 18 * b + 128) >> 8);
                }

                if ((y & 1) == 0 && (y >> 1) < halfH)
                {
                    byte* srcRow2 = (y + 1 < height) ? src + (y + 1) * srcStride : srcRow;
                    byte* uRow = dstU + (y >> 1) * uStride;
                    byte* vRow = dstV + (y >> 1) * vStride;

                    for (int x = 0; x < halfW; x++)
                    {
                        int si0 = x * 2 * 4;
                        int si1 = si0 + 4;

                        // Average 2x2 block
                        int b = srcRow[si0] + srcRow[si1] + srcRow2[si0] + srcRow2[si1];
                        int g = srcRow[si0 + 1] + srcRow[si1 + 1] + srcRow2[si0 + 1] + srcRow2[si1 + 1];
                        int r = srcRow[si0 + 2] + srcRow[si1 + 2] + srcRow2[si0 + 2] + srcRow2[si1 + 2];
                        b >>= 2; g >>= 2; r >>= 2;

                        // BT.709: Cb = -0.1146*R - 0.3854*G + 0.5*B + 128
                        //         Cr =  0.5*R - 0.4542*G - 0.0458*B + 128
                        uRow[x] = (byte)Clamp(((-29 * r - 99 * g + 128 * b + 128) >> 8) + 128);
                        vRow[x] = (byte)Clamp(((128 * r - 116 * g - 12 * b + 128) >> 8) + 128);
                    }
                }
            }
        }

        // ── Decode-side: I420 → various formats ─────────────────────────

        /// <summary>
        /// Convert I420 (planar 4:2:0) to UYVY (packed 4:2:2).
        /// Chroma is duplicated vertically (4:2:0 → 4:2:2).
        /// </summary>
        public static unsafe void I420ToUYVY(
            byte* srcY, int yStride, byte* srcU, int uStride, byte* srcV, int vStride,
            int width, int height, byte* dst, int dstStride)
        {
            int halfW = width >> 1;

            for (int y = 0; y < height; y++)
            {
                byte* yRow = srcY + y * yStride;
                byte* uRow = srcU + (y >> 1) * uStride;
                byte* vRow = srcV + (y >> 1) * vStride;
                byte* dstRow = dst + y * dstStride;

                for (int x = 0; x < halfW; x++)
                {
                    int di = x * 4;
                    dstRow[di] = uRow[x];
                    dstRow[di + 1] = yRow[x * 2];
                    dstRow[di + 2] = vRow[x];
                    dstRow[di + 3] = yRow[x * 2 + 1];
                }
            }
        }

        /// <summary>
        /// Convert I420 (planar 4:2:0) to BGRA (packed 32-bit).
        /// Uses BT.709 color matrix for HD content.
        /// </summary>
        public static unsafe void I420ToBGRA(
            byte* srcY, int yStride, byte* srcU, int uStride, byte* srcV, int vStride,
            int width, int height, byte* dst, int dstStride)
        {
            for (int y = 0; y < height; y++)
            {
                byte* yRow = srcY + y * yStride;
                byte* uRow = srcU + (y >> 1) * uStride;
                byte* vRow = srcV + (y >> 1) * vStride;
                byte* dstRow = dst + y * dstStride;

                for (int x = 0; x < width; x++)
                {
                    int yVal = yRow[x] - 16;
                    int uVal = uRow[x >> 1] - 128;
                    int vVal = vRow[x >> 1] - 128;

                    // BT.709 inverse:
                    // R = 1.1644*Y + 1.7928*V
                    // G = 1.1644*Y - 0.2132*U - 0.5329*V
                    // B = 1.1644*Y + 2.1124*U
                    int c = 298 * yVal + 128;
                    int r = (c + 459 * vVal) >> 8;
                    int g = (c - 55 * uVal - 136 * vVal) >> 8;
                    int b = (c + 541 * uVal) >> 8;

                    int di = x * 4;
                    dstRow[di] = (byte)Clamp(b);
                    dstRow[di + 1] = (byte)Clamp(g);
                    dstRow[di + 2] = (byte)Clamp(r);
                    dstRow[di + 3] = 255;
                }
            }
        }

        private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
    }
}
#endif
