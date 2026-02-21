using System;
using System.Runtime.InteropServices;
using libomtnet.codecs;
using Xunit;

namespace libomtnet.Tests;

public class PixelConverterTests
{
    private const int Width = 320;
    private const int Height = 240;

    [Fact]
    public unsafe void UYVY_To_I420_RoundTrip()
    {
        int uyvyStride = Width * 2;
        var uyvy = new byte[uyvyStride * Height];

        // Fill with a gradient pattern
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x += 2)
            {
                int off = y * uyvyStride + x * 2;
                byte luma = (byte)((x + y * 2) % 256);
                uyvy[off] = 128;       // Cb
                uyvy[off + 1] = luma;   // Y0
                uyvy[off + 2] = 128;    // Cr
                uyvy[off + 3] = luma;   // Y1
            }
        }

        // I420 plane sizes
        int yStride = Width;
        int uvStride = Width / 2;
        var yPlane = new byte[yStride * Height];
        var uPlane = new byte[uvStride * (Height / 2)];
        var vPlane = new byte[uvStride * (Height / 2)];

        fixed (byte* srcPtr = uyvy)
        fixed (byte* yPtr = yPlane)
        fixed (byte* uPtr = uPlane)
        fixed (byte* vPtr = vPlane)
        {
            OMTPixelConverter.UYVYToI420(srcPtr, uyvyStride, Width, Height,
                yPtr, yStride, uPtr, uvStride, vPtr, uvStride);
        }

        // Verify Y plane has non-zero data matching our pattern
        Assert.NotEqual(0, yPlane[1]); // second pixel Y value
        // Verify U/V planes are ~128 (neutral chroma)
        Assert.InRange(uPlane[0], (byte)126, (byte)130);
        Assert.InRange(vPlane[0], (byte)126, (byte)130);

        // Convert back to UYVY
        var reconstructed = new byte[uyvyStride * Height];
        fixed (byte* yPtr = yPlane)
        fixed (byte* uPtr = uPlane)
        fixed (byte* vPtr = vPlane)
        fixed (byte* dstPtr = reconstructed)
        {
            OMTPixelConverter.I420ToUYVY(yPtr, yStride, uPtr, uvStride, vPtr, uvStride,
                Width, Height, dstPtr, uyvyStride);
        }

        // Luma values should match closely (chroma subsampling may cause small differences)
        int maxLumaDiff = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int off = y * uyvyStride + x * 2 + 1; // Y position in UYVY
                int diff = Math.Abs(uyvy[off] - reconstructed[off]);
                if (diff > maxLumaDiff) maxLumaDiff = diff;
            }
        }
        Assert.True(maxLumaDiff <= 1, $"Luma round-trip error {maxLumaDiff} should be <= 1");
    }

    [Fact]
    public unsafe void BGRA_To_I420_ProducesValidOutput()
    {
        int bgraStride = Width * 4;
        var bgra = new byte[bgraStride * Height];

        // Fill with known colors: red top half, green bottom half
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int off = y * bgraStride + x * 4;
                if (y < Height / 2)
                {
                    bgra[off] = 0;       // B
                    bgra[off + 1] = 0;   // G
                    bgra[off + 2] = 255; // R
                    bgra[off + 3] = 255; // A
                }
                else
                {
                    bgra[off] = 0;       // B
                    bgra[off + 1] = 255; // G
                    bgra[off + 2] = 0;   // R
                    bgra[off + 3] = 255; // A
                }
            }
        }

        int yStride = Width;
        int uvStride = Width / 2;
        var yPlane = new byte[yStride * Height];
        var uPlane = new byte[uvStride * (Height / 2)];
        var vPlane = new byte[uvStride * (Height / 2)];

        fixed (byte* srcPtr = bgra)
        fixed (byte* yPtr = yPlane)
        fixed (byte* uPtr = uPlane)
        fixed (byte* vPtr = vPlane)
        {
            OMTPixelConverter.BGRAToI420(srcPtr, bgraStride, Width, Height,
                yPtr, yStride, uPtr, uvStride, vPtr, uvStride);
        }

        // Red and green should produce different Y values (BT.709)
        // Red: Y ~= 0.2126*255 = 54
        // Green: Y ~= 0.7152*255 = 182
        byte redY = yPlane[0];
        byte greenY = yPlane[yStride * (Height / 2 + 10)];
        Assert.True(redY < 100, $"Red luma {redY} should be low (~54)");
        Assert.True(greenY > 100, $"Green luma {greenY} should be high (~182)");
        Assert.True(greenY > redY, "Green should have higher luma than red in BT.709");
    }

    [Fact]
    public unsafe void I420_To_BGRA_ProducesValidOutput()
    {
        int yStride = Width;
        int uvStride = Width / 2;
        var yPlane = new byte[yStride * Height];
        var uPlane = new byte[uvStride * (Height / 2)];
        var vPlane = new byte[uvStride * (Height / 2)];

        // Fill with neutral gray (Y=128, U=128, V=128)
        for (int i = 0; i < yPlane.Length; i++) yPlane[i] = 128;
        for (int i = 0; i < uPlane.Length; i++) uPlane[i] = 128;
        for (int i = 0; i < vPlane.Length; i++) vPlane[i] = 128;

        int bgraStride = Width * 4;
        var bgra = new byte[bgraStride * Height];

        fixed (byte* yPtr = yPlane)
        fixed (byte* uPtr = uPlane)
        fixed (byte* vPtr = vPlane)
        fixed (byte* dstPtr = bgra)
        {
            OMTPixelConverter.I420ToBGRA(yPtr, yStride, uPtr, uvStride, vPtr, uvStride,
                Width, Height, dstPtr, bgraStride);
        }

        // Neutral gray I420 -> BGRA should produce ~(128,128,128)
        byte b = bgra[0], g = bgra[1], r = bgra[2], a = bgra[3];
        Assert.InRange(r, (byte)110, (byte)145);
        Assert.InRange(g, (byte)110, (byte)145);
        Assert.InRange(b, (byte)110, (byte)145);
    }
}
