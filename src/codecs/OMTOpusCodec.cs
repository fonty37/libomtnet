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
    /// Opus audio encoder/decoder for OMT.
    /// Compresses 32-bit float PCM audio using libopus.
    /// FPA1 at 48kHz stereo uses ~384 kbps; Opus achieves transparent quality at 64-128 kbps.
    /// </summary>
    internal class OMTOpusCodec : OMTBase
    {
        private readonly int sampleRate;
        private readonly int channels;
        private readonly int bitrate;

        private IntPtr encoder;
        private IntPtr decoder;
        private bool initialized;

        // Reusable buffers for planar<->interleaved conversion
        private float[] interleavedBuffer;

        public OMTOpusCodec(int sampleRate, int channels, int bitrate = 128000)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.bitrate = bitrate;

            int err;
            encoder = OpusUnmanaged.opus_encoder_create(
                sampleRate, channels, OpusUnmanaged.OPUS_APPLICATION_AUDIO, out err);
            if (err != OpusUnmanaged.OPUS_OK || encoder == IntPtr.Zero)
                throw new Exception($"opus_encoder_create failed: {err}");

            OpusUnmanaged.opus_encoder_ctl_set(encoder, OpusUnmanaged.OPUS_SET_BITRATE_REQUEST, bitrate);
            OpusUnmanaged.opus_encoder_ctl_set(encoder, OpusUnmanaged.OPUS_SET_COMPLEXITY_REQUEST, 5);
            OpusUnmanaged.opus_encoder_ctl_set(encoder, OpusUnmanaged.OPUS_SET_SIGNAL_REQUEST, OpusUnmanaged.OPUS_SIGNAL_MUSIC);
            OpusUnmanaged.opus_encoder_ctl_set(encoder, OpusUnmanaged.OPUS_SET_LSB_DEPTH_REQUEST, 24);

            decoder = OpusUnmanaged.opus_decoder_create(sampleRate, channels, out err);
            if (err != OpusUnmanaged.OPUS_OK || decoder == IntPtr.Zero)
            {
                OpusUnmanaged.opus_encoder_destroy(encoder);
                encoder = IntPtr.Zero;
                throw new Exception($"opus_decoder_create failed: {err}");
            }

            initialized = true;
        }

        /// <summary>
        /// Encode planar float audio to an Opus packet.
        /// Input is in OMT's native planar format (channel-by-channel).
        /// Returns the compressed byte count written to dst.
        /// </summary>
        public unsafe int Encode(OMTBuffer planarSrc, int srcChannels, int samplesPerChannel, byte[] dst)
        {
            if (!initialized || encoder == IntPtr.Zero) return 0;

            int totalSamples = srcChannels * samplesPerChannel;
            if (interleavedBuffer == null || interleavedBuffer.Length < totalSamples)
                interleavedBuffer = new float[totalSamples];

            // Convert planar to interleaved: planar[ch][sample] -> interleaved[sample*channels+ch]
            fixed (byte* srcPtr = planarSrc.Buffer)
            fixed (float* ilPtr = interleavedBuffer)
            {
                float* planar = (float*)(srcPtr + planarSrc.Offset);
                for (int ch = 0; ch < srcChannels; ch++)
                {
                    int planarOffset = ch * samplesPerChannel;
                    for (int s = 0; s < samplesPerChannel; s++)
                    {
                        ilPtr[s * srcChannels + ch] = planar[planarOffset + s];
                    }
                }

                fixed (byte* dstPtr = dst)
                {
                    int encoded = OpusUnmanaged.opus_encode_float(
                        encoder, ilPtr, samplesPerChannel, dstPtr, dst.Length);
                    return encoded > 0 ? encoded : 0;
                }
            }
        }

        /// <summary>
        /// Decode an Opus packet to planar float audio.
        /// Output is in OMT's native planar format (channel-by-channel).
        /// Returns samples per channel decoded.
        /// </summary>
        public unsafe int Decode(byte[] src, int srcLen, OMTBuffer planarDst, int maxSamplesPerChannel)
        {
            if (!initialized || decoder == IntPtr.Zero) return 0;

            int totalSamples = channels * maxSamplesPerChannel;
            if (interleavedBuffer == null || interleavedBuffer.Length < totalSamples)
                interleavedBuffer = new float[totalSamples];

            int decoded;
            fixed (byte* srcPtr = src)
            fixed (float* ilPtr = interleavedBuffer)
            {
                decoded = OpusUnmanaged.opus_decode_float(
                    decoder, srcPtr, srcLen, ilPtr, maxSamplesPerChannel, 0);
            }

            if (decoded <= 0) return 0;

            // Convert interleaved to planar: interleaved[sample*channels+ch] -> planar[ch][sample]
            fixed (byte* dstPtr = planarDst.Buffer)
            fixed (float* ilPtr = interleavedBuffer)
            {
                float* planar = (float*)(dstPtr + planarDst.Offset);
                for (int ch = 0; ch < channels; ch++)
                {
                    int planarOffset = ch * decoded;
                    for (int s = 0; s < decoded; s++)
                    {
                        planar[planarOffset + s] = ilPtr[s * channels + ch];
                    }
                }
            }

            planarDst.SetBuffer(planarDst.Offset, channels * decoded * sizeof(float));
            return decoded;
        }

        public int SampleRate => sampleRate;
        public int Channels => channels;
        public int Bitrate => bitrate;

        protected override void DisposeInternal()
        {
            if (initialized)
            {
                if (encoder != IntPtr.Zero)
                {
                    OpusUnmanaged.opus_encoder_destroy(encoder);
                    encoder = IntPtr.Zero;
                }
                if (decoder != IntPtr.Zero)
                {
                    OpusUnmanaged.opus_decoder_destroy(decoder);
                    decoder = IntPtr.Zero;
                }
                initialized = false;
            }
            base.DisposeInternal();
        }
    }
}
#endif
