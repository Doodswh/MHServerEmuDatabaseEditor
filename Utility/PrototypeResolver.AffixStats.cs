using System;
using System.Collections.Generic;
using System.Linq;

namespace MHDBEditor.Utility
{
    // What an affix actually grants, in the order Item.OnAffixAdded() applies it:
    //   1. the affix prototype's own Properties collection (fixed values, inherited from parents)
    //   2. AffixPowerModifier only: PowerBoost, then PowerGrantRank - each rolled with
    //      random.NextFloat() only when its max eval is > 0
    //   3. PropertyEntries, in order, one random.NextFloat() each
    // where random is GRandom(affix seed): the saved AffixSpec.Seed for rolled affixes, or
    // GenAffixRandomSeed() for built-in ones. So values are this item's real rolls, not ranges.
    //
    // Also checks every condition under which the server silently drops an affix (it logs a
    // warning and moves on) or stops applying the item's affixes altogether - which is what
    // matters for items made without the normal AllowAttachment / affix limit checks.
    public partial class PrototypeResolver
    {
        // Worst first is the higher value, so Max() over several affixes gives the item's state.
        public enum AffixHealth { Works, Metadata, Hidden, Partial, Inert, BreaksItem }

        public class AffixStat
        {
            public string Name { get; init; }
            public string For { get; init; }
            public string Value { get; init; }
            public string Details { get; init; }

            // No tooltip text on the property: it applies, but the client has nothing to show.
            public bool Hidden { get; init; }
            // The server skips this entry (unsupported property type / bad range).
            public bool Skipped { get; init; }
            // The property's own Min/Max clamp, when it has one, and the rolled number.
            public float? Number { get; init; }
            public float? CapMin { get; init; }
            public float? CapMax { get; init; }
            public bool SumsWhenStacked { get; init; }

            public string CappedAt => Number == null ? null
                : CapMax != null && Number > CapMax ? FormatNumber(CapMax.Value)
                : CapMin != null && Number < CapMin ? FormatNumber(CapMin.Value)
                : null;

            public override string ToString()
            {
                string text = For == "-" ? $"{Name} {Value}" : $"{Name} [{For}] {Value}";
                if (Skipped) text += " (ignored by server)";
                if (Hidden) text += " (hidden)";
                if (CappedAt != null) text += $" (capped at {CappedAt})";
                return text;
            }
        }

        public class AffixReport
        {
            public AffixHealth Health { get; private set; } = AffixHealth.Works;
            public List<AffixStat> Stats { get; } = new();
            public List<string> Problems { get; } = new();
            public List<string> Notes { get; } = new();

            public void Flag(AffixHealth health, string problem)
            {
                if (health > Health) Health = health;
                Problems.Add(problem);
            }

            public string StatusText => Health switch
            {
                AffixHealth.Works => "Works",
                AffixHealth.Metadata => "No stats (normal)",
                AffixHealth.Hidden => $"Works - {Stats.Count(s => s.Hidden)} hidden stat(s)",
                AffixHealth.Partial => "Partly works",
                AffixHealth.Inert => "Does nothing",
                _ => "BREAKS ITEM",
            };

            public string Describe()
            {
                List<string> lines = new() { StatusText };
                lines.AddRange(Problems.Select(p => "- " + p));
                lines.AddRange(Notes.Select(n => "Note: " + n));
                if (Stats.Count > 0)
                {
                    lines.Add(string.Empty);
                    lines.AddRange(Stats.Select(s => s.Details != null ? $"{s}    ({s.Details})" : s.ToString()));
                }
                return string.Join("\n", lines);
            }
        }

        // One line for a grid cell, e.g. "HealthAddBonus 320; DamageRating 45".
        public static string SummarizeAffixStats(List<AffixStat> stats)
        {
            if (stats == null) return string.Empty;
            if (stats.Count == 0) return "(no stats)";
            return string.Join("; ", stats);
        }

        public static string DescribeAffixStats(List<AffixStat> stats)
        {
            if (stats == null || stats.Count == 0) return null;
            return string.Join("\n", stats.Select(s => s.Details != null ? $"{s}    ({s.Details})" : s.ToString()));
        }

