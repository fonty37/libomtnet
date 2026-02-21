#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace libomtnet.quic
{
    /// <summary>
    /// QUIC-based channel that mirrors the functionality of OMTChannel
    /// but uses QuicStream instead of raw Socket + SocketAsyncEventArgs.
    ///
    /// QuicStream inherits from Stream, so frame data is read/written
    /// using standard async stream I/O. The OMT wire protocol
    /// (16-byte header + extended header + data) is identical.
    ///
    /// Key differences from TCP OMTChannel:
    ///   - No SocketAsyncEventArgs pool (uses Stream.ReadAsync/WriteAsync)
    ///   - No head-of-line blocking (each channel can use a separate QUIC stream)
    ///   - Built-in TLS 1.3 (no separate SslStream needed)
    /// </summary>
    internal class OMTQuicChannel : OMTBase
    {
        private static readonly int HeaderLength = (int)OMTFrameLength.Header;

        private QuicStream stream;
        private OMTFramePool framePool;
        private OMTFrame pendingFrame;
        private readonly Queue<OMTFrame> readyFrames;
        private AutoResetEvent frameReadyEvent;
        private OMTFrameType subscriptions = OMTFrameType.None;
        private readonly Queue<OMTMetadata> metadatas;
        private AutoResetEvent metadataReadyEvent;
        private OMTTally tally;
        private bool preview;
        private object lockSync = new object();
        private object sendSync = new object();
        private OMTQuality suggestedQuality = OMTQuality.Default;
        private OMTSenderInfo senderInfo = null;
        private IPEndPoint endPoint = null;
        private OMTStatistics statistics = new OMTStatistics();
        private CancellationTokenSource readCts;

        // Buffers for stream I/O
        private byte[] receiveBuffer;
        private SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);

        public delegate void ChangedEventHandler(object sender, OMTEventArgs e);
        public event ChangedEventHandler Changed;
        private OMTEventArgs tempEvent = new OMTEventArgs(OMTEventType.None);

        private string redirectAddress = null;
        public string RedirectAddress { get { return redirectAddress; } }
        public IPEndPoint RemoteEndPoint { get { return endPoint; } }

        public OMTQuicChannel(QuicStream quicStream, IPEndPoint remoteEndPoint,
            OMTFrameType receiveFrameType, AutoResetEvent frameReady, AutoResetEvent metadataReady)
        {
            stream = quicStream;
            endPoint = remoteEndPoint;
            readCts = new CancellationTokenSource();

            int poolCount;
            int startingFrameSize;
            if (receiveFrameType == OMTFrameType.Video)
            {
                poolCount = OMTConstants.VIDEO_FRAME_POOL_COUNT;
                startingFrameSize = OMTConstants.VIDEO_MIN_SIZE;
                receiveBuffer = new byte[OMTConstants.VIDEO_MAX_SIZE];
            }
            else if (receiveFrameType == OMTFrameType.Audio)
            {
                poolCount = OMTConstants.AUDIO_FRAME_POOL_COUNT;
                startingFrameSize = OMTConstants.AUDIO_MIN_SIZE;
                receiveBuffer = new byte[OMTConstants.AUDIO_MAX_SIZE];
            }
            else
            {
                poolCount = 1;
                startingFrameSize = OMTConstants.AUDIO_MIN_SIZE;
                receiveBuffer = new byte[OMTConstants.AUDIO_MAX_SIZE];
            }

            framePool = new OMTFramePool(poolCount, startingFrameSize, true);
            readyFrames = new Queue<OMTFrame>();
            frameReadyEvent = frameReady;
            metadatas = new Queue<OMTMetadata>();
            metadataReadyEvent = metadataReady;
        }

        protected void OnEvent(OMTEventType type)
        {
            tempEvent.Type = type;
            Changed?.Invoke(this, tempEvent);
        }

        public OMTQuality SuggestedQuality { get { return suggestedQuality; } }
        public OMTSenderInfo SenderInformation { get { return senderInfo; } }

        public bool Connected
        {
            get
            {
                if (stream == null) return false;
                return stream.CanRead || stream.CanWrite;
            }
        }

        public bool IsVideo() => subscriptions.HasFlag(OMTFrameType.Video);
        public bool IsAudio() => subscriptions.HasFlag(OMTFrameType.Audio);
        public bool IsMetadata() => subscriptions.HasFlag(OMTFrameType.Metadata);

        public int ReadyFrameCount
        {
            get { lock (readyFrames) { return readyFrames.Count; } }
        }

        public int ReadyMetadataCount
        {
            get { lock (metadatas) { return metadatas.Count; } }
        }

        public OMTStatistics GetStatistics()
        {
            OMTStatistics s = statistics;
            statistics.FramesSinceLast = 0;
            statistics.BytesSentSinceLast = 0;
            statistics.BytesReceivedSinceLast = 0;
            return s;
        }

        public OMTTally GetTally() => tally;

        // ── Send ────────────────────────────────────────────────────

        /// <summary>
        /// Send a frame over the QUIC stream. Thread-safe via lock.
        /// The wire format is identical to TCP: header + extended header + data.
        /// </summary>
        public int Send(OMTFrame frame)
        {
            lock (sendSync)
            {
                if (Exiting) return 0;
                int written = 0;
                try
                {
                    if ((frame.FrameType != OMTFrameType.Metadata) &&
                        (subscriptions & frame.FrameType) != frame.FrameType)
                    {
                        return 0;
                    }

                    frame.SetPreviewMode(preview);
                    int length = frame.Length;
                    if (length > OMTConstants.VIDEO_MAX_SIZE)
                    {
                        statistics.FramesDropped += 1;
                        return 0;
                    }

                    // Serialize frame into a contiguous buffer
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        frame.WriteHeaderTo(buffer, 0, length);
                        int headerLen = frame.HeaderLength + frame.ExtendedHeaderLength;
                        frame.WriteDataTo(buffer, 0, headerLen, length - headerLen);

                        // Write to QUIC stream
                        stream.Write(buffer, 0, length);
                        written = length;

                        if (frame.FrameType != OMTFrameType.Metadata)
                        {
                            statistics.Frames += 1;
                            statistics.FramesSinceLast += 1;
                        }
                        statistics.BytesSent += written;
                        statistics.BytesSentSinceLast += written;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch (Exception ex)
                {
                    OMTLogging.Write(ex.ToString(), "OMTQuicChannel.Send");
                }
                return written;
            }
        }

        public int Send(OMTMetadata metadata)
        {
            OMTBuffer m = OMTBuffer.FromMetadata(metadata.XML);
            OMTFrame frame = new OMTFrame(OMTFrameType.Metadata, m);
            frame.Timestamp = metadata.Timestamp;
            return Send(frame);
        }

        // ── Receive ─────────────────────────────────────────────────

        /// <summary>
        /// Start the async read loop. Reads frames from the QUIC stream
        /// using the same wire protocol as TCP.
        /// </summary>
        public void StartReceive()
        {
            _ = ReadLoopAsync(readCts.Token);
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !Exiting)
                {
                    // Read the 16-byte header into the start of receiveBuffer
                    if (!await ReadExactAsync(receiveBuffer, 0, HeaderLength, ct))
                    {
                        break; // stream closed
                    }

                    statistics.BytesReceived += HeaderLength;
                    statistics.BytesReceivedSinceLast += HeaderLength;

                    OMTFrame frame;
                    lock (lockSync)
                    {
                        if (pendingFrame == null)
                            pendingFrame = framePool.Get();

                        frame = pendingFrame;
                    }

                    // Parse header from the buffer
                    if (!frame.ReadHeaderFrom(receiveBuffer, 0, HeaderLength))
                    {
                        OMTLogging.Write("Invalid header", "OMTQuicChannel.ReadLoop");
                        break;
                    }

                    if (frame.FrameType != OMTFrameType.Video &&
                        frame.FrameType != OMTFrameType.Audio &&
                        frame.FrameType != OMTFrameType.Metadata)
                    {
                        OMTLogging.Write("Invalid frame type: " + frame.FrameType, "OMTQuicChannel.ReadLoop");
                        break;
                    }

                    // Read the remaining data (extended header + payload) after the header
                    int totalFrameLength = frame.Length;
                    int dataLength = totalFrameLength - HeaderLength;
                    if (dataLength > 0)
                    {
                        // Ensure receiveBuffer is large enough for the full frame
                        if (totalFrameLength > receiveBuffer.Length)
                        {
                            byte[] newBuffer = new byte[totalFrameLength];
                            Buffer.BlockCopy(receiveBuffer, 0, newBuffer, 0, HeaderLength);
                            receiveBuffer = newBuffer;
                        }

                        // Read remaining data right after the header in the same buffer
                        if (!await ReadExactAsync(receiveBuffer, HeaderLength, dataLength, ct))
                        {
                            break;
                        }

                        statistics.BytesReceived += dataLength;
                        statistics.BytesReceivedSinceLast += dataLength;

                        // Parse extended header and data from the combined buffer
                        frame.ReadExtendedHeaderFrom(receiveBuffer, 0, totalFrameLength);
                        frame.ReadDataFrom(receiveBuffer, 0, totalFrameLength);
                    }

                    lock (lockSync)
                    {
                        pendingFrame = null;
                    }

                    // Process the completed frame
                    if (ProcessMetadata(frame))
                    {
                        framePool.Return(frame);
                    }
                    else
                    {
                        if (framePool.Count > 0)
                        {
                            lock (readyFrames)
                            {
                                readyFrames.Enqueue(frame);
                            }
                            frameReadyEvent?.Set();
                            statistics.Frames += 1;
                            statistics.FramesSinceLast += 1;
                        }
                        else
                        {
                            statistics.FramesDropped += 1;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTQuicChannel.ReadLoop");
            }
            finally
            {
                OnEvent(OMTEventType.Disconnected);
            }
        }

        /// <summary>
        /// Read exactly count bytes from the QUIC stream.
        /// Returns false if the stream closed before any data.
        /// </summary>
        private async Task<bool> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct);
                if (read == 0)
                {
                    if (totalRead == 0) return false;
                    throw new EndOfStreamException($"QUIC stream closed after {totalRead}/{count} bytes");
                }
                totalRead += read;
            }
            return true;
        }

        public OMTFrame ReceiveFrame()
        {
            lock (readyFrames)
            {
                if (Exiting) return null;
                if (readyFrames.Count > 0) return readyFrames.Dequeue();
            }
            return null;
        }

        public void ReturnFrame(OMTFrame frame)
        {
            lock (readyFrames)
            {
                if (frame != null)
                {
                    if (Exiting) frame.Dispose();
                    else framePool.Return(frame);
                }
            }
        }

        public OMTMetadata ReceiveMetadata()
        {
            lock (metadatas)
            {
                if (Exiting) return null;
                if (metadatas.Count > 0) return metadatas.Dequeue();
            }
            return null;
        }

        // ── Metadata processing (same logic as OMTChannel) ──────────

        private void UpdateTally(OMTTally t)
        {
            if (t.Preview != tally.Preview || t.Program != tally.Program)
            {
                tally = t;
                OnEvent(OMTEventType.TallyChanged);
            }
        }

        private bool ProcessMetadata(OMTFrame frame)
        {
            if (frame.FrameType == OMTFrameType.Metadata)
            {
                string xml = frame.Data.ToMetadata();
                if (xml == OMTMetadataConstants.CHANNEL_SUBSCRIBE_VIDEO)
                {
                    subscriptions |= OMTFrameType.Video;
                    return true;
                }
                else if (xml == OMTMetadataConstants.CHANNEL_SUBSCRIBE_AUDIO)
                {
                    subscriptions |= OMTFrameType.Audio;
                    return true;
                }
                else if (xml == OMTMetadataConstants.CHANNEL_SUBSCRIBE_METADATA)
                {
                    subscriptions |= OMTFrameType.Metadata;
                    return true;
                }
                else if (xml == OMTMetadataConstants.TALLY_PREVIEWPROGRAM)
                {
                    UpdateTally(new OMTTally(1, 1));
                    return true;
                }
                else if (xml == OMTMetadataConstants.TALLY_PROGRAM)
                {
                    UpdateTally(new OMTTally(0, 1));
                    return true;
                }
                else if (xml == OMTMetadataConstants.TALLY_PREVIEW)
                {
                    UpdateTally(new OMTTally(1, 0));
                    return true;
                }
                else if (xml == OMTMetadataConstants.TALLY_NONE)
                {
                    UpdateTally(new OMTTally(0, 0));
                    return true;
                }
                else if (xml == OMTMetadataConstants.CHANNEL_PREVIEW_VIDEO_ON)
                {
                    preview = true;
                    return true;
                }
                else if (xml == OMTMetadataConstants.CHANNEL_PREVIEW_VIDEO_OFF)
                {
                    preview = false;
                    return true;
                }
                else if (xml.StartsWith(OMTMetadataTemplates.SUGGESTED_QUALITY_PREFIX))
                {
                    XmlDocument doc = OMTMetadataUtils.TryParse(xml);
                    if (doc != null)
                    {
                        XmlNode n = doc.DocumentElement;
                        if (n != null)
                        {
                            XmlNode a = n.Attributes.GetNamedItem("Quality");
                            if (a?.InnerText != null)
                            {
                                foreach (OMTQuality e in Enum.GetValues(typeof(OMTQuality)))
                                {
                                    if (e.ToString() == a.InnerText)
                                    {
                                        suggestedQuality = e;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    return true;
                }
                else if (xml.StartsWith(OMTMetadataTemplates.SENDER_INFO_PREFIX))
                {
                    senderInfo = OMTSenderInfo.FromXML(xml);
                }
                else if (xml.StartsWith(OMTMetadataTemplates.REDIRECT_PREFIX))
                {
                    this.redirectAddress = OMTRedirect.FromXML(xml);
                    OnEvent(OMTEventType.RedirectChanged);
                    return true;
                }

                lock (metadatas)
                {
                    if (metadatas.Count < OMTConstants.METADATA_MAX_COUNT)
                    {
                        metadatas.Enqueue(new OMTMetadata(frame.Timestamp, xml, endPoint));
                    }
                    metadataReadyEvent?.Set();
                }
                return true;
            }
            return false;
        }

        // ── Dispose ─────────────────────────────────────────────────

        protected override void DisposeInternal()
        {
            readCts?.Cancel();
            lock (sendSync) { }
            lock (readyFrames) { }
            lock (metadatas) { metadatas.Clear(); }

            if (stream != null)
            {
                try { stream.Dispose(); } catch { }
                stream = null;
            }

            if (framePool != null)
            {
                framePool.Dispose();
                framePool = null;
            }

            if (readyFrames != null)
            {
                lock (readyFrames)
                {
                    foreach (OMTFrame frame in readyFrames)
                    {
                        frame?.Dispose();
                    }
                    readyFrames.Clear();
                }
            }

            pendingFrame?.Dispose();
            pendingFrame = null;
            writeLock?.Dispose();
            readCts?.Dispose();
            base.DisposeInternal();
        }
    }
}
#endif
