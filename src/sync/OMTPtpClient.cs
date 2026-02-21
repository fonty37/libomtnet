#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace libomtnet.sync
{
    /// <summary>
    /// PTP v2 (IEEE 1588-2008) follower/slave implementation.
    /// Listens for Sync/Follow_Up messages on PTP multicast,
    /// sends Delay_Req, and calculates master offset.
    ///
    /// Software PTP achieves 10-100μs on standard gigabit networks.
    /// </summary>
    internal class OMTPtpClient : IDisposable
    {
        // PTP multicast addresses and ports
        private const string PTP_PRIMARY_MULTICAST = "224.0.1.129";
        private const int PTP_EVENT_PORT = 319;
        private const int PTP_GENERAL_PORT = 320;

        // Linux SO_TIMESTAMPING constants
        private const int SOL_SOCKET = 1;
        private const int SO_TIMESTAMPING = 37;
        private const int SOF_TIMESTAMPING_RX_SOFTWARE = (1 << 3);
        private const int SOF_TIMESTAMPING_SOFTWARE = (1 << 4);

        private readonly string interfaceName;
        private readonly byte domain;
        private readonly PtpServo servo;

        private Socket eventSocket;
        private Socket generalSocket;
        private Thread listenerThread;
        private volatile bool running;

        // Our port identity (8-byte MAC-based clock ID + 2-byte port)
        private byte[] localPortIdentity;
        private ushort delayReqSequence;

        // Timing state
        private readonly Stopwatch localClock;
        private long t1;  // Master send time (from Follow_Up)
        private long t2;  // Slave receive time of Sync
        private long t3;  // Slave send time of Delay_Req
        private long t4;  // Master receive time (from Delay_Resp)
        private ushort lastSyncSequence;
        private bool hasSyncTimestamp;
        private bool hasFollowUp;

        // Accumulated correction from servo (drift only, not epoch offset)
        private long clockCorrection;
        private readonly object correctionLock = new object();

        // Epoch baseline: on first sync, record the offset between PTP epoch and local clock.
        // Subsequent measurements only track drift from this baseline.
        private long epochBaseline;
        private bool hasEpochBaseline;

        // Master info
        private byte[] masterPortIdentity;
        private DateTime lastSyncReceived;

        /// <summary>
        /// Current PTP synchronization state.
        /// </summary>
        public OMTPtpState State => servo.State;

        /// <summary>
        /// Current offset from master in microseconds.
        /// </summary>
        public double OffsetMicroseconds => servo.OffsetNanoseconds / 1000.0;

        /// <summary>
        /// Current path delay in microseconds.
        /// </summary>
        public double PathDelayMicroseconds => servo.PathDelayNanoseconds / 1000.0;

        /// <summary>
        /// Whether we've received at least one sync from a master.
        /// </summary>
        public bool HasMaster => masterPortIdentity != null;

        /// <summary>
        /// Number of sync samples processed.
        /// </summary>
        public int SampleCount => servo.SampleCount;

        /// <summary>
        /// Current clock correction in 100-nanosecond units.
        /// Add this to local Stopwatch time to get PTP-disciplined time.
        /// </summary>
        public long ClockCorrection
        {
            get { lock (correctionLock) return clockCorrection; }
        }

        public OMTPtpClient(string interfaceName, byte domain = 0, double kp = 0.7, double ki = 0.3)
        {
            this.interfaceName = interfaceName;
            this.domain = domain;
            this.servo = new PtpServo(kp, ki);
            this.localClock = Stopwatch.StartNew();
            this.localPortIdentity = GeneratePortIdentity(interfaceName);
            this.delayReqSequence = 0;
            this.lastSyncReceived = DateTime.MinValue;

            Start();
        }

        private void Start()
        {
            running = true;

            // Create event socket (port 319) for Sync and Delay messages
            eventSocket = CreateMulticastSocket(PTP_EVENT_PORT);

            // Create general socket (port 320) for Follow_Up and Announce
            generalSocket = CreateMulticastSocket(PTP_GENERAL_PORT);

            // Try to enable software timestamping on Linux
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                TryEnableSoftwareTimestamping(eventSocket);
            }

            listenerThread = new Thread(ListenerLoop)
            {
                Name = "OMTPtpClient",
                IsBackground = true
            };
            listenerThread.Start();
        }

        private Socket CreateMulticastSocket(int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            // Join PTP multicast group
            IPAddress mcastAddr = IPAddress.Parse(PTP_PRIMARY_MULTICAST);

            // Try to bind to specific interface
            IPAddress localAddr = GetInterfaceAddress(interfaceName);
            if (localAddr != null)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    localAddr.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(mcastAddr, localAddr));
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(mcastAddr));
            }

            socket.ReceiveTimeout = 1000;
            return socket;
        }

        private static void TryEnableSoftwareTimestamping(Socket socket)
        {
            try
            {
                int flags = SOF_TIMESTAMPING_RX_SOFTWARE | SOF_TIMESTAMPING_SOFTWARE;
                socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)SO_TIMESTAMPING, flags);
            }
            catch
            {
                // Not supported — fall back to userspace timestamps
            }
        }

        private void ListenerLoop()
        {
            var eventBuf = new byte[256];
            var generalBuf = new byte[256];
            var eventEp = new IPEndPoint(IPAddress.Any, 0);
            var generalEp = new IPEndPoint(IPAddress.Any, 0);

            while (running)
            {
                try
                {
                    // Poll both sockets
                    bool eventReady = eventSocket.Poll(100000, SelectMode.SelectRead); // 100ms
                    if (eventReady)
                    {
                        EndPoint ep = eventEp;
                        int len = eventSocket.ReceiveFrom(eventBuf, ref ep);
                        long receiveTime = localClock.ElapsedMilliseconds * 10000; // 100ns units
                        ProcessEventMessage(eventBuf, len, receiveTime);
                    }

                    bool generalReady = generalSocket.Poll(100000, SelectMode.SelectRead);
                    if (generalReady)
                    {
                        EndPoint ep = generalEp;
                        int len = generalSocket.ReceiveFrom(generalBuf, ref ep);
                        ProcessGeneralMessage(generalBuf, len);
                    }
                }
                catch (SocketException)
                {
                    // Timeout or transient error — continue
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private void ProcessEventMessage(byte[] buf, int length, long receiveTime)
        {
            var msg = PtpMessage.Parse(buf, length);
            if (msg == null || msg.DomainNumber != domain) return;

            switch (msg.MessageType)
            {
                case PtpMessageType.Sync:
                    // Record t2 (slave receive time of Sync)
                    t2 = receiveTime;
                    lastSyncSequence = msg.SequenceId;
                    hasSyncTimestamp = true;
                    hasFollowUp = false;
                    lastSyncReceived = DateTime.UtcNow;

                    // Remember master identity
                    if (masterPortIdentity == null)
                    {
                        masterPortIdentity = new byte[10];
                        Array.Copy(msg.SourcePortIdentity, masterPortIdentity, 10);
                    }

                    // Check two-step flag (bit 1 of flags0)
                    bool twoStep = (msg.Flags0 & 0x02) != 0;
                    if (!twoStep)
                    {
                        // One-step: Sync carries the precise timestamp directly
                        t1 = msg.OriginTimestamp.ToOmtUnits();
                        hasFollowUp = true;
                        TryCalculateOffset();
                    }
                    break;
            }
        }

        private void ProcessGeneralMessage(byte[] buf, int length)
        {
            var msg = PtpMessage.Parse(buf, length);
            if (msg == null || msg.DomainNumber != domain) return;

            switch (msg.MessageType)
            {
                case PtpMessageType.FollowUp:
                    // Follow_Up carries the precise t1 timestamp for the matching Sync
                    if (hasSyncTimestamp && msg.SequenceId == lastSyncSequence)
                    {
                        t1 = msg.OriginTimestamp.ToOmtUnits();

                        // Apply correction field (nanoseconds * 2^16 → 100ns units)
                        if (msg.CorrectionField != 0)
                        {
                            long corrNs = msg.CorrectionField >> 16;
                            t1 += corrNs / 100;
                        }

                        hasFollowUp = true;
                        TryCalculateOffset();
                    }
                    break;

                case PtpMessageType.DelayResp:
                    // Delay_Resp carries t4 (master receive time of our Delay_Req)
                    if (msg.RequestingPortIdentity != null && MatchesPortIdentity(msg.RequestingPortIdentity))
                    {
                        t4 = msg.ReceiveTimestamp.ToOmtUnits();
                        CalculateDelayAndUpdate();
                    }
                    break;
            }
        }

        private void TryCalculateOffset()
        {
            if (!hasSyncTimestamp || !hasFollowUp) return;

            // Send a Delay_Req to measure path delay
            SendDelayReq();
        }

        private void SendDelayReq()
        {
            try
            {
                var reqBuf = PtpMessage.CreateDelayReq(localPortIdentity, delayReqSequence++, domain);
                t3 = localClock.ElapsedMilliseconds * 10000; // Record send time

                var masterEp = new IPEndPoint(IPAddress.Parse(PTP_PRIMARY_MULTICAST), PTP_EVENT_PORT);
                eventSocket.SendTo(reqBuf, masterEp);
            }
            catch
            {
                // Send failed — skip this measurement
            }
        }

        private void CalculateDelayAndUpdate()
        {
            // IEEE 1588 offset and delay calculation:
            // offset = ((t2 - t1) - (t4 - t3)) / 2
            // pathDelay = ((t2 - t1) + (t4 - t3)) / 2
            //
            // t1, t4 are in PTP epoch time (seconds since 1970, in 100ns units)
            // t2, t3 are in local Stopwatch time (since app start, in 100ns units)
            // The raw offset includes the epoch difference (potentially years).
            //
            // We establish a baseline on the first measurement, then only track drift.

            long rawOffset = ((t2 - t1) - (t4 - t3)) / 2;
            long pathDelay = ((t2 - t1) + (t4 - t3)) / 2;

            // Sanity check: path delay should be positive
            if (pathDelay < 0) pathDelay = 0;

            if (!hasEpochBaseline)
            {
                // First measurement: record the epoch-to-local offset as baseline
                epochBaseline = rawOffset;
                hasEpochBaseline = true;
                servo.ProcessSample(0, pathDelay); // Tell servo we're starting at 0
                return;
            }

            // Drift = how much the offset has changed since baseline
            // Positive drift = local clock is drifting ahead of master
            long drift = rawOffset - epochBaseline;

            // Feed drift to PI servo
            long correction = servo.ProcessSample(drift, pathDelay);

            // Update accumulated clock correction
            lock (correctionLock)
            {
                clockCorrection += correction;
            }

            // Reset for next measurement
            hasSyncTimestamp = false;
            hasFollowUp = false;
        }

        private bool MatchesPortIdentity(byte[] identity)
        {
            if (identity == null || localPortIdentity == null) return false;
            if (identity.Length != localPortIdentity.Length) return false;
            for (int i = 0; i < identity.Length; i++)
            {
                if (identity[i] != localPortIdentity[i]) return false;
            }
            return true;
        }

        private static byte[] GeneratePortIdentity(string interfaceName)
        {
            var identity = new byte[10];

            // Try to use the MAC address of the specified interface
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.Name == interfaceName || nic.Description == interfaceName)
                    {
                        byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
                        if (mac.Length == 6)
                        {
                            // EUI-64: insert FF:FE in the middle of the MAC
                            identity[0] = mac[0];
                            identity[1] = mac[1];
                            identity[2] = mac[2];
                            identity[3] = 0xFF;
                            identity[4] = 0xFE;
                            identity[5] = mac[3];
                            identity[6] = mac[4];
                            identity[7] = mac[5];
                            identity[8] = 0;  // Port number
                            identity[9] = 1;
                            return identity;
                        }
                    }
                }
            }
            catch { }

            // Fallback: random identity
            new Random().NextBytes(identity);
            identity[8] = 0;
            identity[9] = 1;
            return identity;
        }

        private static IPAddress GetInterfaceAddress(string interfaceName)
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.Name == interfaceName || nic.Description == interfaceName)
                    {
                        foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                                return addr.Address;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        public void Dispose()
        {
            running = false;

            try { eventSocket?.Close(); } catch { }
            try { generalSocket?.Close(); } catch { }

            if (listenerThread != null && listenerThread.IsAlive)
                listenerThread.Join(2000);

            try { eventSocket?.Dispose(); } catch { }
            try { generalSocket?.Dispose(); } catch { }
        }
    }
}
#endif