        // Stats that add up across affixes past the property's own clamp, e.g.
        // "DamageRating: 71,000 total, capped at 65535". Single affixes over the cap are
        // already marked on the stat itself.
        public static List<string> FindStackedCaps(IEnumerable<AffixStat> stats)
        {
            List<string> result = new();
            foreach (var group in stats.Where(s => s.Number != null && s.Skipped == false && s.SumsWhenStacked && (s.CapMax != null || s.CapMin != null))
                .GroupBy(s => (s.Name, s.For)))
            {
                if (group.Count() < 2) continue;
                AffixStat first = group.First();
                float total = group.Sum(s => s.Number.Value);
                string forText = first.For == "-" ? "" : $" [{first.For}]";
                if (first.CapMax != null && total > first.CapMax)
                    result.Add($"{first.Name}{forText}: {FormatNumber(total)} total, capped at {FormatNumber(first.CapMax.Value)}");
                else if (first.CapMin != null && total < first.CapMin)
                    result.Add($"{first.Name}{forText}: {FormatNumber(total)} total, capped at {FormatNumber(first.CapMin.Value)}");
            }
            return result;
        }

        public List<AffixStat> GetAffixStats(ulong affixGuid, ulong scopeGuid, ulong avatarGuid, int itemLevel, int seed)
            => GetAffixReport(affixGuid, scopeGuid, avatarGuid, itemLevel, seed, savedAffix: false)?.Stats;

