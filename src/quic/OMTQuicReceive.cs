#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using libomtnet.codecs;

namespace libomtnet.quic
{
    /// <summary>
    /// QUIC-based OMT receiver. Connects to a QUIC sender and receives
    /// video/audio/metadata frames.
    ///
    /// This is the QUIC equivalent of OMTReceive. The wire protocol is
    /// identical — only the transport layer changes.
    ///
    /// Usage:
    ///   var receiver = await OMTQuicReceive.ConnectAsync("192.168.1.10", 6400);
    ///   while (receiver.Receive(1000, ref frame)) { /* process frame */ }
    /// </summary>
    public class OMTQuicReceive : IDisposable
    {
        private QuicConnection connection;
        private OMTQuicChannel videoChannel;
        private OMTQuicChannel audioChannel;

        private AutoResetEvent videoReady = new AutoResetEvent(false);
        private AutoResetEvent audioReady = new AutoResetEvent(false);
        private AutoResetEvent metadataReady = new AutoResetEvent(false);

        private OMTVMX1Codec codec;
        private OMTFPA1Codec audioCodec;
        private OMTAV1Codec av1Codec;
        private OMTOpusCodec opusCodec;
        private OMTPinnedBuffer tempVideo;
        private OMTPinnedBuffer tempAudio;
        private int tempVideoStride;
        private readonly object videoLock = new();
        private readonly object audioLock = new();
        private readonly object metaLock = new();
        private IntPtr lastMetadata = IntPtr.Zero;

        private OMTPreferredVideoFormat preferredFormat = OMTPreferredVideoFormat.UYVY;
        private OMTReceiveFlags receiveFlags = OMTReceiveFlags.None;
        private CancellationTokenSource cts = new();
        private bool disposed;

        public bool IsConnected => videoChannel?.Connected == true;
        public IPEndPoint RemoteEndPoint { get; private set; }

        private OMTQuicReceive() { }

        /// <summary>
        /// Connect to a QUIC OMT sender.
        /// </summary>
        /// <param name="host">Sender hostname or IP</param>
        /// <param name="port">Sender QUIC port</param>
        /// <param name="preferredFormat">Preferred decoded video format</param>
        /// <param name="flags">Receive flags (preview, compressed, etc.)</param>
        public static async Task<OMTQuicReceive> ConnectAsync(
            string host,
            int port = 6400,
            OMTPreferredVideoFormat preferredFormat = OMTPreferredVideoFormat.UYVY,
            OMTReceiveFlags flags = OMTReceiveFlags.None)
        {
            if (!OMTQuicTransport.IsSupported)
                throw new NotSupportedException(
                    "QUIC is not supported on this platform. " +
                    "Requires Windows 11+ or Linux with libmsquic installed.");

            var receiver = new OMTQuicReceive();
            receiver.preferredFormat = preferredFormat;
            receiver.receiveFlags = flags;
            receiver.audioCodec = new OMTFPA1Codec(OMTConstants.AUDIO_MAX_SIZE);

            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Length == 0)
                throw new Exception($"Could not resolve host: {host}");

            var endpoint = new IPEndPoint(addresses[0], port);
            receiver.RemoteEndPoint = endpoint;

