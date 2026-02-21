/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;
using System.Collections.Generic;

namespace libomtnet
{
    /// <summary>
    /// Typed metadata type identifiers, inspired by ST 2110-41 Data Item Types.
    /// </summary>
    public enum OMTMetadataTypeID : ushort
    {
        Reserved = 0x0000,
        /// <summary>SMPTE 12M Timecode (5 bytes: HH MM SS FF Flags)</summary>
        Timecode = 0x0001,
        /// <summary>CEA-608 closed captions</summary>
        CaptionCEA608 = 0x0002,
        /// <summary>CEA-708 closed captions</summary>
        CaptionCEA708 = 0x0003,
        /// <summary>SCTE-104 ad insertion markers</summary>
        SCTE104 = 0x0004,
        /// <summary>Active Format Description + Bar Data (6 bytes)</summary>
        AFDBarData = 0x0005,
        /// <summary>Tally state (2 bytes: preview, program)</summary>
        Tally = 0x0006,
        // 0x0007-0x00FF: Reserved for future OMT standard types
        // 0x0100-0x7FFF: User-defined
        // 0x8000-0xFFFE: Vendor/experimental
        /// <summary>Custom XML wrapped in typed metadata container</summary>
        CustomXML = 0xFFFF
    }

    /// <summary>
    /// Frame rate index for SMPTE 12M timecode.
    /// </summary>
    public enum OMTTimecodeFrameRate : byte
    {
        FPS24 = 0,
        FPS25 = 1,
        FPS30 = 2,
        Reserved = 3
    }

    /// <summary>
    /// SMPTE 12M timecode value.
    /// </summary>
    public struct OMTTimecode
    {
        public byte Hours;
        public byte Minutes;
        public byte Seconds;
        public byte Frames;
        public bool DropFrame;
        public bool ColorFrame;
        public bool FieldMark;
        public OMTTimecodeFrameRate FrameRate;

        public byte GetFlagsByte()
        {
            byte flags = 0;
            if (DropFrame) flags |= 1;
            if (ColorFrame) flags |= 2;
            if (FieldMark) flags |= 4;
            flags |= (byte)((byte)FrameRate << 3);
            return flags;
        }

        public static OMTTimecode FromFlagsByte(byte hours, byte minutes, byte seconds, byte frames, byte flags)
        {
            return new OMTTimecode
            {
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds,
                Frames = frames,
                DropFrame = (flags & 1) != 0,
                ColorFrame = (flags & 2) != 0,
                FieldMark = (flags & 4) != 0,
                FrameRate = (OMTTimecodeFrameRate)((flags >> 3) & 0x03)
            };
        }

        public override string ToString()
        {
            return string.Format("{0:D2}:{1:D2}:{2:D2}{3}{4:D2}",
                Hours, Minutes, Seconds,
                DropFrame ? ";" : ":",
                Frames);
        }
    }

    /// <summary>
    /// Active Format Description + Bar Data.
    /// </summary>
    public struct OMTAFDBarData
    {
        public byte AFD;
        public byte AspectRatio;
        public ushort BarTop;
        public ushort BarBottom;
    }

    /// <summary>
    /// SCTE-104 splice marker.
    /// </summary>
    public struct OMTScteMarker
    {
        public byte Operation;
        public uint SpliceEventId;
        public uint PtsOffset;
        public byte AutoReturn;

        public const byte OP_SPLICE_INSERT = 0;
        public const byte OP_SPLICE_NULL = 1;
        public const byte OP_TIME_SIGNAL = 2;
    }

    /// <summary>
    /// A single typed metadata item parsed from a typed metadata buffer.
    /// </summary>
    public struct OMTTypedMetadataItem
    {
        public OMTMetadataTypeID TypeID;
        public byte[] Payload;
        public int PayloadLength;
    }

    /// <summary>
    /// Serialization codec for OMT typed binary metadata.
    /// Wire format: [0xFD magic] [item1] [item2] ...
    /// Each item: [uint16 TypeID LE] [uint16 PayloadLength LE] [payload bytes]
    /// </summary>
    internal static class OMTTypedMetadataCodec
    {
        public const byte MAGIC = 0xFD;
        private const int ITEM_HEADER_SIZE = 4;

        /// <summary>
        /// Returns true if the buffer starts with the typed metadata magic byte.
        /// </summary>
        public static bool IsTypedMetadata(byte[] buf, int offset, int length)
        {
            return length > 0 && buf[offset] == MAGIC;
        }

        /// <summary>
        /// Write a single timecode item. Returns bytes written.
        /// Total: 1 (magic) + 4 (item header) + 5 (payload) = 10 bytes.
        /// </summary>
        public static int WriteTimecode(byte[] buf, int offset, OMTTimecode tc)
        {
            int pos = offset;
            buf[pos++] = MAGIC;
            WriteUInt16(buf, pos, (ushort)OMTMetadataTypeID.Timecode); pos += 2;
            WriteUInt16(buf, pos, 5); pos += 2;
            buf[pos++] = tc.Hours;
            buf[pos++] = tc.Minutes;
            buf[pos++] = tc.Seconds;
            buf[pos++] = tc.Frames;
            buf[pos++] = tc.GetFlagsByte();
            return pos - offset;
        }

