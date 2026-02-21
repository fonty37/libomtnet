#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace libomtnet.quic
{
    /// <summary>
    /// QUIC-based OMT sender. Listens for incoming QUIC connections
    /// and sends video/audio/metadata frames to connected receivers.
    ///
    /// This is the QUIC equivalent of OMTSend. The wire protocol is
    /// identical — only the transport layer changes.
    ///
    /// Usage:
    ///   var sender = await OMTQuicSend.CreateAsync("My Source", OMTQuality.High);
    ///   sender.Send(videoFrame);
    ///   // receivers connect via QUIC and receive the same frames
    /// </summary>
    public class OMTQuicSend : IDisposable
    {
        private QuicListener listener;
        private readonly List<OMTQuicChannel> channels = new();
        private readonly object channelsLock = new();
        private readonly X509Certificate2 cert;
        private readonly CancellationTokenSource cts = new();

        private OMTFrame tempVideo;
        private OMTFrame tempAudio;
        private OMTBuffer tempAudioBuffer;
        private codecs.OMTVMX1Codec codec;
        private OMTQuality quality;
        private OMTQuality suggestedQuality;
        private string senderInfoXml;
        private readonly OMTClock videoClock;
        private readonly OMTClock audioClock;

        private readonly object videoLock = new();
        private readonly object audioLock = new();
        private readonly object metaLock = new();

        public int Port { get; private set; }
        public bool IsListening { get; private set; }
        public int ConnectionCount
        {
            get { lock (channelsLock) { return channels.Count; } }
        }

        private OMTQuicSend(OMTQuality quality, X509Certificate2 certificate, sync.IOMTTimeSource timeSource)
        {
            this.quality = quality;
            this.suggestedQuality = quality;
            this.cert = certificate;
            videoClock = new OMTClock(false, timeSource);
            audioClock = new OMTClock(true, timeSource);
            tempVideo = new OMTFrame(OMTFrameType.Video, new OMTBuffer(OMTConstants.VIDEO_MIN_SIZE, true));
            tempAudio = new OMTFrame(OMTFrameType.Audio, new OMTBuffer(OMTConstants.AUDIO_MIN_SIZE, true));
            tempAudioBuffer = new OMTBuffer(OMTConstants.AUDIO_MIN_SIZE, true);
        }

        /// <summary>
        /// Create and start a QUIC OMT sender.
        /// </summary>
        /// <param name="name">Source name (for discovery)</param>
        /// <param name="quality">Video encoding quality</param>
        /// <param name="port">UDP port to listen on (0 = auto from 6400-6600)</param>
        /// <param name="certificate">TLS certificate (null = generate self-signed)</param>
        public static async Task<OMTQuicSend> CreateAsync(
            string name,
            OMTQuality quality,
            int port = 0,
            X509Certificate2 certificate = null,
            sync.IOMTTimeSource timeSource = null)
        {
            if (!OMTQuicTransport.IsSupported)
                throw new NotSupportedException(
                    "QUIC is not supported on this platform. " +
                    "Requires Windows 11+ or Linux with libmsquic installed.");

            var cert = certificate ?? OMTQuicTransport.GenerateSelfSignedCert();
            var sender = new OMTQuicSend(quality, cert, timeSource);

            // Find an available port
            int startPort = port > 0 ? port : OMTConstants.NETWORK_PORT_START;
            int endPort = port > 0 ? port : OMTConstants.NETWORK_PORT_END;

            Exception lastEx = null;
            for (int p = startPort; p <= endPort; p++)
            {
                try
                {
                    sender.listener = await QuicListener.ListenAsync(new QuicListenerOptions
                    {
                        ListenEndPoint = new IPEndPoint(IPAddress.Any, p),
                        ApplicationProtocols = new List<SslApplicationProtocol> { OMTQuicTransport.AlpnProtocol },
                        ConnectionOptionsCallback = (_, _, _) =>
                            ValueTask.FromResult(new QuicServerConnectionOptions
                            {
                                DefaultStreamErrorCode = OMTQuicTransport.StreamErrorCode,
                                DefaultCloseErrorCode = OMTQuicTransport.ConnectionCloseCode,
                                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = cert,
                                    ApplicationProtocols = new List<SslApplicationProtocol> { OMTQuicTransport.AlpnProtocol }
                                }
                            })
                    }, sender.cts.Token);

                    sender.Port = p;
                    sender.IsListening = true;
                    OMTLogging.Write($"QUIC sender listening on UDP port {p}", "OMTQuicSend");

                    // Start accept loop
                    _ = sender.AcceptLoopAsync(sender.cts.Token);
                    return sender;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }

            throw lastEx ?? new Exception("Failed to bind to any port");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var quicConn = await listener.AcceptConnectionAsync(ct);
                    _ = HandleConnectionAsync(quicConn, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OMTLogging.Write(ex.ToString(), "OMTQuicSend.AcceptLoop");
                }
            }
        }

        private async Task HandleConnectionAsync(QuicConnection quicConn, CancellationToken ct)
        {
            try
            {
                var remote = quicConn.RemoteEndPoint;
                OMTLogging.Write($"QUIC connection from {remote}", "OMTQuicSend");

                // Accept the bidirectional stream from the receiver
                var stream = await quicConn.AcceptInboundStreamAsync(ct);

                var channel = new OMTQuicChannel(stream, remote,
                    OMTFrameType.Metadata, null, null);

                channel.StartReceive();

                // Send sender info if configured
                if (senderInfoXml != null)
                {
                    channel.Send(new OMTMetadata(0, senderInfoXml));
                }

                lock (channelsLock)
                {
                    channels.Add(channel);
                }

                channel.Changed += (sender, e) =>
                {
                    if (e.Type == OMTEventType.Disconnected)
                    {
                        lock (channelsLock) { channels.Remove(channel); }
                        channel.Dispose();
                        OMTLogging.Write($"QUIC receiver disconnected: {remote}", "OMTQuicSend");
                    }
                };

                OMTLogging.Write($"QUIC receiver connected: {remote}", "OMTQuicSend");
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTQuicSend.HandleConnection");
            }
        }

        /// <summary>
        /// Set sender information visible to receivers.
        /// </summary>
        public void SetSenderInformation(OMTSenderInfo senderInfo)
        {
            senderInfoXml = senderInfo?.ToXML();
            if (senderInfoXml != null)
            {
                SendToAll(new OMTMetadata(0, senderInfoXml));
            }
        }

        /// <summary>
        /// Send a media frame to all connected QUIC receivers.
        /// Supports Video, Audio, and Metadata frame types.
        /// </summary>
        public int Send(OMTMediaFrame frame)
        {
            if (frame.Type == OMTFrameType.Video)
                return SendVideo(frame);
            if (frame.Type == OMTFrameType.Audio)
                return SendAudio(frame);
            if (frame.Type == OMTFrameType.Metadata)
                return SendMetadata(frame);
            return 0;
        }

        private int SendToAll(OMTFrame frame)
        {
            int len = 0;
            OMTQuicChannel[] snapshot;
            lock (channelsLock)
            {
                snapshot = channels.ToArray();
            }
            foreach (var ch in snapshot)
            {
                if (ch.Connected)
                {
                    len += ch.Send(frame);
                }
            }
            return len;
        }

        private int SendToAll(OMTMetadata metadata)
        {
            int len = 0;
            OMTQuicChannel[] snapshot;
            lock (channelsLock)
            {
                snapshot = channels.ToArray();
            }
            foreach (var ch in snapshot)
            {
                if (ch.Connected)
                {
                    len += ch.Send(metadata);
                }
            }
            return len;
        }

        private int SendMetadata(OMTMediaFrame frame)
        {
            var m = OMTMetadata.FromMediaFrame(frame);
            return m != null ? SendToAll(m) : 0;
        }

        private int SendVideo(OMTMediaFrame frame)
        {
            lock (videoLock)
            {
                if (frame.Data == IntPtr.Zero || frame.DataLength <= 0) return 0;

                tempVideo.Data.Resize(frame.DataLength + frame.FrameMetadataLength);

                if (frame.Codec == (int)OMTCodec.VMX1)
                {
                    // Pre-encoded VMX1 passthrough
                    if (frame.DataLength > 0)
                    {
                        tempVideo.SetDataLength(frame.DataLength + frame.FrameMetadataLength);
                        tempVideo.SetMetadataLength(frame.FrameMetadataLength);
                        tempVideo.SetPreviewDataLength(frame.DataLength + frame.FrameMetadataLength);
                        System.Runtime.InteropServices.Marshal.Copy(frame.Data, tempVideo.Data.Buffer, 0, frame.DataLength);
                        if (frame.FrameMetadataLength > 0)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(frame.FrameMetadata,
                                tempVideo.Data.Buffer, frame.DataLength, frame.FrameMetadataLength);
                        }
                        tempVideo.ConfigureVideo((int)OMTCodec.VMX1, frame.Width, frame.Height,
                            frame.FrameRateN, frame.FrameRateD, frame.AspectRatio, frame.Flags, frame.ColorSpace);
                        videoClock.Process(ref frame);
                        tempVideo.Timestamp = frame.Timestamp;
                        return SendToAll(tempVideo);
                    }
                }
                else if (frame.Codec == (int)OMTCodec.AV1)
                {
                    // Pre-encoded AV1 passthrough
                    if (frame.DataLength > 0)
                    {
                        tempVideo.SetDataLength(frame.DataLength + frame.FrameMetadataLength);
                        tempVideo.SetMetadataLength(frame.FrameMetadataLength);
                        tempVideo.SetPreviewDataLength(frame.DataLength + frame.FrameMetadataLength);
                        System.Runtime.InteropServices.Marshal.Copy(frame.Data, tempVideo.Data.Buffer, 0, frame.DataLength);
                        if (frame.FrameMetadataLength > 0)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(frame.FrameMetadata,
                                tempVideo.Data.Buffer, frame.DataLength, frame.FrameMetadataLength);
                        }
                        tempVideo.ConfigureVideo((int)OMTCodec.AV1, frame.Width, frame.Height,
                            frame.FrameRateN, frame.FrameRateD, frame.AspectRatio, frame.Flags, frame.ColorSpace);
                        videoClock.Process(ref frame);
                        tempVideo.Timestamp = frame.Timestamp;
                        return SendToAll(tempVideo);
                    }
                }
                else
                {
                    // Encode to VMX1 (same logic as OMTSend)
                    if (frame.Width >= 16 && frame.Height >= 16 && frame.Stride >= frame.Width)
                    {
                        CreateCodec(frame);
                        byte[] buffer = tempVideo.Data.Buffer;
                        var itype = MapCodecToImageType(frame);
                        if (itype == codecs.VMXImageType.None) return 0;

                        int len = codec.Encode(itype, frame.Data, frame.Stride, buffer,
                            frame.Flags.HasFlag(OMTVideoFlags.Interlaced));

                        if (len > 0)
                        {
                            if (frame.FrameMetadataLength > 0)
                            {
                                tempVideo.Data.SetBuffer(len, len);
                                tempVideo.Data.Append(frame.FrameMetadata, 0, frame.FrameMetadataLength);
                            }
                            tempVideo.SetDataLength(len + frame.FrameMetadataLength);
                            tempVideo.SetMetadataLength(frame.FrameMetadataLength);
                            tempVideo.SetPreviewDataLength(codec.GetEncodedPreviewLength() + frame.FrameMetadataLength);
                            tempVideo.ConfigureVideo((int)OMTCodec.VMX1, frame.Width, frame.Height,
                                frame.FrameRateN, frame.FrameRateD, frame.AspectRatio, frame.Flags, frame.ColorSpace);
                            videoClock.Process(ref frame);
                            tempVideo.Timestamp = frame.Timestamp;
                            return SendToAll(tempVideo);
                        }
                    }
                }
                return 0;
            }
        }

        private int SendAudio(OMTMediaFrame frame)
        {
            lock (audioLock)
            {
                if (frame.Data == IntPtr.Zero || frame.DataLength <= 0 ||
                    frame.Channels <= 0 || frame.SampleRate <= 0 ||
                    frame.SamplesPerChannel <= 0 || frame.Channels > 32)
                    return 0;

                if (frame.DataLength > OMTConstants.AUDIO_MAX_SIZE)
                    return 0;

                // Pre-encoded Opus passthrough
                if (frame.Codec == (int)OMTCodec.OPUS)
                {
                    tempAudio.Data.Resize(frame.DataLength + frame.FrameMetadataLength);
                    System.Runtime.InteropServices.Marshal.Copy(frame.Data, tempAudio.Data.Buffer, 0, frame.DataLength);
                    if (frame.FrameMetadataLength > 0 && frame.FrameMetadata != IntPtr.Zero)
                        tempAudio.Data.Append(frame.FrameMetadata, 0, frame.FrameMetadataLength);
                    tempAudio.Data.SetBuffer(0, frame.DataLength);
                    tempAudio.SetDataLength(frame.DataLength + frame.FrameMetadataLength);
                    tempAudio.SetMetadataLength(frame.FrameMetadataLength);
                    tempAudio.ConfigureAudio(frame.SampleRate, frame.Channels, frame.SamplesPerChannel, 0, OMTCodec.OPUS);
                    audioClock.Process(ref frame);
                    tempAudio.Timestamp = frame.Timestamp;
                    return SendToAll(tempAudio);
                }

                tempAudioBuffer.Resize(frame.DataLength);
                tempAudio.Data.Resize(frame.DataLength + frame.FrameMetadataLength);
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, tempAudioBuffer.Buffer, 0, frame.DataLength);
                tempAudioBuffer.SetBuffer(0, frame.DataLength);
                tempAudio.Data.SetBuffer(0, 0);

                var ch = codecs.OMTFPA1Codec.Encode(tempAudioBuffer, frame.Channels, frame.SamplesPerChannel, tempAudio.Data);
                if (frame.FrameMetadataLength > 0 && frame.FrameMetadata != IntPtr.Zero)
                {
                    tempAudio.Data.Append(frame.FrameMetadata, 0, frame.FrameMetadataLength);
                }
                tempAudio.SetDataLength(tempAudio.Data.Length);
                tempAudio.SetMetadataLength(frame.FrameMetadataLength);
                tempAudio.ConfigureAudio(frame.SampleRate, frame.Channels, frame.SamplesPerChannel, ch);
                audioClock.Process(ref frame);
                tempAudio.Timestamp = frame.Timestamp;
                return SendToAll(tempAudio);
            }
        }

        private void CreateCodec(OMTMediaFrame frame)
        {
            var prof = codecs.VMXProfile.Default;
            if (suggestedQuality != OMTQuality.Default)
            {
                if (suggestedQuality >= OMTQuality.Low) prof = codecs.VMXProfile.OMT_LQ;
                if (suggestedQuality >= OMTQuality.Medium) prof = codecs.VMXProfile.OMT_SQ;
                if (suggestedQuality >= OMTQuality.High) prof = codecs.VMXProfile.OMT_HQ;
            }
            int fps = (int)frame.FrameRate;
            var cs = (codecs.VMXColorSpace)frame.ColorSpace;
            if (codec == null)
            {
                codec = new codecs.OMTVMX1Codec(frame.Width, frame.Height, fps, prof, cs);
            }
            else if (codec.Width != frame.Width || codec.Height != frame.Height ||
                     codec.Profile != prof || codec.ColorSpace != cs || codec.FramesPerSecond != fps)
            {
                int lastQ = codec.GetQuality();
                codec.Dispose();
                codec = new codecs.OMTVMX1Codec(frame.Width, frame.Height, fps, prof, cs);
                codec.SetQuality(lastQ);
            }
        }

        private static codecs.VMXImageType MapCodecToImageType(OMTMediaFrame frame)
        {
            bool alpha = frame.Flags.HasFlag(OMTVideoFlags.Alpha);
            return frame.Codec switch
            {
                (int)OMTCodec.UYVY => codecs.VMXImageType.UYVY,
                (int)OMTCodec.YUY2 => codecs.VMXImageType.YUY2,
                (int)OMTCodec.NV12 => codecs.VMXImageType.NV12,
                (int)OMTCodec.YV12 => codecs.VMXImageType.YV12,
                (int)OMTCodec.BGRA => alpha ? codecs.VMXImageType.BGRA : codecs.VMXImageType.BGRX,
                (int)OMTCodec.UYVA => alpha ? codecs.VMXImageType.UYVA : codecs.VMXImageType.UYVY,
                (int)OMTCodec.P216 => codecs.VMXImageType.P216,
                (int)OMTCodec.PA16 => alpha ? codecs.VMXImageType.PA16 : codecs.VMXImageType.P216,
                _ => codecs.VMXImageType.None
            };
        }

        public void Dispose()
        {
            cts.Cancel();
            listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            lock (channelsLock)
            {
                foreach (var ch in channels) ch.Dispose();
                channels.Clear();
            }
            codec?.Dispose();
            tempVideo?.Dispose();
            tempAudio?.Dispose();
            tempAudioBuffer?.Dispose();
            videoClock?.Dispose();
            audioClock?.Dispose();
            cert?.Dispose();
            cts.Dispose();
        }
    }
}
#endif
