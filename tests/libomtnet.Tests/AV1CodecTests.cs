using System;
using System.Runtime.InteropServices;
using libomtnet.codecs;
using Xunit;

namespace libomtnet.Tests;

public class AV1CodecTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 30;

    [Fact]
    public void Codec_InitAndDispose()
    {
        var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2);
        Assert.Equal(Width, codec.Width);
        Assert.Equal(Height, codec.Height);
        Assert.Equal(Fps, codec.FramesPerSecond);
        Assert.Equal(12, codec.Preset);
        codec.Dispose();
    }

    [Fact]
    public unsafe void Encode_UYVY_ProducesOutput()
    {
        using var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2);

        int srcStride = Width * 2; // UYVY = 2 bytes per pixel
        var src = new byte[srcStride * Height];
        // Fill with a valid UYVY pattern (Y=128, Cb=128, Cr=128 = neutral gray)
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 128;     // Cb
            src[i + 1] = 128; // Y0
            src[i + 2] = 128; // Cr
            src[i + 3] = 128; // Y1
        }

        var dst = new byte[src.Length]; // plenty of room for compressed
        var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            int encoded = 0;
            // AV1 encoders need multiple frames to produce output (buffering)
            // Feed several frames and check that at least one produces output
            for (int f = 0; f < 10; f++)
            {
                encoded = codec.Encode(VMXImageType.UYVY, handle.AddrOfPinnedObject(), srcStride, dst, false);
                if (encoded > 0) break;
            }
            Assert.True(encoded > 0, "AV1 encoder should produce output after feeding multiple frames");
            Assert.True(encoded < src.Length, "Compressed output should be smaller than raw UYVY");
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public unsafe void Encode_Decode_RoundTrip()
    {
        using var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2);

        int srcStride = Width * 2;
        var src = new byte[srcStride * Height];
        // Create a gradient pattern for meaningful encode
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x += 2)
            {
                int offset = y * srcStride + x * 2;
                byte luma = (byte)((x + y) % 256);
                src[offset] = 128;       // Cb
                src[offset + 1] = luma;   // Y0
                src[offset + 2] = 128;    // Cr
                src[offset + 3] = luma;   // Y1
            }
        }

        var compressed = new byte[src.Length];
        var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
        int encodedLen = 0;
        try
        {
            for (int f = 0; f < 10; f++)
            {
                encodedLen = codec.Encode(VMXImageType.UYVY, handle.AddrOfPinnedObject(), srcStride, compressed, false);
                if (encodedLen > 0) break;
            }
        }
        finally
        {
            handle.Free();
        }

        Assert.True(encodedLen > 0, "Encoder must produce output");

        // Decode
        var decoded = new byte[srcStride * Height];
        bool ok = codec.Decode(VMXImageType.UYVY, compressed, encodedLen, ref decoded, srcStride);
        Assert.True(ok, "Decode should succeed for a valid AV1 frame");
        Assert.True(decoded.Length > 0, "Decoded buffer should contain data");
    }

    [Fact]
    public unsafe void Encode_BGRA_ProducesOutput()
    {
        using var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2);

        int srcStride = Width * 4; // BGRA = 4 bytes per pixel
        var src = new byte[srcStride * Height];
        // Fill with a blue frame
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 255;     // B
            src[i + 1] = 0;   // G
            src[i + 2] = 0;   // R
            src[i + 3] = 255; // A
        }

        var dst = new byte[src.Length];
        var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            int encoded = 0;
            for (int f = 0; f < 10; f++)
            {
                encoded = codec.Encode(VMXImageType.BGRA, handle.AddrOfPinnedObject(), srcStride, dst, false);
                if (encoded > 0) break;
            }
            Assert.True(encoded > 0, "AV1 encoder should handle BGRA input");
        }
        finally
        {
            handle.Free();
        }
    }
}
