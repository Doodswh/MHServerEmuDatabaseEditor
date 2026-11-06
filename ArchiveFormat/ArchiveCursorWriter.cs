using System;
using System.Collections.Generic;

namespace MHDBEditor.ArchiveFormat
{
    // Mirror writer for ArchiveCursor - see that class for format notes.
    public class ArchiveCursorWriter
    {
        private readonly List<byte> _buffer = new();

        public byte[] ToArray() => _buffer.ToArray();

        public void WriteRawByte(byte value) => _buffer.Add(value);

        public void WriteRawBytes(byte[] bytes) => _buffer.AddRange(bytes);

        public void WriteUnencodedUInt32(uint value) => _buffer.AddRange(BitConverter.GetBytes(value));

        public void WriteVarUInt32(uint value)
        {
            while (value >= 0x80)
            {
                _buffer.Add((byte)(value | 0x80));
                value >>= 7;
            }
            _buffer.Add((byte)value);
        }

        public void WriteVarUInt64(ulong value)
        {
            while (value >= 0x80)
            {
                _buffer.Add((byte)(value | 0x80));
                value >>= 7;
            }
            _buffer.Add((byte)value);
        }

        public void WriteZigZagInt32(int value)
        {
            uint encoded = (uint)((value << 1) ^ (value >> 31));
            WriteVarUInt32(encoded);
        }

        public void WriteZigZagInt64(long value)
        {
            ulong encoded = (ulong)((value << 1) ^ (value >> 63));
            WriteVarUInt64(encoded);
        }

        public void WriteFloatVarint(float value)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            WriteVarUInt32(bits);
        }

        public int Position => _buffer.Count;

        // Reserves a 4-byte placeholder for a size prefix (matching Archive.StartSizeChecking's
        // persistent-archive path) and returns its position, to be patched via
        // PatchUnencodedUInt32() once the object's total length is known.
        public int ReserveUnencodedUInt32()
        {
            int position = _buffer.Count;
            WriteUnencodedUInt32(0);
            return position;
        }

        public void PatchUnencodedUInt32(int position, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            for (int i = 0; i < 4; i++)
                _buffer[position + i] = bytes[i];
        }

        public byte ByteAt(int position) => _buffer[position];

        public void PatchByte(int position, byte value) => _buffer[position] = value;
    }
}
