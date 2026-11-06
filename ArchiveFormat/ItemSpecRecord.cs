using System;
using System.Collections.Generic;

namespace MHDBEditor.ArchiveFormat
{
    // One decoded affix roll, matching MHServerEmu.Games.Entities.Items.AffixSpec.Serialize()
    // exactly: affixProtoRef and _scopeProtoRef are PrototypeId fields, which on persistent
    // archives are transferred as their PrototypeGuid (confirmed in Serializer.cs).
    public class AffixSpecRecord
    {
        public ulong AffixProtoGuid { get; set; }
        public ulong ScopeProtoGuid { get; set; }
        public int Seed { get; set; }

        public string AffixProtoGuidText
        {
            get => $"0x{AffixProtoGuid:X}";
            set => AffixProtoGuid = ParseGuidText(value);
        }

        public string ScopeProtoGuidText
        {
            get => $"0x{ScopeProtoGuid:X}";
            set => ScopeProtoGuid = ParseGuidText(value);
        }

        private static ulong ParseGuidText(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt64(text, 16);
            return ulong.Parse(text);
        }
    }

    // A decoded ItemSpec, matching MHServerEmu.Games.Entities.Items.ItemSpec.Serialize() field
    // order exactly: _itemProtoRef, _rarityProtoRef, _itemLevel, _creditsAmount,
    // _affixSpecList, _seed, _equippableBy.
    public class ItemSpecRecord
    {
        public ulong ItemProtoGuid { get; set; }
        public ulong RarityProtoGuid { get; set; }
        public int ItemLevel { get; set; }
        public int CreditsAmount { get; set; }
        public List<AffixSpecRecord> Affixes { get; set; } = new();
        public int Seed { get; set; }
        public ulong EquippableByGuid { get; set; }
    }
}
