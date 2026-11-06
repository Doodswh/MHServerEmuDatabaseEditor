using System.Collections.Generic;
using System.IO;

namespace MHDBEditor.Utility
{
    // Raw, untyped view of one Calligraphy prototype file (or an embedded RHStruct inside one),
    // following CalligraphySerializer.DoDeserialize / DeserializeFieldGroup / DeserializePrototypePtr:
    //
    //   header byte (1 = has parent ref, 2 = has instance data, 4 = polymorphic), [u64 parent data ref]
    //   if instance data: i16 field groups, each = u64 blueprint id, u8 copy num,
    //       simple fields: i16 count, each = u64 field id, u8 base type, value
    //       list fields:   i16 count, each = u64 field id, u8 base type, i16 n, n values
    //   value = embedded prototype (this same layout) for 'R' (RHStruct), otherwise 8 bytes.
    //
    // Property mixin groups use the same shape (8-byte values, empty list section), so one
    // reader handles everything. Fields are keyed by field id; CalligraphyReader.GetFieldName
    // turns ids into names. Only this prototype's own fields are here - a field missing from
    // it is inherited from ParentDataRef.
    public class PrototypeData
    {
        private const byte RHStruct = (byte)'R';

        public ulong ParentDataRef { get; private set; }
        public bool HasData { get; private set; }

        public Dictionary<ulong, ulong> Simple { get; } = new();
        public Dictionary<ulong, PrototypeData> SimpleStructs { get; } = new();
        public Dictionary<ulong, List<ulong>> Lists { get; } = new();
        public Dictionary<ulong, List<PrototypeData>> StructLists { get; } = new();

        // Field group blueprint ids in file order. Property mixin groups (e.g. a built-in
        // "Health +100" entry) are identified by their blueprint, which names the property.
        public List<(ulong BlueprintId, List<ulong> FieldIds)> Groups { get; } = new();

        public static PrototypeData ParseFile(byte[] data)
        {
            using BinaryReader reader = new(new MemoryStream(data));
            reader.ReadUInt32();    // CalligraphyHeader
            PrototypeData result = Parse(reader);
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException($"Prototype parse ended at {reader.BaseStream.Position} of {reader.BaseStream.Length} bytes.");
            return result;
        }

        private static PrototypeData Parse(BinaryReader reader)
        {
            PrototypeData proto = new();
            byte flags = reader.ReadByte();
            if ((flags & 1) == 0) return proto;

            proto.ParentDataRef = reader.ReadUInt64();
            if ((flags & 2) == 0) return proto;
            proto.HasData = true;

            short numGroups = reader.ReadInt16();
            for (int g = 0; g < numGroups; g++)
            {
                ulong groupBlueprintId = reader.ReadUInt64();
                reader.ReadByte();      // blueprint copy num
                List<ulong> groupFields = new();
                proto.Groups.Add((groupBlueprintId, groupFields));

                short numSimple = reader.ReadInt16();
                for (int i = 0; i < numSimple; i++)
                {
                    ulong fieldId = reader.ReadUInt64();
                    groupFields.Add(fieldId);
                    if (reader.ReadByte() == RHStruct)
                        proto.SimpleStructs[fieldId] = Parse(reader);
                    else
                        proto.Simple[fieldId] = reader.ReadUInt64();
                }

                short numLists = reader.ReadInt16();
                for (int i = 0; i < numLists; i++)
                {
                    ulong fieldId = reader.ReadUInt64();
                    groupFields.Add(fieldId);
                    byte baseType = reader.ReadByte();
                    short count = reader.ReadInt16();
                    if (baseType == RHStruct)
                    {
                        List<PrototypeData> items = new(count);
                        for (int j = 0; j < count; j++) items.Add(Parse(reader));
                        proto.StructLists[fieldId] = items;
                    }
                    else
                    {
                        List<ulong> items = new(count);
                        for (int j = 0; j < count; j++) items.Add(reader.ReadUInt64());
                        proto.Lists[fieldId] = items;
                    }
                }
            }

            return proto;
        }
    }
}
