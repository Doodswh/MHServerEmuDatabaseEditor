using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MHDBEditor.Utility
{
    // Turns the raw PrototypeGuid / AssetGuid numbers stored in the DB into readable names, and
    // back again so edits can be typed as names instead of hex.
    //
    // The DB stores PrototypeGuids (stable ids baked into the game data), not the DataRef
    // hashes. The best source is the game's own Calligraphy.sip (see CalligraphyReader), which
    // also covers asset guids. A text list works too: either "guid<TAB or ,>name" lines, or the
    // server's Prototype.directory.tsv dump (dataRef, guid, blueprint, flags, path).
    //
    // Display names are the prototype's file name ("CharacterLevel"). ~3500 file names are
    // shared by more than one prototype, so those get their parent folder in front
    // ("Mark1Defense/HealthMaxIncreaseMark1") until unique - so every display name maps back
    // to exactly one guid.
    public partial class PrototypeResolver
    {
        public class Entry
        {
            public ulong DataRef { get; init; }
            public ulong Guid { get; init; }
            public ulong BlueprintId { get; init; }
            public string Path { get; init; }
            public string DisplayName { get; set; }
            public bool IsAbstract { get; init; }

            public override string ToString() => DisplayName;
        }

        private readonly Dictionary<ulong, Entry> _byGuid = new();
        private readonly Dictionary<ulong, Entry> _byDataRef = new();
        private readonly Dictionary<string, Entry> _byDisplayName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Entry>> _byShortName = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<ulong, string> _assetGuidToName = new();
        private readonly Dictionary<string, ulong> _assetNameToGuid = new(StringComparer.OrdinalIgnoreCase);

        // Only set when loaded from Calligraphy.sip - needed to read built-in prototype data.
        private CalligraphyReader _gameData;
        private readonly Dictionary<ulong, PrototypeData> _prototypeDataCache = new();

        public int Count => _byGuid.Count;
        public bool HasGameData => _gameData != null;
        public int AssetCount => _assetGuidToName.Count;
        public string LoadedFrom { get; private set; }

        public event Action Loaded;

        public void LoadFromFile(string path)
        {
            if (path.EndsWith(".sip", StringComparison.OrdinalIgnoreCase))
            {
                LoadFromGameData(path);
                return;
            }

            List<Entry> entries = new();
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] columns = line.Split('\t');
                if (columns.Length >= 5)
                {
                    // Prototype.directory.tsv: dataRef, guid, blueprint, flags, path
                    if (ulong.TryParse(columns[1], out ulong tsvGuid))
                        entries.Add(new Entry { Guid = tsvGuid, Path = columns[4].Trim(), IsAbstract = columns[3].Contains("Abstract") });
                    continue;
                }

                int splitIndex = line.IndexOfAny(new[] { '\t', ',' });
                if (splitIndex <= 0) continue;

                if (ulong.TryParse(line[..splitIndex].Trim(), out ulong guid))
                    entries.Add(new Entry { Guid = guid, Path = line[(splitIndex + 1)..].Trim() });
            }

            _gameData = null;
            SetEntries(entries, Array.Empty<CalligraphyReader.AssetEntry>(), path);
        }

        public void LoadFromGameData(string sipPath)
        {
            CalligraphyReader gameData = new(sipPath);
            List<Entry> entries = gameData.ReadPrototypeDirectory()
                .Select(p => new Entry { DataRef = p.DataRef, Guid = p.Guid, BlueprintId = p.BlueprintId, Path = p.Path, IsAbstract = p.IsAbstract }).ToList();
            List<CalligraphyReader.AssetEntry> assets = gameData.ReadAssets();

            _gameData = gameData;
            SetEntries(entries, assets, sipPath);
        }

        private void SetEntries(List<Entry> entries, IEnumerable<CalligraphyReader.AssetEntry> assets, string source)
        {
            _byGuid.Clear();
            _byDataRef.Clear();
            _prototypeDataCache.Clear();
            _startLocations = null;
            _byDisplayName.Clear();
            _byPath.Clear();
            _byShortName.Clear();
            _assetGuidToName.Clear();
            _assetNameToGuid.Clear();
            _assetIdToName.Clear();

            foreach (Entry entry in entries)
            {
                if (entry.Guid == 0 || _byGuid.TryAdd(entry.Guid, entry) == false)
                    continue;
                if (entry.DataRef != 0)
                    _byDataRef.TryAdd(entry.DataRef, entry);

                _byPath.TryAdd(entry.Path, entry);
                _byPath.TryAdd(StripExtension(entry.Path), entry);

                string shortName = ShortName(entry.Path);
                if (_byShortName.TryGetValue(shortName, out List<Entry> list) == false)
                    _byShortName[shortName] = list = new List<Entry>();
                list.Add(entry);
            }

            // Prefix colliding names with as many parent folders as it takes to make them unique.
            foreach (List<Entry> group in _byShortName.Values)
            {
                if (group.Count == 1)
                {
                    group[0].DisplayName = ShortName(group[0].Path);
                    continue;
                }

                // A property keeps the plain name when it shares one with some mixin/other
                // prototype - property names are what people read most in this tool.
                List<Entry> rest = group;
                List<Entry> properties = group.Where(e => e.Path.StartsWith("Property/Info/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (properties.Count == 1)
                {
                    properties[0].DisplayName = ShortName(properties[0].Path);
                    rest = group.Where(e => e != properties[0]).ToList();
                }

                for (int depth = 2; ; depth++)
                {
                    foreach (Entry entry in rest)
                        entry.DisplayName = TrailingSegments(entry.Path, depth);

                    bool allUnique = rest.Select(e => e.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == rest.Count;
                    if (allUnique)
                        break;

                    if (rest.All(e => e.DisplayName == StripExtension(e.Path)))
                    {
                        // A few paths exist as both .defaults and .prototype - only the extension differs.
                        foreach (var sameName in rest.GroupBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                            foreach (Entry entry in sameName)
                                entry.DisplayName = entry.Path;
                        break;
                    }
                }
            }

            foreach (Entry entry in _byGuid.Values)
                _byDisplayName.TryAdd(entry.DisplayName, entry);

            foreach (CalligraphyReader.AssetEntry asset in assets)
            {
                if (asset.Id != 0) _assetIdToName.TryAdd(asset.Id, asset.Name);
                if (asset.Guid == 0) continue;
                _assetGuidToName.TryAdd(asset.Guid, asset.Name);
                _assetNameToGuid.TryAdd(asset.Name, asset.Guid);
            }

            LoadedFrom = source;
            Loaded?.Invoke();
        }

        public bool TryResolve(long protoGuid, out string name)
        {
            if (_byGuid.TryGetValue(unchecked((ulong)protoGuid), out Entry entry))
            {
                name = entry.DisplayName;
                return true;
            }
            name = null;
            return false;
        }

        public bool TryGetEntry(ulong guid, out Entry entry) => _byGuid.TryGetValue(guid, out entry);
        public bool TryGetEntryByDataRef(ulong dataRef, out Entry entry) => _byDataRef.TryGetValue(dataRef, out entry);

        public bool TryResolveAsset(ulong assetGuid, out string name) => _assetGuidToName.TryGetValue(assetGuid, out name);

        // Readable text for a prototype guid: its name if known, hex otherwise. 0 is "(none)".
        public string Format(ulong guid)
        {
            if (guid == 0) return "(none)";
            return _byGuid.TryGetValue(guid, out Entry entry) ? entry.DisplayName : $"0x{guid:X}";
        }

        public string FormatAsset(ulong guid)
        {
            if (guid == 0) return "(none)";
            return _assetGuidToName.TryGetValue(guid, out string name) ? name : $"0x{guid:X}";
        }

        // Name lookup used by code (e.g. "CharacterLevel"): display name, then full path, then a
        // short name - preferring a property prototype when a short name is shared.
        public bool TryResolveByName(string name, out ulong guid)
        {
            guid = 0;
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();

            if (_byDisplayName.TryGetValue(name, out Entry entry) || _byPath.TryGetValue(StripExtension(name), out entry))
            {
                guid = entry.Guid;
                return true;
            }

            if (_byShortName.TryGetValue(name, out List<Entry> group))
            {
                entry = group.Count == 1 ? group[0] : group.FirstOrDefault(e => e.Path.StartsWith("Property/Info/", StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    guid = entry.Guid;
                    return true;
                }
            }

            return false;
        }

        // Parses whatever the user typed into a prototype guid: a name, a full path, hex (0x...),
        // decimal, "(none)", or a previously displayed "Name (0x...)".
        public ulong ParsePrototype(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0 || text.Equals("(none)", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (TryParseNumber(text, out ulong number))
                return number;
            if (TryResolveByName(text, out ulong guid))
                return guid;

            if (_byShortName.TryGetValue(text, out List<Entry> group) && group.Count > 1)
                throw new FormatException($"\"{text}\" matches {group.Count} prototypes - use one of: " +
                    string.Join(", ", group.Take(5).Select(e => e.DisplayName)) + (group.Count > 5 ? ", ..." : ""));

            throw new FormatException($"\"{text}\" isn't a known prototype name or a guid.");
        }

        public ulong ParseAsset(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0 || text.Equals("(none)", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (TryParseNumber(text, out ulong number))
                return number;
            if (_assetNameToGuid.TryGetValue(text, out ulong guid))
                return guid;
            throw new FormatException($"\"{text}\" isn't a known asset name or a guid.");
        }

        // All prototypes under a path prefix (e.g. "Entity/Items/Affixes/"), sorted by name.
        public IEnumerable<Entry> Find(string pathPrefix = null, bool includeAbstract = true)
        {
            return _byGuid.Values
                .Where(e => (pathPrefix == null || e.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    && (includeAbstract || e.IsAbstract == false))
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase);
        }

        public class BuiltInAffix
        {
            public ulong AffixGuid { get; init; }
            public ulong PowerGuid { get; init; }
            public ulong AvatarGuid { get; init; }
            public string Source { get; init; }
        }

        // Affixes an item gets from the game data rather than from its saved ItemSpec - the
        // same two sources as the server's ItemPrototype.GetBuiltInAffixEntries(): the item
        // prototype's AffixesBuiltIn, then its rarity's AffixesBuiltIn. These are never stored
        // in the DB (the server re-applies them on load), so they're read-only here.
        // Returns null when built-in data isn't available (no Calligraphy.sip loaded).
        public List<BuiltInAffix> GetBuiltInAffixes(ulong itemGuid, ulong rarityGuid)
        {
            if (_gameData == null) return null;

            List<BuiltInAffix> result = new();
            AddBuiltInAffixes(itemGuid, "Built into item", result);
            AddBuiltInAffixes(rarityGuid, "Built into rarity", result);
            return result;
        }

        private void AddBuiltInAffixes(ulong guid, string source, List<BuiltInAffix> result)
        {
            if (_byGuid.TryGetValue(guid, out Entry entry) == false) return;

            // List fields replace the parent's list entirely, so the nearest prototype in the
            // parent chain that sets AffixesBuiltIn is the one that counts.
            List<PrototypeData> entries = null;
            ulong ownerRef = 0;
            for (ulong dataRef = entry.DataRef; dataRef != 0 && entries == null; )
            {
                PrototypeData data = GetPrototypeData(dataRef);
                if (data == null) break;
                entries = FindStructList(data, "AffixesBuiltIn");
                ownerRef = dataRef;
                dataRef = data.ParentDataRef;
            }
            if (entries == null) return;

            string from = ownerRef != entry.DataRef && _byDataRef.TryGetValue(ownerRef, out Entry owner) ? $"{source} (inherited from {owner.DisplayName})" : source;
            foreach (PrototypeData affixEntry in entries)
            {
                ulong affixRef = GetInheritedSimple(affixEntry, "Affix");
                if (affixRef == 0) continue;
                result.Add(new BuiltInAffix
                {
                    AffixGuid = GuidFromDataRef(affixRef),
                    PowerGuid = GuidFromDataRef(GetInheritedSimple(affixEntry, "Power")),
                    AvatarGuid = GuidFromDataRef(GetInheritedSimple(affixEntry, "Avatar")),
                    Source = from,
                });
            }
        }

        private ulong GuidFromDataRef(ulong dataRef) => dataRef != 0 && _byDataRef.TryGetValue(dataRef, out Entry entry) ? entry.Guid : 0;

        private PrototypeData GetPrototypeData(ulong dataRef)
        {
            lock (_prototypeDataCache)
                if (_prototypeDataCache.TryGetValue(dataRef, out PrototypeData cached)) return cached;
            if (_byDataRef.TryGetValue(dataRef, out Entry entry) == false) return null;

            string file = $"Calligraphy/{entry.Path}";
            PrototypeData data = _gameData.HasFile(file) ? PrototypeData.ParseFile(_gameData.LoadFile(file)) : null;
            lock (_prototypeDataCache) _prototypeDataCache[dataRef] = data;
            return data;
        }

        private List<PrototypeData> FindStructList(PrototypeData data, string fieldName)
        {
            foreach (var pair in data.StructLists)
                if (_gameData.GetFieldName(pair.Key) == fieldName) return pair.Value;
            return null;
        }

        // An embedded struct only stores the fields it overrides; the rest come from its parent
        // (usually the struct type's .defaults).
        private ulong GetInheritedSimple(PrototypeData data, string fieldName)
        {
            for (int depth = 0; data != null && depth < 32; depth++)
            {
                foreach (var pair in data.Simple)
                    if (_gameData.GetFieldName(pair.Key) == fieldName) return pair.Value;
                data = data.ParentDataRef != 0 ? GetPrototypeData(data.ParentDataRef) : null;
            }
            return 0;
        }

        public IEnumerable<KeyValuePair<ulong, string>> GetAll() => _byGuid.Select(kvp => new KeyValuePair<ulong, string>(kvp.Key, kvp.Value.DisplayName));

        private static bool TryParseNumber(string text, out ulong value)
        {
            // Accept "Name (0x1234)" as shown in older displays - the guid in parens wins.
            int open = text.LastIndexOf("(0x", StringComparison.OrdinalIgnoreCase);
            if (open >= 0 && text.EndsWith(")"))
                text = text[(open + 1)..^1];

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static string StripExtension(string path)
        {
            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            return dot > slash ? path[..dot] : path;
        }

        private static string ShortName(string path) => TrailingSegments(path, 1);

        private static string TrailingSegments(string path, int count)
        {
            string[] segments = StripExtension(path).Split('/');
            return string.Join("/", segments.Skip(Math.Max(0, segments.Length - count)));
        }
    }
}
