using System;
using System.Collections.Generic;
using System.IO;

namespace MHDBEditor.ArchiveFormat
{
    // Decodes/encodes a persisted PropertyCollection, matching
    // MHServerEmu.Games.Properties.PropertyCollection.SerializeWithDefault() (the
    // archive.IsPersistent branch) combined with PropertyStore.Serialize() exactly.
    public static class PropertyBlockCodec
    {
        public static List<PropertyRecord> Decode(ArchiveCursor cursor)
        {
            uint numProperties = cursor.ReadUnencodedUInt32();
            // Each property is at least 4 bytes, so a bigger count is a corrupt blob - fail cleanly
            // instead of trying to allocate a huge list.
            if (numProperties > (uint)((cursor.Length - cursor.Position) / 4))
                throw new InvalidDataException($"Property count {numProperties:N0} is more than the {cursor.Length - cursor.Position:N0} bytes left could hold.");
            List<PropertyRecord> result = new((int)numProperties);

            for (uint i = 0; i < numProperties; i++)
                result.Add(DecodeOne(cursor));

            return result;
        }

        private static PropertyRecord DecodeOne(ArchiveCursor cursor)
        {
            PropertyRecord record = new()
            {
                Version = cursor.ReadRawByte(),
                PrototypeGuid = cursor.ReadVarUInt64(),
            };

            byte paramCount = cursor.ReadRawByte();
            for (int i = 0; i < paramCount; i++)
            {
                sbyte rawParamType = unchecked((sbyte)cursor.ReadRawByte());
                PropertyParamType paramType = (PropertyParamType)rawParamType;

                PropertyParamRecord param = new() { Type = paramType };
                switch (paramType)
                {
                    case PropertyParamType.Integer:
                        param.IntValue = cursor.ReadZigZagInt32();
                        break;
                    case PropertyParamType.Asset:
                    case PropertyParamType.Prototype:
                        param.GuidValue = cursor.ReadVarUInt64();
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported property param type {rawParamType} at offset {cursor.Position}");
                }
                record.Params.Add(param);
            }

            int rawDataType = cursor.ReadZigZagInt32();
            record.DataType = (PropertyDataType)rawDataType;

            switch (record.DataType)
            {
                case PropertyDataType.Boolean:
                case PropertyDataType.Integer:
                case PropertyDataType.EntityId:
                case PropertyDataType.Time:
                case PropertyDataType.Guid:
                case PropertyDataType.RegionId:
                case PropertyDataType.Int21Vector3:
                    record.ValueLong = cursor.ReadZigZagInt64();
                    break;

                case PropertyDataType.Real:
                    record.ValueFloat = cursor.ReadFloatVarint();
                    break;

                case PropertyDataType.Prototype:
                case PropertyDataType.Asset:
                    record.ValueGuid = cursor.ReadVarUInt64();
                    break;

                default:
                    throw new InvalidDataException($"Unsupported property data type {rawDataType} at offset {cursor.Position}");
            }

            return record;
        }

        public static void Encode(ArchiveCursorWriter writer, List<PropertyRecord> records)
        {
            writer.WriteUnencodedUInt32((uint)records.Count);
            foreach (PropertyRecord record in records)
                EncodeOne(writer, record);
        }

        private static void EncodeOne(ArchiveCursorWriter writer, PropertyRecord record)
        {
            writer.WriteRawByte(record.Version);
            writer.WriteVarUInt64(record.PrototypeGuid);
            writer.WriteRawByte((byte)record.Params.Count);

            foreach (PropertyParamRecord param in record.Params)
            {
                writer.WriteRawByte(unchecked((byte)(sbyte)param.Type));
                switch (param.Type)
                {
                    case PropertyParamType.Integer:
                        writer.WriteZigZagInt32(param.IntValue);
                        break;
                    case PropertyParamType.Asset:
                    case PropertyParamType.Prototype:
                        writer.WriteVarUInt64(param.GuidValue);
                        break;
                    default:
                        throw new NotSupportedException($"Can't save property param type {param.Type}.");
                }
            }

            writer.WriteZigZagInt32((int)record.DataType);

            switch (record.DataType)
            {
                case PropertyDataType.Boolean:
                case PropertyDataType.Integer:
                case PropertyDataType.EntityId:
                case PropertyDataType.Time:
                case PropertyDataType.Guid:
                case PropertyDataType.RegionId:
                case PropertyDataType.Int21Vector3:
                    writer.WriteZigZagInt64(record.ValueLong);
                    break;
                case PropertyDataType.Real:
                    writer.WriteFloatVarint(record.ValueFloat);
                    break;
                case PropertyDataType.Prototype:
                case PropertyDataType.Asset:
                    writer.WriteVarUInt64(record.ValueGuid);
                    break;
                default:
                    // Never write a type with no value bytes - the result wouldn't load.
                    throw new NotSupportedException($"Can't save property data type {record.DataType}.");
            }
        }
    }
}
