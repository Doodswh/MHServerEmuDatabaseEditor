using System;
using System.Collections.Generic;
using System.IO;

namespace MHDBEditor.ArchiveFormat
{
    // Full result of parsing one DBEntity.ArchiveData blob (Database archive type). Properties
    // is the editable, decoded part. TailBytes is everything after the properties block -
    // ConditionCollection plus whatever type-specific data follows it - preserved byte-for-byte
    // and used verbatim by Rebuild() UNLESS ItemSpec below was successfully decoded, in which
    // case the tail is reconstructed from ConditionCollectionBytes + a re-encoded ItemSpec
    // instead, so editing rarity/level/credits/seed/equippableBy/affixes round-trips correctly
    // while everything else still passes through unchanged.
    public class ParsedEntityArchive
    {
        public uint Version { get; set; }
        public uint GameBuildNumber { get; set; }
        public List<PropertyRecord> Properties { get; set; } = new();
        public byte[] TailBytes { get; set; } = Array.Empty<byte>();

        // Populated only when the tail was successfully and completely decoded as
        // [ConditionCollection][ItemSpec] with nothing left over - i.e. only for items, and
        // only once this format assumption has actually held for this specific blob.
        public byte[] ConditionCollectionBytes { get; set; }
        public ItemSpecRecord ItemSpec { get; set; }

        public byte[] Rebuild()
        {
            ArchiveCursorWriter writer = new();
            writer.WriteUnencodedUInt32(0); // placeholder for the outer total-size field
            writer.WriteVarUInt32(Version);
            writer.WriteVarUInt32(GameBuildNumber);

            // The whole entity is itself transferred as one ISerialize object (confirmed by
            // testing against real ArchiveData - the properties block doesn't start right after
            // the header), so it carries its own size prefix wrapping everything that follows.
            int entitySizePos = writer.ReserveUnencodedUInt32();

            PropertyBlockCodec.Encode(writer, Properties);

            if (ItemSpec != null)
            {
                writer.WriteRawBytes(ConditionCollectionBytes);
                ItemSpecCodec.Encode(writer, ItemSpec);
            }
            else
            {
                writer.WriteRawBytes(TailBytes);
            }

            writer.PatchUnencodedUInt32(entitySizePos, (uint)(writer.Position - entitySizePos));

            byte[] result = writer.ToArray();

            // Patch the leading 4-byte total-size field, matching Archive.UpdateSizeInArchive().
            byte[] sizeBytes = BitConverter.GetBytes((uint)result.Length);
            Array.Copy(sizeBytes, 0, result, 0, 4);

            return result;
        }
    }

    public static class EntityArchiveCodec
    {
        public static ParsedEntityArchive Parse(byte[] data)
        {
            if (data == null || data.Length < 4)
                throw new InvalidDataException("ArchiveData is empty or too short to be a Database-archive entity blob.");

            ArchiveCursor cursor = new(data);

            uint totalSize = cursor.ReadUnencodedUInt32();
            if (totalSize != data.Length)
            {
                throw new InvalidDataException(
                    $"Header size ({totalSize}) doesn't match blob length ({data.Length}) - " +
                    "this may not be a Database-archive entity blob, or the format has changed.");
            }

            uint version = cursor.ReadVarUInt32();
            uint gameBuildNumber = cursor.ReadVarUInt32();

            // The entity itself is transferred as an ISerialize object, which carries its own
            // 4-byte size prefix (covering Properties + everything after it) - step past it
            // before reading the properties block.
            int expectedEntityEnd = cursor.ReadSizePrefixHeader();

            List<PropertyRecord> properties = PropertyBlockCodec.Decode(cursor);

            byte[] tail = cursor.ReadRemainingBytes();

            // The entity's declared end should land exactly at the end of the buffer - nothing
            // else follows it in a Database archive. If it doesn't, something about this blob
            // doesn't match the assumption above, and it's better to fail loudly here than
            // produce a tail that looks fine but silently omits or includes the wrong bytes.
            cursor.ExpectPosition(expectedEntityEnd, "Entity");

            ParsedEntityArchive result = new()
            {
                Version = version,
                GameBuildNumber = gameBuildNumber,
                Properties = properties,
                TailBytes = tail,
            };

            TryDecodeItemSpecTail(result);

            return result;
        }

        // Best-effort: only ever called with the tail bytes already captured, and only ever
        // populates ItemSpec/ConditionCollectionBytes on complete success (including consuming
        // every byte of the tail). Any failure - wrong entity type, an assumption not holding -
        // leaves both null, and Rebuild() falls back to replaying TailBytes verbatim, exactly
        // as it always has. This can never make a save less safe than before this was added.
        private static void TryDecodeItemSpecTail(ParsedEntityArchive result)
        {
            try
            {
                byte[] tail = result.TailBytes;
                ArchiveCursor ccCursor = new(tail);

                int ccStart = ccCursor.Position;
                int ccEnd = ccCursor.ReadSizePrefixHeader();
                if (ccEnd < ccCursor.Position || ccEnd > tail.Length)
                    return;

                byte[] conditionCollectionBytes = new byte[ccEnd - ccStart];
                Array.Copy(tail, ccStart, conditionCollectionBytes, 0, conditionCollectionBytes.Length);

                ArchiveCursor itemSpecCursor = new(tail);
                itemSpecCursor.ReadRawBytes(ccEnd - itemSpecCursor.Position); // skip past ConditionCollection

                ItemSpecRecord itemSpec = ItemSpecCodec.Decode(itemSpecCursor);

                if (itemSpecCursor.Position != tail.Length)
                    return; // leftover bytes - this isn't an Item, or the assumption doesn't hold

                // Only keep it if saving it back gives the exact same bytes. A non-item tail that
                // happens to look like an ItemSpec, or a value too big for the decoder, won't.
                ArchiveCursorWriter check = new();
                check.WriteRawBytes(conditionCollectionBytes);
                ItemSpecCodec.Encode(check, itemSpec);
                if (check.ToArray().AsSpan().SequenceEqual(tail) == false)
                    return;

                result.ConditionCollectionBytes = conditionCollectionBytes;
                result.ItemSpec = itemSpec;
            }
            catch
            {
                result.ConditionCollectionBytes = null;
                result.ItemSpec = null;
            }
        }
    }
}