            receiver.connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = endpoint,
                DefaultStreamErrorCode = OMTQuicTransport.StreamErrorCode,
                DefaultCloseErrorCode = OMTQuicTransport.ConnectionCloseCode,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol>
                    {
                        OMTQuicTransport.AlpnProtocol
                    },
                    RemoteCertificateValidationCallback = (_, _, _, _) => true // Accept any cert
                }
            }, receiver.cts.Token);

            OMTLogging.Write($"QUIC connected to {endpoint}", "OMTQuicReceive");

            // Open a bidirectional stream for video + metadata
            var videoStream = await receiver.connection.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional, receiver.cts.Token);

            receiver.videoChannel = new OMTQuicChannel(videoStream, endpoint,
                OMTFrameType.Video, receiver.videoReady, receiver.metadataReady);

            receiver.videoChannel.StartReceive();

            // Subscribe to video and metadata
            receiver.videoChannel.Send(new OMTMetadata(0, OMTMetadataConstants.CHANNEL_SUBSCRIBE_VIDEO));
            receiver.videoChannel.Send(new OMTMetadata(0, OMTMetadataConstants.CHANNEL_SUBSCRIBE_METADATA));

            if (flags.HasFlag(OMTReceiveFlags.Preview))
            {
                receiver.videoChannel.Send(new OMTMetadata(0, OMTMetadataConstants.CHANNEL_PREVIEW_VIDEO_ON));
            }

            // Open a second stream for audio
            var audioStream = await receiver.connection.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional, receiver.cts.Token);

            receiver.audioChannel = new OMTQuicChannel(audioStream, endpoint,
                OMTFrameType.Audio, receiver.audioReady, null);

            receiver.audioChannel.StartReceive();
            receiver.audioChannel.Send(new OMTMetadata(0, OMTMetadataConstants.CHANNEL_SUBSCRIBE_AUDIO));

            OMTLogging.Write("QUIC receiver connected and subscribed", "OMTQuicReceive");
            return receiver;
        }

        /// <summary>
        /// Receive the next available video or audio frame.
        /// Blocks up to millisecondsTimeout.
        /// Returns true if a frame was received, false on timeout.
        /// </summary>
        public bool Receive(int millisecondsTimeout, ref OMTMediaFrame outFrame)
        {
            if (disposed) return false;

            // Check for ready frames first
            if (TryReceiveVideo(ref outFrame)) return true;
            if (TryReceiveAudio(ref outFrame)) return true;
            if (TryReceiveMetadata(ref outFrame)) return true;

            // Wait for any frame to arrive
            var handles = new WaitHandle[] { videoReady, audioReady, metadataReady };
            int which = WaitHandle.WaitAny(handles, millisecondsTimeout);
            if (which == WaitHandle.WaitTimeout) return false;

            if (which == 0 && TryReceiveVideo(ref outFrame)) return true;
            if (which == 1 && TryReceiveAudio(ref outFrame)) return true;
            if (which == 2 && TryReceiveMetadata(ref outFrame)) return true;

            return false;
        }

        private bool TryReceiveVideo(ref OMTMediaFrame outFrame)
        {
            if (videoChannel == null || videoChannel.ReadyFrameCount == 0) return false;

            lock (videoLock)
            {
                OMTFrame frame = videoChannel.ReceiveFrame();
                if (frame == null) return false;

                try
                {
                    OMTVideoHeader header = frame.GetVideoHeader();

                    outFrame.Type = OMTFrameType.Video;
                    outFrame.Timestamp = frame.Timestamp;
                    outFrame.Width = header.Width;
                    outFrame.Height = header.Height;
                    outFrame.FrameRateN = header.FrameRateN;
                    outFrame.FrameRateD = header.FrameRateD;
                    outFrame.AspectRatio = header.AspectRatio;
                    outFrame.Flags = (OMTVideoFlags)header.Flags;
                    outFrame.ColorSpace = (OMTColorSpace)header.ColorSpace;
                    outFrame.Codec = header.Codec;

                    if (receiveFlags.HasFlag(OMTReceiveFlags.CompressedOnly))
                    {
                        // Pass compressed data through without decoding
                        int frameLen = frame.Data.Length - frame.MetadataLength;
                        if (tempVideo == null || tempVideo.Length < frameLen)
                        {
                            tempVideo?.Dispose();
                            tempVideo = new OMTPinnedBuffer(frameLen);
                        }
                        Buffer.BlockCopy(frame.Data.Buffer, frame.Data.Offset, tempVideo.Buffer, 0, frameLen);
                        tempVideo.SetBuffer(0, frameLen);
                        outFrame.CompressedData = tempVideo.Pointer;
                        outFrame.CompressedLength = frameLen;
                        outFrame.DataLength = 0;
                    }
                    else if (header.Codec == (int)OMTCodec.VMX1)
                    {
                        // Decode VMX1
                        DecodeVideo(frame, header, ref outFrame);
                    }
                    else if (header.Codec == (int)OMTCodec.AV1)
                    {
                        DecodeAV1Video(frame, header, ref outFrame);
                    }
                    else
                    {
                        // Raw passthrough
                        int frameLen = frame.Data.Length - frame.MetadataLength;
                        if (tempVideo == null || tempVideo.Length < frameLen)
                        {
                            tempVideo?.Dispose();
                            tempVideo = new OMTPinnedBuffer(frameLen);
                        }
                        Buffer.BlockCopy(frame.Data.Buffer, frame.Data.Offset, tempVideo.Buffer, 0, frameLen);
                        tempVideo.SetBuffer(0, frameLen);
                        outFrame.Data = tempVideo.Pointer;
                        outFrame.DataLength = frameLen;
                    }

                    return true;
                }
                finally
                {
                    videoChannel.ReturnFrame(frame);
                }
            }
        }

        private void DecodeVideo(OMTFrame frame, OMTVideoHeader header, ref OMTMediaFrame outFrame)
        {
            int width = header.Width;
            int height = header.Height;
            OMTVideoFlags flags = (OMTVideoFlags)header.Flags;
            int fps = (int)OMTUtils.ToFrameRate(header.FrameRateN, header.FrameRateD);
            int frameLength = frame.Data.Length - frame.MetadataLength;

            if (codec == null || codec.Width != width || codec.Height != height)
            {
                codec?.Dispose();
                codec = new OMTVMX1Codec(width, height, fps, VMXProfile.Default,
                    (VMXColorSpace)header.ColorSpace);
            }

            // Determine output format based on preferences
            VMXImageType outputType;
            bool hasAlpha = flags.HasFlag(OMTVideoFlags.Alpha);
            bool highBit = flags.HasFlag(OMTVideoFlags.HighBitDepth);

            switch (preferredFormat)
            {
                case OMTPreferredVideoFormat.BGRA:
                    outputType = VMXImageType.BGRA;
                    break;
                case OMTPreferredVideoFormat.UYVYorBGRA:
                    outputType = hasAlpha ? VMXImageType.BGRA : VMXImageType.UYVY;
                    break;
                case OMTPreferredVideoFormat.UYVYorUYVA:
                    outputType = hasAlpha ? VMXImageType.UYVA : VMXImageType.UYVY;
                    break;
                case OMTPreferredVideoFormat.UYVYorUYVAorP216orPA16:
                    if (highBit)
                        outputType = hasAlpha ? VMXImageType.PA16 : VMXImageType.P216;
                    else
                        outputType = hasAlpha ? VMXImageType.UYVA : VMXImageType.UYVY;
                    break;
                case OMTPreferredVideoFormat.P216:
                    outputType = VMXImageType.P216;
                    break;
                default:
                    outputType = VMXImageType.UYVY;
                    break;
            }

            // Calculate stride based on output format
            int stride;
            switch (outputType)
            {
                case VMXImageType.BGRA:
                    stride = width * 4;
                    break;
                default:
                    stride = width * 2;
                    break;
            }

            // Ensure temp buffer is large enough (stride * height * 2 covers all formats)
            int bufLen = stride * height * 2;
            if (tempVideo == null || tempVideo.Length < bufLen)
            {
                tempVideo?.Dispose();
                tempVideo = new OMTPinnedBuffer(bufLen);
            }

            // Decode using byte[] based API (same pattern as OMTReceive)
            byte[] dst = tempVideo.Buffer;
            bool result = codec.Decode(outputType, frame.Data.Buffer, frameLength, ref dst, stride);

            if (result)
            {
                outFrame.Data = tempVideo.Pointer;
                outFrame.DataLength = stride * height;
                outFrame.Stride = stride;

                // Set codec based on output format
                outFrame.Codec = outputType switch
                {
                    VMXImageType.BGRA => (int)OMTCodec.BGRA,
                    VMXImageType.UYVA => (int)OMTCodec.UYVA,
                    VMXImageType.P216 => (int)OMTCodec.P216,
                    VMXImageType.PA16 => (int)OMTCodec.PA16,
                    _ => (int)OMTCodec.UYVY
                };
            }

            tempVideoStride = stride;
        }

        private void DecodeAV1Video(OMTFrame frame, OMTVideoHeader header, ref OMTMediaFrame outFrame)
        {
            OMTVideoFlags flags = (OMTVideoFlags)header.Flags;
            bool alpha = flags.HasFlag(OMTVideoFlags.Alpha);
            int frameLength = frame.Data.Length - frame.MetadataLength;
            int fps = (int)OMTUtils.ToFrameRate(header.FrameRateN, header.FrameRateD);

            if (receiveFlags.HasFlag(OMTReceiveFlags.CompressedOnly))
            {
                // Compressed passthrough — no decode
                if (tempVideo == null || tempVideo.Length < frameLength)
                {
                    tempVideo?.Dispose();
                    tempVideo = new OMTPinnedBuffer(frameLength);
                }
                Buffer.BlockCopy(frame.Data.Buffer, frame.Data.Offset, tempVideo.Buffer, 0, frameLength);
                tempVideo.SetBuffer(0, frameLength);
                outFrame.CompressedData = tempVideo.Pointer;
                outFrame.CompressedLength = frameLength;
                outFrame.DataLength = 0;
                outFrame.Codec = (int)OMTCodec.AV1;
                return;
            }

            if (av1Codec == null || av1Codec.Width != header.Width || av1Codec.Height != header.Height)
            {
                av1Codec?.Dispose();
                av1Codec = new OMTAV1Codec(header.Width, header.Height, fps);
            }

            VMXImageType outputType;
            if (preferredFormat == OMTPreferredVideoFormat.BGRA ||
                (preferredFormat == OMTPreferredVideoFormat.UYVYorBGRA && alpha))
            {
                outputType = VMXImageType.BGRA;
                tempVideoStride = header.Width * 4;
                outFrame.Codec = (int)OMTCodec.BGRA;
            }
            else
            {
                outputType = VMXImageType.UYVY;
                tempVideoStride = header.Width * 2;
                outFrame.Codec = (int)OMTCodec.UYVY;
            }

            int bufLen = tempVideoStride * header.Height * 2;
            if (tempVideo == null || tempVideo.Length < bufLen)
            {
                tempVideo?.Dispose();
                tempVideo = new OMTPinnedBuffer(bufLen);
            }

            byte[] dst = tempVideo.Buffer;
            bool result = av1Codec.Decode(outputType, frame.Data.Buffer, frameLength, ref dst, tempVideoStride);

            if (result)
            {
                outFrame.Data = tempVideo.Pointer;
                outFrame.DataLength = tempVideoStride * header.Height;
                outFrame.Stride = tempVideoStride;
            }
        }

        private bool TryReceiveAudio(ref OMTMediaFrame outFrame)
        {
            if (audioChannel == null || audioChannel.ReadyFrameCount == 0) return false;

            lock (audioLock)
            {
                OMTFrame frame = audioChannel.ReceiveFrame();
                if (frame == null) return false;

                try
                {
                    OMTAudioHeader header = frame.GetAudioHeader();

                    if (header.Codec == (int)OMTCodec.FPA1)
                    {
                        int len = header.SamplesPerChannel * header.Channels * OMTConstants.AUDIO_SAMPLE_SIZE;
                        if (len <= OMTConstants.AUDIO_MAX_SIZE)
                        {
                            if (tempAudio == null || tempAudio.Length < len)
                            {
                                tempAudio?.Dispose();
                                tempAudio = new OMTPinnedBuffer(len);
                            }
                            tempAudio.SetBuffer(0, 0);

                            // Decode FPA1 audio using instance method
                            audioCodec.Decode(frame.Data, header.Channels,
                                header.SamplesPerChannel, (OMTActiveAudioChannels)header.ActiveChannels, tempAudio);

                            outFrame.Type = OMTFrameType.Audio;
                            outFrame.Codec = (int)OMTCodec.FPA1;
                            outFrame.Timestamp = frame.Timestamp;
                            outFrame.SampleRate = header.SampleRate;
                            outFrame.Channels = header.Channels;
                            outFrame.SamplesPerChannel = header.SamplesPerChannel;
                            outFrame.Data = tempAudio.Pointer;
                            outFrame.DataLength = tempAudio.Length;

                            return true;
                        }
                    }
                    else if (header.Codec == (int)OMTCodec.OPUS)
                    {
                        int len = header.SamplesPerChannel * header.Channels * OMTConstants.AUDIO_SAMPLE_SIZE;
                        if (len <= OMTConstants.AUDIO_MAX_SIZE)
                        {
                            if (opusCodec == null || opusCodec.SampleRate != header.SampleRate || opusCodec.Channels != header.Channels)
                            {
                                opusCodec?.Dispose();
                                opusCodec = new OMTOpusCodec(header.SampleRate, header.Channels);
                            }

                            if (tempAudio == null || tempAudio.Length < len)
                            {
                                tempAudio?.Dispose();
                                tempAudio = new OMTPinnedBuffer(len);
                            }
                            tempAudio.SetBuffer(0, 0);

                            byte[] opusSrc = new byte[frame.Data.Length - frame.MetadataLength];
                            Buffer.BlockCopy(frame.Data.Buffer, frame.Data.Offset, opusSrc, 0, opusSrc.Length);

                            int decoded = opusCodec.Decode(opusSrc, opusSrc.Length, tempAudio, header.SamplesPerChannel);
                            if (decoded > 0)
                            {
                                outFrame.Type = OMTFrameType.Audio;
                                outFrame.Codec = (int)OMTCodec.FPA1; // Output as planar float
                                outFrame.Timestamp = frame.Timestamp;
                                outFrame.SampleRate = header.SampleRate;
                                outFrame.Channels = header.Channels;
                                outFrame.SamplesPerChannel = decoded;
                                outFrame.Data = tempAudio.Pointer;
                                outFrame.DataLength = tempAudio.Length;

                                return true;
                            }
                        }
                    }

                    return false;
                }
                finally
                {
                    audioChannel.ReturnFrame(frame);
                }
            }
        }

        private bool TryReceiveMetadata(ref OMTMediaFrame outFrame)
        {
            if (videoChannel == null || videoChannel.ReadyMetadataCount == 0) return false;

            lock (metaLock)
            {
                OMTMetadata meta = videoChannel.ReceiveMetadata();
                if (meta == null) return false;

                OMTMetadata.FreeIntPtr(lastMetadata);
                lastMetadata = IntPtr.Zero;

                outFrame.Type = OMTFrameType.Metadata;
                outFrame.Timestamp = meta.Timestamp;
                outFrame.Data = meta.ToIntPtr(ref outFrame.DataLength);
                lastMetadata = outFrame.Data;
                return true;
            }
        }

        /// <summary>
        /// Set the tally state (program/preview) on the sender.
        /// </summary>
        public void SetTally(OMTTally tally)
        {
            videoChannel?.Send(OMTMetadata.FromTally(tally));
        }

        /// <summary>
        /// Send metadata to the sender.
        /// </summary>
        public void SendMetadata(string xml)
        {
            videoChannel?.Send(new OMTMetadata(0, xml));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cts.Cancel();
            videoChannel?.Dispose();
            audioChannel?.Dispose();
            connection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            codec?.Dispose();
            audioCodec?.Dispose();
            av1Codec?.Dispose();
            opusCodec?.Dispose();
            tempVideo?.Dispose();
            tempAudio?.Dispose();
            OMTMetadata.FreeIntPtr(lastMetadata);
            videoReady?.Dispose();
            audioReady?.Dispose();
            metadataReady?.Dispose();
            cts.Dispose();
        }
    }
}
#endif
