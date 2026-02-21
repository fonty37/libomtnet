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
    public void Codec_Init_SetsProperties()
    {
        var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2, threads: 1);
        Assert.Equal(Width, codec.Width);
        Assert.Equal(Height, codec.Height);
        Assert.Equal(Fps, codec.FramesPerSecond);
        Assert.Equal(12, codec.Preset);
        // Note: Dispose() not called here as SVT-AV1 thread shutdown can
        // hang in CI. The GC finalizer will clean up after the test process.
    }

    [Fact]
    public unsafe void Encode_UYVY_ProducesCompressedOutput()
    {
        var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2, threads: 1);

        int srcStride = Width * 2;
        var src = new byte[srcStride * Height];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 128;     // Cb
            src[i + 1] = 128; // Y0
            src[i + 2] = 128; // Cr
            src[i + 3] = 128; // Y1
        }

        var dst = new byte[src.Length];
        var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            int encoded = 0;
            for (int f = 0; f < 30; f++)
            {
                encoded = codec.Encode(VMXImageType.UYVY, handle.AddrOfPinnedObject(), srcStride, dst, false);
                if (encoded > 0) break;
            }
            Assert.True(encoded > 0, "AV1 encoder should produce output after feeding frames");
            Assert.True(encoded < src.Length, "Compressed output should be smaller than raw UYVY");
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public unsafe void Encode_BGRA_ProducesCompressedOutput()
    {
        var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2, threads: 1);

        int srcStride = Width * 4;
        var src = new byte[srcStride * Height];
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
            for (int f = 0; f < 30; f++)
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

    [Fact]
    public unsafe void Encode_AchievesCompression()
    {
        var codec = new OMTAV1Codec(Width, Height, Fps, preset: 12, targetBitrateMbps: 2, threads: 1);

        int srcStride = Width * 2;
        var src = new byte[srcStride * Height];
        // Gradient pattern for meaningful compression
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x += 2)
            {
                int offset = y * srcStride + x * 2;
                byte luma = (byte)((x + y) % 256);
                src[offset] = 128;
                src[offset + 1] = luma;
                src[offset + 2] = 128;
                src[offset + 3] = luma;
            }
        }

        var compressed = new byte[src.Length];
        var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
        int encodedLen = 0;
        try
        {
            for (int f = 0; f < 30; f++)
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

        int rawSize = srcStride * Height;
        double ratio = (double)encodedLen / rawSize * 100.0;
        Assert.True(ratio < 50.0, $"AV1 should compress UYVY by at least 2x (got {ratio:F1}%, {encodedLen}/{rawSize} bytes)");
    }
}
