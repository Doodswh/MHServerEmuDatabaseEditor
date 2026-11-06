using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MHDBEditor.Utility
{
    // Achievement names / thresholds from the server's Data/Game/Achievements folder, merged the
    // way AchievementDatabase.Initialize() does it: AchievementInfoMap.json first, then every other
    // AchievementInfoMap*.json in alphabetical order, later files overriding earlier ones by Id.
    // Strings come from AchievementStringMap*.json the same way (main file first).
    public class AchievementCatalog
    {
        public class Info
        {
            public uint Id { get; init; }
            public bool Enabled { get; init; }
            public uint ParentId { get; init; }
            public string Name { get; init; }
            public string Category { get; init; }
            public string SubCategory { get; init; }
            public uint Threshold { get; init; }
            public uint Score { get; init; }
        }

        private readonly Dictionary<uint, Info> _infos = new();
        public string Directory { get; private set; }
        public int Count => _infos.Count;
        public IEnumerable<Info> All => _infos.Values;

        public bool TryGet(uint id, out Info info) => _infos.TryGetValue(id, out info);

        // Data/Game/Achievements next to the loaded Calligraphy.sip. Returns null if it isn't there.
        public static AchievementCatalog TryLoadNextTo(string sipPath)
        {
            if (string.IsNullOrEmpty(sipPath)) return null;
            string directory = Path.Combine(Path.GetDirectoryName(sipPath) ?? "", "Achievements");
            if (File.Exists(Path.Combine(directory, "AchievementInfoMap.json")) == false) return null;

            AchievementCatalog catalog = new() { Directory = directory };
            Dictionary<ulong, string> strings = new();
            foreach (string file in OrderedFiles(directory, "AchievementStringMap"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (JsonProperty entry in doc.RootElement.EnumerateObject())
                    {
                        if (ulong.TryParse(entry.Name, out ulong id) && entry.Value.TryGetProperty("en_us", out JsonElement text))
                            strings[id] = text.GetString();
                    }
                }
                catch (Exception) { /* a broken patch file is skipped, like the server does */ }
            }

            string Text(JsonElement element, string name) =>
                element.TryGetProperty(name, out JsonElement value) && value.TryGetUInt64(out ulong id) && id != 0 && strings.TryGetValue(id, out string s) ? s : null;
            uint UInt(JsonElement element, string name) =>
                element.TryGetProperty(name, out JsonElement value) && value.TryGetUInt32(out uint v) ? v : 0;

            foreach (string file in OrderedFiles(directory, "AchievementInfoMap"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (JsonElement e in doc.RootElement.EnumerateArray())
                    {
                        uint id = UInt(e, "Id");
                        catalog._infos[id] = new Info
                        {
                            Id = id,
                            Enabled = e.TryGetProperty("Enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True,
                            ParentId = UInt(e, "ParentId"),
                            Name = Text(e, "Name"),
                            Category = Text(e, "CategoryStr"),
                            SubCategory = Text(e, "SubCategoryStr"),
                            Threshold = UInt(e, "Threshold"),
                            Score = UInt(e, "Score"),
                        };
                    }
                }
                catch (Exception) { }
            }
            return catalog;
        }

        // Display name; child achievements without their own name fall back to the parent's.
        public string NameOf(uint id)
        {
            for (int depth = 0; depth < 5 && _infos.TryGetValue(id, out Info info); depth++)
            {
                if (string.IsNullOrEmpty(info.Name) == false)
                    return depth == 0 ? info.Name : $"{info.Name} (step)";
                id = info.ParentId;
            }
            return null;
        }

        private static IEnumerable<string> OrderedFiles(string directory, string prefix)
        {
            string main = Path.Combine(directory, prefix + ".json");
            List<string> files = System.IO.Directory.GetFiles(directory, prefix + "*.json").OrderBy(f => f).ToList();   // same ordering as the server
            files.RemoveAll(f => string.Equals(f, main, StringComparison.OrdinalIgnoreCase));
            if (File.Exists(main)) files.Insert(0, main);
            return files;
        }
    }
}
