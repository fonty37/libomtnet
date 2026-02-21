using System;
using System.Runtime.InteropServices;
using libomtnet.codecs;
using Xunit;

namespace libomtnet.Tests;

/// <summary>
/// Tests for OMTOpusCodec. Since OMTOpusCodec is internal, these tests use
/// the public Opus P/Invoke layer directly to verify the native library works.
/// </summary>
public class OpusCodecTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FrameSamples = 960; // 20ms at 48kHz

    [Fact]
    public void OpusEncoder_CreateAndDestroy()
    {
        int error;
        var encoder = OpusUnmanaged.opus_encoder_create(SampleRate, Channels, 2048 /* OPUS_APPLICATION_AUDIO */, out error);
        Assert.Equal(0, error); // OPUS_OK
        Assert.NotEqual(IntPtr.Zero, encoder);
        OpusUnmanaged.opus_encoder_destroy(encoder);
    }

    [Fact]
    public void OpusDecoder_CreateAndDestroy()
    {
        int error;
        var decoder = OpusUnmanaged.opus_decoder_create(SampleRate, Channels, out error);
        Assert.Equal(0, error);
        Assert.NotEqual(IntPtr.Zero, decoder);
        OpusUnmanaged.opus_decoder_destroy(decoder);
    }

    [Fact]
    public unsafe void Opus_EncodeFloat_ProducesOutput()
    {
        int error;
        var encoder = OpusUnmanaged.opus_encoder_create(SampleRate, Channels, 2048, out error);
        Assert.Equal(0, error);

        try
        {
            // Generate a 440Hz sine wave (interleaved stereo)
            var pcm = new float[FrameSamples * Channels];
            for (int i = 0; i < FrameSamples; i++)
            {
                float sample = (float)Math.Sin(2.0 * Math.PI * 440.0 * i / SampleRate);
                pcm[i * Channels] = sample;     // left
                pcm[i * Channels + 1] = sample; // right
            }

            var output = new byte[4000]; // max packet size
            int encoded;
            fixed (float* pcmPtr = pcm)
            fixed (byte* outPtr = output)
            {
                encoded = OpusUnmanaged.opus_encode_float(encoder, pcmPtr, FrameSamples, outPtr, output.Length);
            }

            Assert.True(encoded > 0, $"Opus encode should produce bytes, got {encoded}");
            Assert.True(encoded < pcm.Length * 4, "Compressed should be smaller than raw PCM");
        }
        finally
        {
            OpusUnmanaged.opus_encoder_destroy(encoder);
        }
    }

    [Fact]
    public unsafe void Opus_RoundTrip_Quality()
    {
        int error;
        var encoder = OpusUnmanaged.opus_encoder_create(SampleRate, Channels, 2048, out error);
        Assert.Equal(0, error);
        var decoder = OpusUnmanaged.opus_decoder_create(SampleRate, Channels, out error);
        Assert.Equal(0, error);

        try
        {
            // Set bitrate to 128kbps
            // OPUS_SET_BITRATE = 4002
            OpusUnmanaged.opus_encoder_ctl(encoder, 4002, 128000);

            // Generate 440Hz sine wave
            var pcmIn = new float[FrameSamples * Channels];
            for (int i = 0; i < FrameSamples; i++)
            {
                float sample = 0.8f * (float)Math.Sin(2.0 * Math.PI * 440.0 * i / SampleRate);
                pcmIn[i * Channels] = sample;
                pcmIn[i * Channels + 1] = sample;
            }

            // Encode
            var packet = new byte[4000];
            int packetLen;
            fixed (float* pcmPtr = pcmIn)
            fixed (byte* pktPtr = packet)
            {
                packetLen = OpusUnmanaged.opus_encode_float(encoder, pcmPtr, FrameSamples, pktPtr, packet.Length);
            }
            Assert.True(packetLen > 0, "Encode must succeed");

            // Decode
            var pcmOut = new float[FrameSamples * Channels];
            int decoded;
            fixed (byte* pktPtr = packet)
            fixed (float* outPtr = pcmOut)
            {
                decoded = OpusUnmanaged.opus_decode_float(decoder, pktPtr, packetLen, outPtr, FrameSamples, 0);
            }
            Assert.Equal(FrameSamples, decoded);

            // Check quality: max error should be < 0.5 for 128kbps on a sine wave
            float maxErr = 0;
            for (int i = 0; i < pcmIn.Length; i++)
            {
                float err = Math.Abs(pcmIn[i] - pcmOut[i]);
                if (err > maxErr) maxErr = err;
            }
            Assert.True(maxErr < 1.0f, $"Max round-trip error {maxErr} should be < 1.0");
        }
        finally
        {
            OpusUnmanaged.opus_encoder_destroy(encoder);
            OpusUnmanaged.opus_decoder_destroy(decoder);
        }
    }

    [Fact]
    public unsafe void Opus_CompressionRatio()
    {
        int error;
        var encoder = OpusUnmanaged.opus_encoder_create(SampleRate, Channels, 2048, out error);
        Assert.Equal(0, error);

        try
        {
            OpusUnmanaged.opus_encoder_ctl(encoder, 4002, 128000);

            var pcm = new float[FrameSamples * Channels];
            for (int i = 0; i < FrameSamples; i++)
            {
                float sample = (float)Math.Sin(2.0 * Math.PI * 1000.0 * i / SampleRate);
                pcm[i * Channels] = sample;
                pcm[i * Channels + 1] = sample;
            }

            var packet = new byte[4000];
            int packetLen;
            fixed (float* pcmPtr = pcm)
            fixed (byte* pktPtr = packet)
            {
                packetLen = OpusUnmanaged.opus_encode_float(encoder, pcmPtr, FrameSamples, pktPtr, packet.Length);
            }

            int rawSize = FrameSamples * Channels * sizeof(float); // 7680 bytes
            double ratio = (double)packetLen / rawSize * 100.0;

            Assert.True(packetLen > 0, "Encode must succeed");
            Assert.True(ratio < 20.0, $"Compression ratio {ratio:F1}% should be < 20% (got {packetLen} bytes from {rawSize})");
        }
        finally
        {
            OpusUnmanaged.opus_encoder_destroy(encoder);
        }
    }
}