        // Returns null if game data isn't loaded. avatarGuid is the item's EquippableBy (what
        // OnAffixAdded receives for rolled affixes). savedAffix = from the ItemSpec (vs built in),
        // where a bad entry makes ApplyItemSpec() return early instead of just skipping it.
        public AffixReport GetAffixReport(ulong affixGuid, ulong scopeGuid, ulong avatarGuid, int itemLevel, int seed, bool savedAffix)
        {
            if (_gameData == null) return null;
            AffixReport report = new();

            _byGuid.TryGetValue(affixGuid, out Entry affix);
            PrototypeData data = affix != null ? GetPrototypeData(affix.DataRef) : null;
            // affixSpec.AffixProto is As<AffixPrototype>(), so anything else is null to the server.
            string affixClass = GetRuntimeClass(affixGuid);
            if (data == null || affixClass == null || affixClass.StartsWith("Affix") == false)
            {
                string what = affix == null ? $"0x{affixGuid:X} isn't in the game data" : $"{affix.DisplayName} isn't an affix";
                if (savedAffix)
                    report.Flag(AffixHealth.BreaksItem, $"{what}. The server stops applying this item's affixes here - this and every affix after it do nothing.");
                else
                    report.Flag(AffixHealth.Inert, $"{what} - skipped.");
                return report;
            }

            if (savedAffix && seed == 0)
            {
                report.Flag(AffixHealth.BreaksItem, "Seed is 0. The server stops applying this item's affixes here - this and every affix after it do nothing. Give it any other seed.");
                return report;
            }

            _assetIdToName.TryGetValue(GetInheritedSimple(data, "Position"), out string position);
            if (position == "Metadata")
            {
                report.Flag(AffixHealth.Metadata, "Metadata affix - stores info about the item, never has stats.");
                return report;
            }

            bool hasFixedProperties = AddFixedAffixProperties(data, report.Stats);
            List<PrototypeData> entries = FindInheritedStructList(data, "PropertyEntries");
            bool isPowerModifier = affixClass == "AffixPowerModifierPrototype";
            bool isRegionModifier = affixClass == "AffixRegionModifierPrototype";

            // AffixPrototype.HasBonusPropertiesToApply
            bool hasBonus = hasFixedProperties || entries != null || isRegionModifier
                || (isPowerModifier && (GetInheritedStruct(data, "PowerBoostMax") != null || GetInheritedStruct(data, "PowerGrantRankMax") != null));
            if (hasBonus == false && affix.Path.Contains("NoVisuals", StringComparison.OrdinalIgnoreCase) == false)
            {
                report.Flag(AffixHealth.Inert, "Has nothing to apply - the server skips it.");
                return report;
            }

            Mwc random = seed != 0 ? new Mwc((uint)seed) : null;
            string scopeText = scopeGuid != 0 ? Format(scopeGuid) : null;
            bool scopedToOtherHero = false;

            if (isPowerModifier)
            {
                bool singlePower = GetInheritedSimple(data, "IsForSinglePowerOnly") != 0;
                ulong keywordFilter = GuidFromDataRef(GetInheritedSimple(data, "PowerKeywordFilter"));
                ulong progTab = GuidFromDataRef(GetInheritedSimple(data, "PowerProgTableTabRef"));
                int var2 = 0;

                // Same checks, in the same order, as OnAffixAdded - any failure drops the affix.
                if (singlePower)
                {
                    bool isGem = position != null && position.StartsWith("Socket");
                    if (IsClass(scopeGuid, "PowerPrototype") == false)
                        return Dropped(report, $"Needs a single power as its scope, but the scope is {scopeText ?? "(none)"}.");
                    // Gems skip the "no avatar" warning but still fail the avatar lookup after it.
                    if (avatarGuid == 0)
                        return Dropped(report, $"Boosts one hero power, but the item has no Equippable By hero{(isGem ? " (gems too)" : "")}.");
                    if (IsClass(avatarGuid, "AvatarPrototype") == false)
                        return Dropped(report, $"Equippable By ({Format(avatarGuid)}) isn't a hero.");
                    int? unlockLevel = GetPowerUnlockLevel(avatarGuid, scopeGuid);
                    if (unlockLevel == null)
                        return Dropped(report, $"{scopeText} isn't one of {Format(avatarGuid)}'s powers.");
                    var2 = unlockLevel.Value;
                }
                else if (progTab != 0)
                {
                    if (IsClass(scopeGuid, "AvatarPrototype") == false)
                        return Dropped(report, $"Boosts a hero's power tab, so its scope must be a hero - it's {scopeText ?? "(none)"}.");
                    if (avatarGuid != 0 && scopeGuid != avatarGuid)
                        scopedToOtherHero = true;
                }
                else if (keywordFilter != 0 && scopeGuid != 0)
                {
                    return Dropped(report, $"Boosts every {Format(keywordFilter)} power, so it must have no scope - it has {scopeText}.");
                }

                string target = keywordFilter != 0 ? Format(keywordFilter)
                    : progTab != 0 ? $"{Format(progTab)}{(scopeText != null ? ", " + scopeText : "")}"
                    : scopeText ?? "-";

                // A field the affix doesn't set is 0 (no roll); only a formula we can't work out is null.
                float? EvalOptional(string field) =>
                    GetInheritedStruct(data, field) is PrototypeData s ? Eval(s, itemLevel, 0, itemLevel, var2) : 0f;

                float? boostMax = EvalOptional("PowerBoostMax");
                if (boostMax == null || boostMax > 0)
                {
                    float? boostMin = EvalOptional("PowerBoostMin");
                    report.Stats.Add(RolledInt("PowerBoost", target, random, boostMin, boostMax, boostMax));
                }

                float? grantMax = EvalOptional("PowerGrantRankMax");
                if (grantMax == null || grantMax > 0)
                {
                    float? grantMin = EvalOptional("PowerGrantRankMin");
                    // The server rolls this between grantMin and the *boost* max - matched as-is.
                    report.Stats.Add(RolledInt("PowerGrantRank", target, random, grantMin, boostMax, grantMax));
                }
            }
            else if (isRegionModifier)
            {
                if (IsClass(scopeGuid, "RegionAffixPrototype") == false)
                    return Dropped(report, $"Region modifier without a region affix scope (scope is {scopeText ?? "(none)"}).");
                report.Stats.Add(new AffixStat { Name = "RegionAffix", For = scopeText, Value = "true" });
                report.Notes.Add("Region modifiers only do something on portal / challenge items.");
            }

            foreach (PrototypeData entry in entries ?? Enumerable.Empty<PrototypeData>())
            {
                // Consumed even for entries the server then skips, so later rolls stay in step.
                float randomMult = random?.NextFloat() ?? 0f;
                PrototypeData prop = GetInheritedStruct(entry, "Prop");
                var (name, forText, dataType) = DescribeProp(prop);
                Entry info = prop != null ? FindPropertyInfo(RootDefaultsName(prop) ?? string.Empty) : null;

                float? min = GetInheritedStruct(entry, "ValueMin") is PrototypeData minEval ? Eval(minEval, itemLevel) : 0f;
                float? max = GetInheritedStruct(entry, "ValueMax") is PrototypeData maxEval ? Eval(maxEval, itemLevel) : 0f;
                bool rollAsInteger = GetInheritedSimple(entry, "RollAsInteger") != 0;

                if (dataType is not ("Boolean" or "Real" or "Integer"))
                {
                    report.Stats.Add(new AffixStat { Name = name, For = forText, Value = "-", Skipped = true, Details = $"{dataType ?? "unknown"} properties can't be rolled" });
                    report.Flag(AffixHealth.Partial, $"{name} is a {dataType ?? "unknown"} property - the server only rolls int / float / bool, so it skips this one.");
                    continue;
                }

                if (min == null || max == null)
                {
                    report.Stats.Add(new AffixStat { Name = name, For = forText, Value = "(formula)", Details = "range isn't a simple formula", Hidden = IsHiddenProperty(info) });
                    continue;
                }

                if (dataType == "Boolean" && (min < 0 || max > 1))
                {
                    report.Stats.Add(new AffixStat { Name = name, For = forText, Value = "-", Skipped = true, Details = $"bool range {FormatNumber(min.Value)} - {FormatNumber(max.Value)}" });
                    report.Flag(AffixHealth.Partial, $"{name} is a bool with a range outside 0-1 - the server skips it.");
                    continue;
                }

                string range = min == max ? FormatNumber(min.Value) : $"{FormatNumber(min.Value)} – {FormatNumber(max.Value)}";
                float? number = random == null ? null : dataType switch
                {
                    "Real" when rollAsInteger == false => (max.Value - min.Value) * randomMult + min.Value,
                    _ => TruncatedWithinRange(randomMult, min.Value, max.Value),
                };
                report.Stats.Add(PropertyStat(info, name, forText, number != null ? FormatNumber(number.Value) : range, number, $"range {range}"));
            }

            foreach (AffixStat stat in report.Stats.Where(s => s.Hidden && s.Skipped == false))
                report.Flag(AffixHealth.Hidden, $"{stat.Name} has no tooltip text, so it applies but the game won't show it.");

            if (report.Health == AffixHealth.Works && report.Stats.All(s => s.Skipped || s.Number == 0))
            {
                if (position == "Visual")
                    report.Flag(AffixHealth.Metadata, "Cosmetic affix - changes how the item looks, no stats.");
                else if (position != null && position.StartsWith("Socket") && report.Stats.Count == 0)
                    report.Flag(AffixHealth.Metadata, "Empty gem socket - no stats until a gem goes in.");
                else
                    report.Flag(AffixHealth.Inert, $"Applies, but everything it grants comes out 0 at item level {itemLevel} (that's what the game data says, not a roll).");
            }

            if (scopedToOtherHero)
                report.Flag(AffixHealth.Inert, $"Boosts {scopeText}'s powers, but the item is for {Format(avatarGuid)} - nothing the wearer uses.");

            if (affixClass == "AffixTeamUpPrototype" && GetInheritedSimple(data, "IsAppliedToOwnerAvatar") != 0)
                report.Notes.Add("Applies to the team-up's owner hero, not to the item itself.");
            if (position != null && position.StartsWith("PetTech"))
                report.Notes.Add("Pet tech: only applies once that tech slot is unlocked on the pet.");

            return report;
        }

