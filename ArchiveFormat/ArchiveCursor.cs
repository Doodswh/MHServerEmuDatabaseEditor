using System;
using System.IO;

namespace MHDBEditor.ArchiveFormat
{
    // A forward-only byte cursor implementing the same low-level primitives as
    // MHServerEmu.Core.Serialization.Archive (ported from Archive.cs): standard protobuf-style
    // base-128 varints (32/64-bit) and zigzag decoding - matching Google.ProtocolBuffers'
    // CodedInputStream, which Archive.cs wraps, since that's the universal wire format all
    // protobuf implementations agree on - plus the "unencoded" raw 4-byte reads Archive uses
    // for size prefixes. Only implements what's needed to walk a Database-archive entity blob
    // as far as the end of its Properties block (bool bit-packing, Vector3, and string transfer
    // aren't needed for that and so aren't ported here).
    public class ArchiveCursor
    {
        private readonly byte[] _data;

        public int Position { get; private set; }
        public int Length => _data.Length;
        public bool HasMore => Position < _data.Length;

        public ArchiveCursor(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public byte ReadRawByte()
        {
            if (Position >= _data.Length)
                throw new EndOfStreamException($"ReadRawByte(): ran out of data at offset {Position}");
            return _data[Position++];
        }

        public byte[] ReadRawBytes(int count)
        {
            if (count < 0 || count > _data.Length - Position)
                throw new EndOfStreamException($"ReadRawBytes({count}): ran out of data at offset {Position}");
            byte[] result = new byte[count];
            Array.Copy(_data, Position, result, 0, count);
            Position += count;
            return result;
        }

        public byte[] ReadRemainingBytes() => ReadRawBytes(_data.Length - Position);

        // Matches Archive.ReadUnencodedStream(ref uint): 4 raw bytes, platform-endian (little
        // endian on every realistic .NET deployment target), no varint encoding.
        public uint ReadUnencodedUInt32()
        {
            byte[] bytes = ReadRawBytes(4);
            return BitConverter.ToUInt32(bytes, 0);
        }

        public uint ReadVarUInt32()
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = ReadRawByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 35) throw new InvalidDataException("ReadVarUInt32(): varint too long");
            }
            return result;
        }

        public ulong ReadVarUInt64()
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte b = ReadRawByte();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 70) throw new InvalidDataException("ReadVarUInt64(): varint too long");
            }
            return result;
        }

        // Matches CodedInputStream.DecodeZigZag32/64 (standard protobuf zigzag decode).
        public int ReadZigZagInt32()
        {
            uint n = ReadVarUInt32();
            return (int)(n >> 1) ^ -(int)(n & 1);
        }

        public long ReadZigZagInt64()
        {
            ulong n = ReadVarUInt64();
            return (long)(n >> 1) ^ -(long)(n & 1);
        }

        // Matches Archive.Transfer(ref float): the raw 32-bit bit pattern written as a PLAIN
        // varint (not zigzag) - see Transfer(ref float) in Archive.cs.
        public float ReadFloatVarint()
        {
            uint bits = ReadVarUInt32();
            return BitConverter.UInt32BitsToSingle(bits);
        }

        // Reads the 4-byte size prefix Archive.cs's generic ISerialize Transfer<T> writes for
        // persistent archives (StartSizeChecking/EndSizeChecking). The recorded size covers the
        // prefix itself plus the object's content, matching how it's written - so the object's
        // content ends at (position of the size field) + (declared size). Returns that expected
        // end position; call ExpectPosition() after decoding the object's fields to verify.
        public int ReadSizePrefixHeader()
        {
            int sizeFieldStart = Position;
            uint declaredSize = ReadUnencodedUInt32();
            if (declaredSize < 4 || declaredSize > (uint)(_data.Length - sizeFieldStart))
                throw new InvalidDataException($"Size prefix {declaredSize:N0} at offset {sizeFieldStart} doesn't fit the {_data.Length - sizeFieldStart:N0} bytes left.");
            return sizeFieldStart + (int)declaredSize;
        }

        public void ExpectPosition(int expected, string contextName)
        {
            if (Position != expected)
            {
                throw new InvalidDataException(
                    $"{contextName}: expected to end at offset {expected} but ended at {Position} - " +
                    "the format assumption doesn't match this data.");
            }
        }
    }
}