        /// <summary>
        /// Write a single tally item. Returns bytes written.
        /// Total: 1 (magic) + 4 (item header) + 2 (payload) = 7 bytes.
        /// </summary>
        public static int WriteTally(byte[] buf, int offset, byte preview, byte program)
        {
            int pos = offset;
            buf[pos++] = MAGIC;
            WriteUInt16(buf, pos, (ushort)OMTMetadataTypeID.Tally); pos += 2;
            WriteUInt16(buf, pos, 2); pos += 2;
            buf[pos++] = preview;
            buf[pos++] = program;
            return pos - offset;
        }

        /// <summary>
        /// Write a single AFD + Bar Data item. Returns bytes written.
        /// Total: 1 (magic) + 4 (item header) + 6 (payload) = 11 bytes.
        /// </summary>
        public static int WriteAFD(byte[] buf, int offset, OMTAFDBarData afd)
        {
            int pos = offset;
            buf[pos++] = MAGIC;
            WriteUInt16(buf, pos, (ushort)OMTMetadataTypeID.AFDBarData); pos += 2;
            WriteUInt16(buf, pos, 6); pos += 2;
            buf[pos++] = afd.AFD;
            buf[pos++] = afd.AspectRatio;
            WriteUInt16(buf, pos, afd.BarTop); pos += 2;
            WriteUInt16(buf, pos, afd.BarBottom); pos += 2;
            return pos - offset;
        }

        /// <summary>
        /// Write a single SCTE-104 marker. Returns bytes written.
        /// Payload: 10 bytes (operation + eventId + ptsOffset + autoReturn).
        /// Total: 1 (magic) + 4 (item header) + 10 (payload) = 15 bytes.
        /// </summary>
        public static int WriteScte(byte[] buf, int offset, OMTScteMarker marker)
        {
            int pos = offset;
            buf[pos++] = MAGIC;
            WriteUInt16(buf, pos, (ushort)OMTMetadataTypeID.SCTE104); pos += 2;
            WriteUInt16(buf, pos, 10); pos += 2;
            buf[pos++] = marker.Operation;
            WriteUInt32(buf, pos, marker.SpliceEventId); pos += 4;
            WriteUInt32(buf, pos, marker.PtsOffset); pos += 4;
            buf[pos++] = marker.AutoReturn;
            return pos - offset;
        }

        /// <summary>
        /// Write a generic typed metadata item. Returns bytes written.
        /// Use this for CEA-608, CEA-708, CustomXML, or user-defined types.
        /// </summary>
        public static int WriteItem(byte[] buf, int offset, OMTMetadataTypeID type, byte[] payload, int pOffset, int pLength)
        {
            int pos = offset;
            buf[pos++] = MAGIC;
            WriteUInt16(buf, pos, (ushort)type); pos += 2;
            WriteUInt16(buf, pos, (ushort)pLength); pos += 2;
            Buffer.BlockCopy(payload, pOffset, buf, pos, pLength);
            pos += pLength;
            return pos - offset;
        }

        /// <summary>
        /// Append an additional item to a buffer that already has the magic byte.
        /// Does NOT write the magic byte. Returns bytes written.
        /// </summary>
        public static int AppendItem(byte[] buf, int offset, OMTMetadataTypeID type, byte[] payload, int pOffset, int pLength)
        {
            int pos = offset;
            WriteUInt16(buf, pos, (ushort)type); pos += 2;
            WriteUInt16(buf, pos, (ushort)pLength); pos += 2;
            Buffer.BlockCopy(payload, pOffset, buf, pos, pLength);
            pos += pLength;
            return pos - offset;
        }

        /// <summary>
        /// Append a timecode item to an existing typed metadata buffer (no magic byte written).
        /// </summary>
        public static int AppendTimecode(byte[] buf, int offset, OMTTimecode tc)
        {
            int pos = offset;
            WriteUInt16(buf, pos, (ushort)OMTMetadataTypeID.Timecode); pos += 2;
            WriteUInt16(buf, pos, 5); pos += 2;
            buf[pos++] = tc.Hours;
            buf[pos++] = tc.Minutes;
            buf[pos++] = tc.Seconds;
            buf[pos++] = tc.Frames;
            buf[pos++] = tc.GetFlagsByte();
            return pos - offset;
        }

        /// <summary>
        /// Append a tally item to an existing typed metadata buffer (no magic byte written).
        /// </summary>
        public static int AppendTally(byte[] buf, int offset, byte preview, byte program)
        {
            int pos = offset;
            WriteUInt16(buf, pos, (ushort)OMTMetadataTypeID.Tally); pos += 2;
            WriteUInt16(buf, pos, 2); pos += 2;
            buf[pos++] = preview;
            buf[pos++] = program;
            return pos - offset;
        }

