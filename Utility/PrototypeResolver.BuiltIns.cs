using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MHDBEditor.Utility
{
    // Stats an item gets from the game data instead of its saved ItemSpec. The server rebuilds
    // these every time the item loads (Item.ApplyItemSpec), so they're never in the DB and can't
    // be edited per item - but they're most of what a medal/unique/artifact actually does, so
    // the editor shows them read-only next to the saved affixes.
    //
    // Mirrors Item.ApplyItemSpec():
    //   1. PropertiesBuiltIn on the item prototype, in order, each consuming one
    //      GRandom(itemSpec.Seed).NextFloat() - PickInRange entries roll between ValueMin and
    //      ValueMax with it (OnBuiltInPropertyRoll), Set entries just evaluate Value.
    //   2. Built-in affixes (item, then rarity) - see GetBuiltInAffixes.
    // Values come from the same evals the server runs; only the handful of eval kinds that
    // item data actually uses are supported (constants, Mult/Div/Add/Sub, LoadProp of the item
    // level, item-level curves). Anything else shows its range or "(formula)" instead.
    public partial class PrototypeResolver
    {
        private readonly Dictionary<ulong, string> _assetIdToName = new();

        public class BuiltInStat
        {
            public string Kind { get; init; }
            public string Name { get; init; }
            public string For { get; init; }
            public string Value { get; init; }
            public string Source { get; init; }
            public string Details { get; init; }
            public AffixReport Report { get; init; }
        }

        public List<BuiltInStat> GetBuiltInStats(ulong itemGuid, ulong rarityGuid, int itemLevel, int seed, ulong equippableByGuid)
        {
            if (_gameData == null) return null;

            List<BuiltInStat> result = new();
            AddBuiltInProperties(itemGuid, itemLevel, seed, result);

            // ItemPrototype.GenerateBuiltInAffixDetails + GeneratePowerModifierRefFromBuiltInAffix
            Dictionary<ulong, int> affixSeedDict = new();
            foreach (BuiltInAffix affix in GetBuiltInAffixes(itemGuid, rarityGuid))
            {
                List<string> forParts = new();
                if (affix.AvatarGuid != 0) forParts.Add(Format(affix.AvatarGuid));
                if (affix.PowerGuid != 0) forParts.Add(Format(affix.PowerGuid));

                ulong scopeGuid = 0;
                ulong avatarGuid = equippableByGuid;
                string skipped = null;
                if (_byGuid.TryGetValue(affix.AffixGuid, out Entry affixEntry) && GetPrototypeData(affixEntry.DataRef) is PrototypeData affixData
                    && GetRuntimeClass(affix.AffixGuid) == "AffixPowerModifierPrototype")
                {
                    avatarGuid = equippableByGuid != 0 ? equippableByGuid : affix.AvatarGuid;
                    if (affix.AvatarGuid != 0 && affix.AvatarGuid != equippableByGuid)
                        skipped = $"Not applied: needs Equippable By = {Format(affix.AvatarGuid)}";
                    else if (GetInheritedSimple(affixData, "IsForSinglePowerOnly") != 0)
                        scopeGuid = affix.PowerGuid;
                    else if (GetInheritedSimple(affixData, "PowerKeywordFilter") == 0 && GetInheritedSimple(affixData, "PowerProgTableTabRef") != 0)
                        scopeGuid = avatarGuid;
                }

                AffixReport report = null;
                if (skipped == null)
                {
                    int affixSeed = GenAffixRandomSeed(affixSeedDict, seed, itemGuid, affix.AffixGuid, affix.PowerGuid);
                    report = GetAffixReport(affix.AffixGuid, scopeGuid, avatarGuid, itemLevel, affixSeed, savedAffix: false);
                }

                string value = skipped;
                if (report != null)
                {
                    value = SummarizeAffixStats(report.Stats);
                    if (report.Health is AffixHealth.Partial or AffixHealth.Inert)
                        value = $"[{report.StatusText}: {report.Problems.FirstOrDefault()}] {value}";
                }

                result.Add(new BuiltInStat
                {
                    Kind = "Affix",
                    Name = Format(affix.AffixGuid),
                    For = forParts.Count > 0 ? string.Join(", ", forParts) : "-",
                    Value = value,
                    Source = affix.Source,
                    Details = (TryGetEntry(affix.AffixGuid, out Entry e) ? e.Path : null) + (report != null ? "\n" + report.Describe() : ""),
                    Report = report,
                });
            }

            return result;
        }

        private void AddBuiltInProperties(ulong itemGuid, int itemLevel, int seed, List<BuiltInStat> result)
        {
            if (_byGuid.TryGetValue(itemGuid, out Entry item) == false) return;

            List<PrototypeData> entries = null;
            ulong ownerRef = 0;
            for (ulong dataRef = item.DataRef; dataRef != 0 && entries == null; )
            {
                PrototypeData data = GetPrototypeData(dataRef);
                if (data == null) break;
                entries = FindStructList(data, "PropertiesBuiltIn");
                ownerRef = dataRef;
                dataRef = data.ParentDataRef;
            }
            if (entries == null) return;

            string source = ownerRef != item.DataRef && _byDataRef.TryGetValue(ownerRef, out Entry owner)
                ? $"Built into item (inherited from {owner.DisplayName})" : "Built into item";

            // Seed 0 makes the server pick a time-based seed, so the roll isn't reproducible.
            Mwc random = seed != 0 ? new Mwc((uint)seed) : null;

            foreach (PrototypeData entry in entries)
            {
                float randomMult = random?.NextFloat() ?? 0f;

                PrototypeData prop = GetInheritedStruct(entry, "Prop");
                var (propName, forText, dataType) = DescribeProp(prop);
                string kind = RootDefaultsName(entry);

                string valueText;
                string details;
                if (kind == "PropertySetEntry")
                {
                    PrototypeData valueEval = GetInheritedStruct(entry, "Value");
                    if (dataType == "Asset")
                    {
                        ulong assetId = GetInheritedSimple(valueEval, "Value");
                        valueText = _assetIdToName.TryGetValue(assetId, out string assetName) ? assetName : "(asset)";
                    }
                    else
                    {
                        float? value = Eval(valueEval, itemLevel);
                        valueText = value == null ? "(formula)" : FormatNumber(dataType == "Integer" ? (int)value.Value : value.Value);
                    }
                    details = "Fixed value";
                }
                else
                {
                    float? min = entry != null && GetInheritedStruct(entry, "ValueMin") is PrototypeData minEval ? Eval(minEval, itemLevel) : 0f;
                    float? max = entry != null && GetInheritedStruct(entry, "ValueMax") is PrototypeData maxEval ? Eval(maxEval, itemLevel) : 0f;
                    bool rollAsInteger = GetInheritedSimple(entry, "RollAsInteger") != 0;

                    if (min == null || max == null)
                    {
                        valueText = "(formula)";
                        details = "Rolled within a range that isn't a simple formula";
                    }
                    else
                    {
                        string range = min == max ? FormatNumber(min.Value) : $"{FormatNumber(min.Value)} – {FormatNumber(max.Value)}";
                        details = $"Range at item level {itemLevel}: {range}";

                        if (random == null || (dataType != "Real" && dataType != "Integer"))
                            valueText = range;
                        else if (dataType == "Integer")
                            valueText = FormatNumber((int)TruncatedWithinRange(randomMult, min.Value, max.Value));
                        else
                            valueText = FormatNumber(rollAsInteger ? TruncatedWithinRange(randomMult, min.Value, max.Value) : (max.Value - min.Value) * randomMult + min.Value);
                    }
                }

                result.Add(new BuiltInStat
                {
                    Kind = "Property",
                    Name = propName,
                    For = forText,
                    Value = valueText,
                    Source = source,
                    Details = details,
                });
            }
        }

        // Item.GenerateTruncatedFloatWithinRange
        private static float TruncatedWithinRange(float randomMult, float min, float max)
        {
            float result = ((max - min + 1f) * randomMult) + min;
            if (result < min) result = min;
            else if (result > max) result = max;
            return MathF.Floor(result);
        }

        private static string FormatNumber(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        // A property mixin struct ("Prop" field) -> its property's display name, its params, and
        // the property's data type name from its Property/Info prototype ("Real", "Integer", ...).
        private (string Name, string For, string DataType) DescribeProp(PrototypeData prop)
        {
            if (prop == null) return ("(unknown property)", "-", null);

            List<(ulong BlueprintId, ulong FieldId, ulong Raw)> fields = new();
            HashSet<ulong> seen = new();
            for (PrototypeData data = prop; data != null; data = data.ParentDataRef != 0 ? GetPrototypeData(data.ParentDataRef) : null)
            {
                foreach (var (blueprintId, fieldIds) in data.Groups)
                    foreach (ulong fieldId in fieldIds)
                        if (seen.Add(fieldId) && data.Simple.TryGetValue(fieldId, out ulong raw))
                            fields.Add((blueprintId, fieldId, raw));
            }

            return DescribeMixin(RootDefaultsName(prop) ?? "(unknown property)", fields);
        }

        // A property mixin (by its blueprint/defaults file name, e.g. "EnduranceAddBonusProp")
        // plus whatever Param0..Param3 fields were set -> the property's display name, its
        // params as readable text, and the property's data type ("Real", "Integer", ...).
        private (string Name, string For, string DataType) DescribeMixin(string mixinName, IEnumerable<(ulong BlueprintId, ulong FieldId, ulong Raw)> fields)
        {
            Entry info = FindPropertyInfo(mixinName);

            string dataType = null;
            if (info != null && GetPrototypeData(info.DataRef) is PrototypeData infoData)
                _assetIdToName.TryGetValue(GetInheritedSimple(infoData, "Type"), out dataType);

            List<(string FieldName, string Text)> parameters = new();
            foreach (var (blueprintId, fieldId, raw) in fields)
            {
                string fieldName = _gameData.GetFieldName(fieldId);
                if (fieldName == null || fieldName.StartsWith("Param") == false) continue;
                parameters.Add((fieldName, FormatRaw(_gameData.GetMemberType(blueprintId, fieldId), raw)));
            }

            string forText = parameters.Count > 0 ? string.Join(", ", parameters.OrderBy(p => p.FieldName).Select(p => p.Text)) : "-";
            return (info?.DisplayName ?? mixinName, forText, dataType);
        }

        // Mixin blueprints are named either like the property or with a "Prop" suffix
        // (PropertyInfoTable does the same matching).
        private Entry FindPropertyInfo(string mixinName)
        {
            foreach (string candidate in new[] { mixinName, mixinName.EndsWith("Prop") ? mixinName[..^4] : null })
            {
                if (candidate != null && _byPath.TryGetValue($"Property/Info/{candidate}", out Entry info))
                    return info;
            }
            return null;
        }

        private string FormatRaw(byte baseType, ulong raw) => baseType switch
        {
            (byte)'P' => raw == 0 ? "(none)" : _byDataRef.TryGetValue(raw, out Entry p) ? p.DisplayName : $"0x{raw:X}",
            (byte)'A' => raw == 0 ? "(none)" : _assetIdToName.TryGetValue(raw, out string asset) ? asset : $"0x{raw:X}",
            (byte)'D' => FormatNumber((float)BitConverter.Int64BitsToDouble(unchecked((long)raw))),
            (byte)'B' => raw != 0 ? "true" : "false",
            (byte)'C' => "(curve)",
            _ => unchecked((long)raw).ToString(CultureInfo.InvariantCulture),
        };

        // var1/var2 are the eval context vars OnAffixAdded sets for power modifier affixes
        // (Var1 = item level, Var2 = the scoped power's unlock level); null when not known.
        private float? Eval(PrototypeData eval, int itemLevel, int depth = 0, int? var1 = null, int? var2 = null)
        {
            if (eval == null || depth > 16) return null;

            float? Arg(string field) => Eval(GetInheritedStruct(eval, field), itemLevel, depth + 1, var1, var2);

            string kind = RootDefaultsName(eval);
            switch (kind)
            {
                case "LoadInt":
                    return unchecked((long)GetInheritedSimple(eval, "Value"));
                case "LoadFloat":
                    return (float)BitConverter.Int64BitsToDouble(unchecked((long)GetInheritedSimple(eval, "Value")));
                case "LoadBool":
                    return GetInheritedSimple(eval, "Value") != 0 ? 1 : 0;
                case "Mult":
                case "Div":
                case "Add":
                case "Sub":
                case "Max":
                case "Min":
                case "Equals":
                case "GreaterThan":
                case "LessThan":
                {
                    float? a = Arg("Arg1");
                    float? b = Arg("Arg2");
                    if (a == null || b == null) return null;
                    return kind switch
                    {
                        "Mult" => a * b,
                        "Div" => b == 0 ? null : a / b,
                        "Add" => a + b,
                        "Sub" => a - b,
                        "Max" => MathF.Max(a.Value, b.Value),
                        "Min" => MathF.Min(a.Value, b.Value),
                        "Equals" => MathF.Abs(a.Value - b.Value) <= (float)BitConverter.Int64BitsToDouble(unchecked((long)GetInheritedSimple(eval, "Epsilon"))) ? 1 : 0,
                        "GreaterThan" => a > b ? 1 : 0,
                        _ => a < b ? 1 : 0,
                    };
                }
                case "IfElse":
                {
                    float? condition = Arg("Conditional");
                    if (condition == null) return null;
                    return condition != 0 ? Arg("EvalIf") : (GetInheritedStruct(eval, "EvalElse") != null ? Arg("EvalElse") : 0);
                }
                case "LoadProp":
                    // Item and affix evals read the item's own level.
                    return DescribeProp(GetInheritedStruct(eval, "Prop")).Name == "ItemLevel" ? itemLevel : null;
                case "LoadContextInt":
                {
                    _assetIdToName.TryGetValue(GetInheritedSimple(eval, "Context"), out string context);
                    return context switch { "Var1" => var1, "Var2" => var2, _ => null };
                }
            }

            if (kind != null && kind.StartsWith("LoadCurve"))
            {
                ulong curveId = GetInheritedSimple(eval, "Curve");
                float? index = Arg("Index");
                if (curveId == 0 || index == null) return null;
                return _gameData.GetCurveValue(curveId, (int)index.Value);
            }

            return null;
        }

        // The .defaults file a prototype (or embedded struct) ultimately derives from, by file
        // name - this is what identifies its type ("Mult", "LoadCurveItemLevelValueCurve",
        // "PropertyPickInRangeEntry", "EnduranceAddBonusProp", ...).
        private string RootDefaultsName(PrototypeData data)
        {
            for (int depth = 0; data != null && depth < 32; depth++)
            {
                if (data.ParentDataRef == 0 || _byDataRef.TryGetValue(data.ParentDataRef, out Entry parent) == false)
                    return null;
                if (parent.Path.EndsWith(".defaults"))
                    return System.IO.Path.GetFileNameWithoutExtension(parent.Path);
                data = GetPrototypeData(parent.DataRef);
            }
            return null;
        }

        // The server class a prototype is loaded as, from its blueprint's runtime binding - e.g.
        // "AffixPowerModifierPrototype". This is what `is SomethingPrototype` tests on the server,
        // and unlike file inheritance it's right for root .defaults with no parent.
        public string GetRuntimeClass(ulong guid) =>
            _gameData != null && _byGuid.TryGetValue(guid, out Entry entry) ? _gameData.GetBlueprintBinding(entry.BlueprintId) : null;

        // Subclasses of a server prototype class share its name as a suffix
        // (MissilePowerPrototype is a PowerPrototype).
        private bool IsClass(ulong guid, string className) => GetRuntimeClass(guid)?.EndsWith(className) == true;

        private PrototypeData GetInheritedStruct(PrototypeData data, string fieldName)
        {
            for (int depth = 0; data != null && depth < 32; depth++)
            {
                foreach (var pair in data.SimpleStructs)
                    if (_gameData.GetFieldName(pair.Key) == fieldName) return pair.Value;
                data = data.ParentDataRef != 0 ? GetPrototypeData(data.ParentDataRef) : null;
            }
            return null;
        }

        // Port of MHServerEmu.Core.System.Random.RandMwc + Rand.GetFloat (what GRandom.NextFloat uses).
        private sealed class Mwc
        {
            private ulong _state;
            public Mwc(uint seed) { _state = (ulong)666 << 32 | seed; }

            private uint Next()
            {
                _state = 698769069UL * (_state & 0xffffffff) + (_state >> 32);
                return (uint)_state;
            }

            public float NextFloat() => BitConverter.UInt32BitsToSingle(Next() & 0x7fffff | 0x3f800000) - 1.0f;
        }
    }
}
