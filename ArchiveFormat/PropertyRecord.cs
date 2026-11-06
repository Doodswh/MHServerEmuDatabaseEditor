using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MHDBEditor.ArchiveFormat
{
    public class PropertyParamRecord
    {
        public PropertyParamType Type { get; set; }
        public int IntValue { get; set; }
        public ulong GuidValue { get; set; }
    }

    // One decoded property entry from a persisted PropertyCollection, matching the exact wire
    // layout of MHServerEmu.Games.Properties.PropertyStore.Serialize(). PrototypeGuid is the
    // property's own PrototypeGuid (GameDatabase.GetPrototypeGuid(propertyInfo.PrototypeDataRef)
    // on the server) - the same guid->name list used for EntityProtoGuid/InventoryProtoGuid can
    // resolve it to a readable name if you've loaded one.
    public class PropertyRecord
    {
        public byte Version { get; set; }
        public ulong PrototypeGuid { get; set; }
        public List<PropertyParamRecord> Params { get; set; } = new();
        public PropertyDataType DataType { get; set; }

        public long ValueLong { get; set; }
        public float ValueFloat { get; set; }
        public ulong ValueGuid { get; set; }

        public string PrototypeGuidHex => $"0x{PrototypeGuid:X}";

        public string ParamsDisplay => Params.Count == 0
            ? "-"
            : string.Join(", ", Params.Select(p => p.Type switch
            {
                PropertyParamType.Integer => p.IntValue.ToString(),
                PropertyParamType.Asset => $"asset:0x{p.GuidValue:X}",
                PropertyParamType.Prototype => $"proto:0x{p.GuidValue:X}",
                _ => "?",
            }));

        // Bound as the editable grid cell: get formats for display, set parses back in.
        public string ValueText
        {
            get => DataType switch
            {
                PropertyDataType.Boolean => (ValueLong != 0).ToString(),
                PropertyDataType.Real => ValueFloat.ToString(CultureInfo.InvariantCulture),
                PropertyDataType.Integer => ValueLong.ToString(),
                PropertyDataType.EntityId => $"0x{ValueLong:X}",
                PropertyDataType.Time => FormatTime(ValueLong),
                PropertyDataType.Guid => $"0x{ValueLong:X}",
                PropertyDataType.RegionId => $"0x{ValueLong:X}",
                PropertyDataType.Int21Vector3 => $"0x{ValueLong:X} (packed vector - edit unsupported)",
                PropertyDataType.Prototype => $"0x{ValueGuid:X}",
                PropertyDataType.Asset => $"0x{ValueGuid:X}",
                _ => "?",
            };
            set => SetValueFromText(value);
        }

        private void SetValueFromText(string text)
        {
            text = (text ?? string.Empty).Trim();

            switch (DataType)
            {
                case PropertyDataType.Boolean:
                    ValueLong = IsTruthy(text) ? 1 : 0;
                    break;

                case PropertyDataType.Real:
                    ValueFloat = float.Parse(text.TrimEnd('f', 'F'), CultureInfo.InvariantCulture);
                    break;

                case PropertyDataType.Integer:
                    ValueLong = ParseHexOrDec(text);
                    break;

                case PropertyDataType.Time:
                    ValueLong = ParseTime(text);
                    break;

                case PropertyDataType.EntityId:
                case PropertyDataType.Guid:
                case PropertyDataType.RegionId:
                    ValueLong = ParseHexOrDec(text);
                    break;

                case PropertyDataType.Prototype:
                case PropertyDataType.Asset:
                    ValueGuid = unchecked((ulong)ParseHexOrDec(text));
                    break;

                case PropertyDataType.Int21Vector3:
                    throw new NotSupportedException("Editing packed Int21Vector3 properties isn't supported yet.");

                default:
                    throw new NotSupportedException($"Editing property data type {DataType} isn't supported.");
            }
        }

        // Time properties hold milliseconds. Large values are Unix timestamps (e.g. loot
        // cooldown start times) and read best as a date; small ones are durations. Shown with
        // milliseconds so editing and saving round-trips the exact stored value.
        private const long UnixMsYear2000 = 946684800000;
        private const long UnixMsYear2100 = 4102444800000;
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private static string FormatTime(long ms)
        {
            if (ms >= UnixMsYear2000 && ms < UnixMsYear2100)
            {
                // The hour repeated when DST ends reads the same twice, so add the UTC offset
                // there - otherwise saving would move the time an hour.
                DateTimeOffset local = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                bool ambiguous = TimeZoneInfo.Local.IsAmbiguousTime(local);
                return local.ToString(ambiguous ? TimeFormat + " zzz" : TimeFormat, CultureInfo.InvariantCulture);
            }
            return $"{ms} ms";
        }

        private static long ParseTime(string text)
        {
            string number = text.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? text[..^2].Trim() : text;
            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                return ms;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset date))
                return date.ToUnixTimeMilliseconds();
            throw new FormatException($"\"{text}\" isn't a date (e.g. 2026-03-24 14:30:00) or a number of milliseconds.");
        }

        private static bool IsTruthy(string text) => text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);

        private static long ParseHexOrDec(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return unchecked((long)Convert.ToUInt64(text, 16));
            return long.Parse(text);
        }
    }
}