        /// <summary>
        /// Read all typed metadata items from a buffer.
        /// Buffer must start with MAGIC byte.
        /// </summary>
        public static OMTTypedMetadataItem[] Read(byte[] buf, int offset, int length)
        {
            if (length < 1 || buf[offset] != MAGIC) return null;

            var items = new List<OMTTypedMetadataItem>();
            int pos = offset + 1; // Skip magic byte
            int end = offset + length;

            while (pos + ITEM_HEADER_SIZE <= end)
            {
                ushort typeId = ReadUInt16(buf, pos); pos += 2;
                ushort payloadLen = ReadUInt16(buf, pos); pos += 2;

                if (pos + payloadLen > end) break;

                byte[] payload = new byte[payloadLen];
                Buffer.BlockCopy(buf, pos, payload, 0, payloadLen);
                pos += payloadLen;

                items.Add(new OMTTypedMetadataItem
                {
                    TypeID = (OMTMetadataTypeID)typeId,
                    Payload = payload,
                    PayloadLength = payloadLen
                });
            }

            return items.ToArray();
        }

        /// <summary>
        /// Try to read the first timecode item from a typed metadata buffer.
        /// </summary>
        public static bool TryReadTimecode(byte[] buf, int offset, int length, out OMTTimecode tc)
        {
            tc = new OMTTimecode();
            if (length < 1 || buf[offset] != MAGIC) return false;

            int pos = offset + 1;
            int end = offset + length;

            while (pos + ITEM_HEADER_SIZE <= end)
            {
                ushort typeId = ReadUInt16(buf, pos); pos += 2;
                ushort payloadLen = ReadUInt16(buf, pos); pos += 2;

                if (pos + payloadLen > end) return false;

                if (typeId == (ushort)OMTMetadataTypeID.Timecode && payloadLen >= 5)
                {
                    tc = OMTTimecode.FromFlagsByte(buf[pos], buf[pos + 1], buf[pos + 2], buf[pos + 3], buf[pos + 4]);
                    return true;
                }
                pos += payloadLen;
            }
            return false;
        }

        /// <summary>
        /// Try to read the first tally item from a typed metadata buffer.
        /// </summary>
        public static bool TryReadTally(byte[] buf, int offset, int length, out byte preview, out byte program)
        {
            preview = 0;
            program = 0;
            if (length < 1 || buf[offset] != MAGIC) return false;

            int pos = offset + 1;
            int end = offset + length;

            while (pos + ITEM_HEADER_SIZE <= end)
            {
                ushort typeId = ReadUInt16(buf, pos); pos += 2;
                ushort payloadLen = ReadUInt16(buf, pos); pos += 2;

                if (pos + payloadLen > end) return false;

                if (typeId == (ushort)OMTMetadataTypeID.Tally && payloadLen >= 2)
                {
                    preview = buf[pos];
                    program = buf[pos + 1];
                    return true;
                }
                pos += payloadLen;
            }
            return false;
        }

        /// <summary>
        /// Try to read the first AFD item from a typed metadata buffer.
        /// </summary>
        public static bool TryReadAFD(byte[] buf, int offset, int length, out OMTAFDBarData afd)
        {
            afd = new OMTAFDBarData();
            if (length < 1 || buf[offset] != MAGIC) return false;

            int pos = offset + 1;
            int end = offset + length;

            while (pos + ITEM_HEADER_SIZE <= end)
            {
                ushort typeId = ReadUInt16(buf, pos); pos += 2;
                ushort payloadLen = ReadUInt16(buf, pos); pos += 2;

                if (pos + payloadLen > end) return false;

                if (typeId == (ushort)OMTMetadataTypeID.AFDBarData && payloadLen >= 6)
                {
                    afd.AFD = buf[pos];
                    afd.AspectRatio = buf[pos + 1];
                    afd.BarTop = ReadUInt16(buf, pos + 2);
                    afd.BarBottom = ReadUInt16(buf, pos + 4);
                    return true;
                }
                pos += payloadLen;
            }
            return false;
        }

        /// <summary>
        /// Try to read the first SCTE-104 marker from a typed metadata buffer.
        /// </summary>
        public static bool TryReadScte(byte[] buf, int offset, int length, out OMTScteMarker marker)
        {
            marker = new OMTScteMarker();
            if (length < 1 || buf[offset] != MAGIC) return false;

            int pos = offset + 1;
            int end = offset + length;

            while (pos + ITEM_HEADER_SIZE <= end)
            {
                ushort typeId = ReadUInt16(buf, pos); pos += 2;
                ushort payloadLen = ReadUInt16(buf, pos); pos += 2;

                if (pos + payloadLen > end) return false;

                if (typeId == (ushort)OMTMetadataTypeID.SCTE104 && payloadLen >= 10)
                {
                    marker.Operation = buf[pos];
                    marker.SpliceEventId = ReadUInt32(buf, pos + 1);
                    marker.PtsOffset = ReadUInt32(buf, pos + 5);
                    marker.AutoReturn = buf[pos + 9];
                    return true;
                }
                pos += payloadLen;
            }
            return false;
        }

        private static void WriteUInt16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteUInt32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static ushort ReadUInt16(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }
    }
}