        private static AffixReport Dropped(AffixReport report, string reason)
        {
            report.Stats.Clear();
            report.Flag(AffixHealth.Inert, reason + " The server drops the whole affix.");
            return report;
        }

        // AvatarPrototype.GetPowerProgressionEntryForPower(power).Level, or null if the hero
        // doesn't have that power. This is the Var2 a single-power affix's evals read.
        private int? GetPowerUnlockLevel(ulong avatarGuid, ulong powerGuid)
        {
            if (_byGuid.TryGetValue(avatarGuid, out Entry avatar) == false || _byGuid.TryGetValue(powerGuid, out Entry power) == false)
                return null;

            foreach (PrototypeData table in FindInheritedStructList(GetPrototypeData(avatar.DataRef), "PowerProgressionTables") ?? Enumerable.Empty<PrototypeData>())
            {
                foreach (PrototypeData entry in FindInheritedStructList(table, "PowerProgressionEntries") ?? Enumerable.Empty<PrototypeData>())
                {
                    if (GetInheritedSimple(GetInheritedStruct(entry, "PowerAssignment"), "Ability") == power.DataRef)
                        return unchecked((int)GetInheritedSimple(entry, "Level"));
                }
            }
            return null;
        }

        // The client shows a stat line from the property's TooltipText - none means no line.
        private bool IsHiddenProperty(Entry info) =>
            info != null && GetPrototypeData(info.DataRef) is PrototypeData data && GetInheritedSimple(data, "TooltipText") == 0;

        private AffixStat PropertyStat(Entry info, string name, string forText, string value, float? number, string details)
        {
            float? capMin = null, capMax = null;
            bool sums = true;
            if (info != null && GetPrototypeData(info.DataRef) is PrototypeData data)
            {
                // PropertyInfoPrototype.ShouldClampValue: clamped when either bound is non-zero.
                float min = (float)BitConverter.Int64BitsToDouble(unchecked((long)GetInheritedSimple(data, "Min")));
                float max = (float)BitConverter.Int64BitsToDouble(unchecked((long)GetInheritedSimple(data, "Max")));
                if (min != 0 || max != 0) { capMin = min; capMax = max; }
                _assetIdToName.TryGetValue(GetInheritedSimple(data, "AggMethod"), out string aggMethod);
                sums = aggMethod == null || aggMethod == "Sum";
            }

            return new AffixStat
            {
                Name = name, For = forText, Value = value, Details = details,
                Hidden = IsHiddenProperty(info), Number = number, CapMin = capMin, CapMax = capMax, SumsWhenStacked = sums,
            };
        }

