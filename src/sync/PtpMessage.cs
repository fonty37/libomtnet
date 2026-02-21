#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;

namespace libomtnet.sync
{
    /// <summary>
    /// PTP v2 (IEEE 1588-2008) message types.
    /// </summary>
    internal enum PtpMessageType : byte
    {
        Sync = 0x0,
        DelayReq = 0x1,
        FollowUp = 0x8,
        DelayResp = 0x9,
        Announce = 0xB
    }

    /// <summary>
    /// PTP timestamp: 48-bit seconds + 32-bit nanoseconds.
    /// Converts to/from OMT's 100-nanosecond unit system.
    /// </summary>
    internal struct PtpTimestamp
    {
        public long Seconds;      // 48-bit unsigned, stored as long
        public uint Nanoseconds;  // 32-bit unsigned

        /// <summary>
        /// Convert to OMT 100-nanosecond units.
        /// </summary>
        public long ToOmtUnits()
        {
            return Seconds * 10_000_000L + Nanoseconds / 100L;
        }

        /// <summary>
        /// Create from OMT 100-nanosecond units.
        /// </summary>
        public static PtpTimestamp FromOmtUnits(long units)
        {
            return new PtpTimestamp
            {
                Seconds = units / 10_000_000L,
                Nanoseconds = (uint)((units % 10_000_000L) * 100L)
            };
        }

        /// <summary>
        /// Parse a 10-byte PTP timestamp from buffer at offset.
        /// Format: 6 bytes seconds (big-endian) + 4 bytes nanoseconds (big-endian).
        /// </summary>
        public static PtpTimestamp Parse(byte[] buf, int offset)
        {
            long sec = 0;
            for (int i = 0; i < 6; i++)
                sec = (sec << 8) | buf[offset + i];

            uint ns = (uint)(
                (buf[offset + 6] << 24) |
                (buf[offset + 7] << 16) |
                (buf[offset + 8] << 8) |
                buf[offset + 9]);

            return new PtpTimestamp { Seconds = sec, Nanoseconds = ns };
        }

        /// <summary>
        /// Write this timestamp to buffer at offset (10 bytes, big-endian).
        /// </summary>
        public void WriteTo(byte[] buf, int offset)
        {
            // 6 bytes seconds, big-endian
            buf[offset + 0] = (byte)(Seconds >> 40);
            buf[offset + 1] = (byte)(Seconds >> 32);
            buf[offset + 2] = (byte)(Seconds >> 24);
            buf[offset + 3] = (byte)(Seconds >> 16);
            buf[offset + 4] = (byte)(Seconds >> 8);
            buf[offset + 5] = (byte)(Seconds);
            // 4 bytes nanoseconds, big-endian
            buf[offset + 6] = (byte)(Nanoseconds >> 24);
            buf[offset + 7] = (byte)(Nanoseconds >> 16);
            buf[offset + 8] = (byte)(Nanoseconds >> 8);
            buf[offset + 9] = (byte)(Nanoseconds);
        }
    }

    /// <summary>
    /// PTP v2 common header (34 bytes) + parsed body fields.
    /// </summary>
    internal class PtpMessage
    {
        // Header fields (34 bytes)
        public PtpMessageType MessageType;
        public byte VersionPTP;           // Should be 2
        public ushort MessageLength;
        public byte DomainNumber;
        public byte Flags0;
        public byte Flags1;
        public long CorrectionField;      // 64-bit, in nanoseconds * 2^16
        public byte[] SourcePortIdentity;  // 10 bytes (8-byte clockId + 2-byte portNumber)
        public ushort SequenceId;
        public byte ControlField;
        public sbyte LogMessageInterval;

        // Body fields (message-type specific)
        public PtpTimestamp OriginTimestamp;     // For Sync, Follow_Up, Delay_Req
        public PtpTimestamp ReceiveTimestamp;    // For Delay_Resp
        public byte[] RequestingPortIdentity;   // For Delay_Resp (10 bytes)

        public const int HEADER_SIZE = 34;
        public const int SYNC_SIZE = 44;        // 34 header + 10 timestamp
        public const int FOLLOWUP_SIZE = 44;
        public const int DELAY_REQ_SIZE = 44;
        public const int DELAY_RESP_SIZE = 54;  // 34 header + 10 timestamp + 10 port identity

        /// <summary>
        /// Parse a PTP v2 message from a byte buffer.
        /// Returns null if the message is too short or invalid.
        /// </summary>
        public static PtpMessage Parse(byte[] buf, int length)
        {
            if (buf == null || length < HEADER_SIZE)
                return null;

            var msg = new PtpMessage();
            msg.MessageType = (PtpMessageType)(buf[0] & 0x0F);
            msg.VersionPTP = (byte)(buf[1] & 0x0F);
            msg.MessageLength = (ushort)((buf[2] << 8) | buf[3]);
            msg.DomainNumber = buf[4];
            // byte 5 reserved
            msg.Flags0 = buf[6];
            msg.Flags1 = buf[7];

            // Correction field: bytes 8-15 (64-bit, big-endian)
            msg.CorrectionField = 0;
            for (int i = 8; i < 16; i++)
                msg.CorrectionField = (msg.CorrectionField << 8) | buf[i];

            // bytes 16-19 reserved
            // Source port identity: bytes 20-29
            msg.SourcePortIdentity = new byte[10];
            Array.Copy(buf, 20, msg.SourcePortIdentity, 0, 10);

            msg.SequenceId = (ushort)((buf[30] << 8) | buf[31]);
            msg.ControlField = buf[32];
            msg.LogMessageInterval = (sbyte)buf[33];

            // Parse body based on message type
            switch (msg.MessageType)
            {
                case PtpMessageType.Sync:
                case PtpMessageType.FollowUp:
                case PtpMessageType.DelayReq:
                    if (length >= SYNC_SIZE)
                        msg.OriginTimestamp = PtpTimestamp.Parse(buf, 34);
                    break;

                case PtpMessageType.DelayResp:
                    if (length >= DELAY_RESP_SIZE)
                    {
                        msg.ReceiveTimestamp = PtpTimestamp.Parse(buf, 34);
                        msg.RequestingPortIdentity = new byte[10];
                        Array.Copy(buf, 44, msg.RequestingPortIdentity, 0, 10);
                    }
                    break;
            }

            return msg;
        }

        /// <summary>
        /// Create a Delay_Req message to send to the master.
        /// </summary>
        public static byte[] CreateDelayReq(byte[] sourcePortIdentity, ushort sequenceId, byte domain)
        {
            var buf = new byte[DELAY_REQ_SIZE];

            // Header
            buf[0] = (byte)PtpMessageType.DelayReq;  // messageType + transportSpecific
            buf[1] = 0x02;  // versionPTP = 2
            buf[2] = (byte)(DELAY_REQ_SIZE >> 8);
            buf[3] = (byte)(DELAY_REQ_SIZE);
            buf[4] = domain;
            // flags, correction, reserved = 0

            // Source port identity
            if (sourcePortIdentity != null && sourcePortIdentity.Length >= 10)
                Array.Copy(sourcePortIdentity, 0, buf, 20, 10);

            // Sequence ID
            buf[30] = (byte)(sequenceId >> 8);
            buf[31] = (byte)(sequenceId);

            buf[32] = 0x01;  // controlField = 1 for Delay_Req
            buf[33] = 0x7F;  // logMessageInterval = 0x7F (unknown)

            // Origin timestamp = 0 (filled by master on receive)
            return buf;
        }
    }
}
#endif
