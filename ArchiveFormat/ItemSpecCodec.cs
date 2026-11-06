namespace MHDBEditor.ArchiveFormat
{
    // Decodes/encodes an ItemSpec, matching MHServerEmu.Games.Entities.Items.ItemSpec.Serialize()
    // combined with the confirmed Serializer.cs behavior for PrototypeId (persistent archives:
    // transferred as a plain ulong PrototypeGuid) and List<T> where T : ISerialize (a ulong
    // count, then each element through the generic ISerialize path - which auto-size-prefixes
    // on persistent archives, same as ItemSpec itself does when Item.cs transfers it).
    public static class ItemSpecCodec
    {
        public static ItemSpecRecord Decode(ArchiveCursor cursor)
        {
            int expectedEnd = cursor.ReadSizePrefixHeader();

            ItemSpecRecord record = new()
            {
                ItemProtoGuid = cursor.ReadVarUInt64(),
                RarityProtoGuid = cursor.ReadVarUInt64(),
                ItemLevel = cursor.ReadZigZagInt32(),
                CreditsAmount = cursor.ReadZigZagInt32(),
            };

            ulong affixCount = cursor.ReadVarUInt64();
            for (ulong i = 0; i < affixCount; i++)
                record.Affixes.Add(DecodeAffix(cursor));

            record.Seed = cursor.ReadZigZagInt32();
            record.EquippableByGuid = cursor.ReadVarUInt64();

            cursor.ExpectPosition(expectedEnd, "ItemSpec");

            return record;
        }

        private static AffixSpecRecord DecodeAffix(ArchiveCursor cursor)
        {
            int expectedEnd = cursor.ReadSizePrefixHeader();

            AffixSpecRecord affix = new()
            {
                AffixProtoGuid = cursor.ReadVarUInt64(),
                ScopeProtoGuid = cursor.ReadVarUInt64(),
                Seed = cursor.ReadZigZagInt32(),
            };

            cursor.ExpectPosition(expectedEnd, "AffixSpec");

            return affix;
        }

        public static void Encode(ArchiveCursorWriter writer, ItemSpecRecord record)
        {
            int sizePos = writer.ReserveUnencodedUInt32();

            writer.WriteVarUInt64(record.ItemProtoGuid);
            writer.WriteVarUInt64(record.RarityProtoGuid);
            writer.WriteZigZagInt32(record.ItemLevel);
            writer.WriteZigZagInt32(record.CreditsAmount);

            writer.WriteVarUInt64((ulong)record.Affixes.Count);
            foreach (AffixSpecRecord affix in record.Affixes)
                EncodeAffix(writer, affix);

            writer.WriteZigZagInt32(record.Seed);
            writer.WriteVarUInt64(record.EquippableByGuid);

            writer.PatchUnencodedUInt32(sizePos, (uint)(writer.Position - sizePos));
        }

        private static void EncodeAffix(ArchiveCursorWriter writer, AffixSpecRecord affix)
        {
            int sizePos = writer.ReserveUnencodedUInt32();

            writer.WriteVarUInt64(affix.AffixProtoGuid);
            writer.WriteVarUInt64(affix.ScopeProtoGuid);
            writer.WriteZigZagInt32(affix.Seed);

            writer.PatchUnencodedUInt32(sizePos, (uint)(writer.Position - sizePos));
        }
    }
}