        private static AffixStat RolledInt(string name, string target, Mwc random, float? min, float? rollMax, float? shownMax)
        {
            if (min == null || rollMax == null || shownMax == null)
            {
                // The server only consumes a roll when max > 0; without the value we can't
                // know, so assume it did (it does for every affix in shipping data that has one).
                random?.NextFloat();
                return new AffixStat { Name = name, For = target, Value = "(formula)", Details = "range isn't a simple formula" };
            }

            float randomMult = random?.NextFloat() ?? 0f;
            string range = min == shownMax ? FormatNumber(min.Value) : $"{FormatNumber(min.Value)} – {FormatNumber(shownMax.Value)}";
            float? number = random == null ? null : (int)TruncatedWithinRange(randomMult, min.Value, rollMax.Value);
            return new AffixStat { Name = name, For = target, Value = number != null ? FormatNumber(number.Value) : range, Number = number, Details = $"range {range}" };
        }

        // The affix's own property collection: property mixin field groups directly in the
        // prototype file. Children override parents (FlattenCopyFrom), so nearer wins.
        // Returns whether the affix has a property collection at all.
        private bool AddFixedAffixProperties(PrototypeData data, List<AffixStat> stats)
        {
            bool any = false;
            HashSet<string> seen = new();
            for (PrototypeData x = data; x != null; x = x.ParentDataRef != 0 ? GetPrototypeData(x.ParentDataRef) : null)
            {
                foreach (var (blueprintId, fieldIds) in x.Groups)
                {
                    string blueprintPath = _gameData.GetBlueprintPath(blueprintId);
                    if (blueprintPath == null || blueprintPath.StartsWith("Property/") == false || blueprintPath.StartsWith("Property/Property"))
                        continue;
                    any = true;

                    string mixinName = System.IO.Path.GetFileNameWithoutExtension(blueprintPath);
                    List<(ulong, ulong, ulong)> fields = fieldIds.Where(f => x.Simple.ContainsKey(f)).Select(f => (blueprintId, f, x.Simple[f])).ToList();
                    var (name, forText, _) = DescribeMixin(mixinName, fields);
                    if (seen.Add($"{name}|{forText}") == false) continue;

                    ulong valueField = fieldIds.FirstOrDefault(f => _gameData.GetFieldName(f) == "Value");
                    byte valueType = valueField != 0 ? _gameData.GetMemberType(blueprintId, valueField) : (byte)0;
                    string value = valueField != 0 && x.Simple.TryGetValue(valueField, out ulong raw)
                        ? FormatRaw(valueType, raw)
                        : "(default)";

                    // Base affix defaults carry zeroed placeholders (EquipmentSetLevel 0, Socket none)
                    // that change nothing - leave them out so the real stats are readable.
                    if (value is "0" or "(none)" or "false" or "(default)") continue;
                    if (value == "(curve)") value = "(scales by curve)";

                    float? number = float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? parsed : null;
                    stats.Add(PropertyStat(FindPropertyInfo(mixinName), name, forText, value, number, "fixed"));
                }
            }
            return any;
        }

        private List<PrototypeData> FindInheritedStructList(PrototypeData data, string fieldName)
        {
            for (int depth = 0; data != null && depth < 32; depth++)
            {
                List<PrototypeData> list = FindStructList(data, fieldName);
                if (list != null) return list;
                data = data.ParentDataRef != 0 ? GetPrototypeData(data.ParentDataRef) : null;
            }
            return null;
        }

        // ItemPrototype.GenAffixRandomSeed - the seed a built-in affix is rolled with. The
        // counter makes repeated (item, affix, power) combinations roll differently.
        private static int GenAffixRandomSeed(Dictionary<ulong, int> affixSeedDict, int itemSeed, ulong itemGuid, ulong affixGuid, ulong powerGuid)
        {
            ulong affixSeed = itemGuid ^ affixGuid;
            if (powerGuid != 0)
                affixSeed ^= powerGuid;

            int count = affixSeedDict.GetValueOrDefault(affixSeed);
            affixSeedDict[affixSeed] = count + 1;

            affixSeed = (affixSeed >> count) & 0xFFFFFFFF;
            return itemSeed ^ unchecked((int)affixSeed);
        }
    }
}
