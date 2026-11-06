using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;

namespace MHDBEditor.Utility
{
    // Reads data straight out of the game's Calligraphy.sip, so names (and built-in item data)
    // always match what the server is actually running with - no exported list to keep in sync.
    //
    // Formats are ported from the server source:
    //   PakFile.cs                  - "KAPG" v1 header, entry table, LZ4-compressed file data
    //   DataDirectory.cs            - Prototype / Type / Blueprint .directory records
    //   Calligraphy/AssetType.cs    - per-type asset lists (AssetId, AssetGuid, flags, name)
    //   Calligraphy/Blueprint.cs    - blueprint members (field id -> field name)
    //   BinaryReaderExtensions.cs   - FixedString16/32 = u16/i32 length + UTF-8 bytes
    public class CalligraphyReader
    {
        private const uint PakSignature = 1196441931;  // KAPG
        private const uint PakVersion = 1;

        public class PrototypeEntry
        {
            public ulong DataRef { get; init; }
            public ulong Guid { get; init; }
            public ulong BlueprintId { get; init; }
            public string Path { get; init; }
            public bool IsAbstract { get; init; }
        }

        public class AssetEntry
        {
            public ulong Id { get; init; }
            public ulong Guid { get; init; }
            public string Name { get; init; }
            public string TypeName { get; init; }
        }

        private readonly string _sipPath;
        private readonly Dictionary<string, (int Offset, int CompressedSize, int UncompressedSize)> _entries = new(StringComparer.OrdinalIgnoreCase);
        private long _dataStart;
        private Dictionary<ulong, string> _fieldNames;
        private Dictionary<ulong, string> _blueprintPaths;
        private Dictionary<ulong, string> _blueprintBindings;
        private Dictionary<(ulong BlueprintId, ulong FieldId), byte> _memberTypes;
        private Dictionary<ulong, string> _curvePaths;
        private readonly Dictionary<ulong, (int Min, float[] Values)> _curves = new();
        // Searches read game data on background threads, so the lazily filled lookups are locked.
        private readonly object _lock = new();

        private Dictionary<ulong, string> FieldNames()
        {
            lock (_lock) return _fieldNames ??= ReadFieldNames();
        }

        public string SipPath => _sipPath;

        public CalligraphyReader(string sipPath)
        {
            _sipPath = sipPath;

            using FileStream stream = File.OpenRead(sipPath);
            using BinaryReader reader = new(stream);

            if (reader.ReadUInt32() != PakSignature)
                throw new InvalidDataException($"{Path.GetFileName(sipPath)} isn't a game data package (bad signature).");
            if (reader.ReadUInt32() != PakVersion)
                throw new InvalidDataException($"{Path.GetFileName(sipPath)} has an unsupported package version.");

            int numEntries = reader.ReadInt32();
            for (int i = 0; i < numEntries; i++)
            {
                reader.ReadUInt64();    // FileHash
                string filePath = ReadFixedString32(reader);
                reader.ReadInt32();     // ModTime
                _entries[filePath] = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            }

            _dataStart = stream.Position;
        }

        public bool HasFile(string filePath) => _entries.ContainsKey(filePath);

        public IEnumerable<string> FilePaths => _entries.Keys;

        public byte[] LoadFile(string filePath)
        {
            if (_entries.TryGetValue(filePath, out var entry) == false)
                throw new InvalidDataException($"{filePath} not found in {Path.GetFileName(_sipPath)}.");

            using FileStream stream = File.OpenRead(_sipPath);
            stream.Position = _dataStart + entry.Offset;
            byte[] compressed = new byte[entry.CompressedSize];
            stream.ReadExactly(compressed);
            byte[] uncompressed = new byte[entry.UncompressedSize];
            LZ4Codec.Decode(compressed, uncompressed);
            return uncompressed;
        }

        private BinaryReader OpenFile(string filePath) => new(new MemoryStream(LoadFile(filePath)));

        public List<PrototypeEntry> ReadPrototypeDirectory()
        {
            using BinaryReader dir = OpenFile("Calligraphy/Prototype.directory");
            dir.ReadUInt32();   // CalligraphyHeader: 3-byte magic + version byte
            int recordCount = dir.ReadInt32();
            List<PrototypeEntry> prototypes = new(recordCount);
            for (int i = 0; i < recordCount; i++)
            {
                ulong dataRef = dir.ReadUInt64();
                ulong guid = dir.ReadUInt64();
                ulong blueprintId = dir.ReadUInt64();
                byte flags = dir.ReadByte();
                string path = ReadFixedString16(dir).Replace('\\', '/');
                prototypes.Add(new PrototypeEntry { DataRef = dataRef, Guid = guid, BlueprintId = blueprintId, Path = path, IsAbstract = (flags & 1) != 0 });
            }
            return prototypes;
        }

        public List<AssetEntry> ReadAssets()
        {
            List<string> typeFiles = new();
            using (BinaryReader dir = OpenFile("Calligraphy/Type.directory"))
            {
                dir.ReadUInt32();
                int recordCount = dir.ReadInt32();
                for (int i = 0; i < recordCount; i++)
                {
                    dir.ReadUInt64();   // AssetTypeId
                    dir.ReadUInt64();   // AssetTypeGuid
                    dir.ReadByte();     // flags
                    typeFiles.Add(ReadFixedString16(dir).Replace('\\', '/'));
                }
            }

            List<AssetEntry> assets = new();
            foreach (string typeFile in typeFiles)
            {
                string typeName = Path.GetFileNameWithoutExtension(typeFile);
                using BinaryReader type = OpenFile($"Calligraphy/{typeFile}");
                type.ReadUInt32();
                int assetCount = type.ReadUInt16();
                for (int i = 0; i < assetCount; i++)
                {
                    ulong id = type.ReadUInt64();
                    ulong guid = type.ReadUInt64();
                    type.ReadByte();    // flags
                    string name = ReadFixedString16(type);
                    assets.Add(new AssetEntry { Id = id, Guid = guid, Name = name, TypeName = typeName });
                }
            }
            return assets;
        }

        // Field id (StringId) -> field name, across every blueprint. Loaded on first use since
        // only the built-in item data lookups need it.
        public string GetFieldName(ulong fieldId)
        {
            return FieldNames().TryGetValue(fieldId, out string name) ? name : null;
        }

        // Blueprint id -> blueprint path (e.g. "Property/Info/HealthBonus.blueprint" for a
        // property mixin group).
        public string GetBlueprintPath(ulong blueprintId)
        {
            FieldNames();
            return _blueprintPaths.TryGetValue(blueprintId, out string path) ? path : null;
        }

        // The C# prototype class a blueprint binds to ("AffixPowerModifierPrototype",
        // "AvatarPrototype", ...) - what the server's `is SomethingPrototype` checks test.
        public string GetBlueprintBinding(ulong blueprintId)
        {
            FieldNames();
            return _blueprintBindings.TryGetValue(blueprintId, out string binding) ? binding : null;
        }

        // Calligraphy base type ('A'sset, 'P'rototype, 'L'ong, 'D'ouble, ...) of a blueprint
        // member, 0 if unknown. Needed to tell what kind of value a property param holds.
        public byte GetMemberType(ulong blueprintId, ulong fieldId)
        {
            FieldNames();
            return _memberTypes.TryGetValue((blueprintId, fieldId), out byte type) ? type : (byte)0;
        }

        // Curve value at an index, clamped to the curve's range like Curve.GetAt() on the server.
        public float? GetCurveValue(ulong curveId, int index)
        {
            (int Min, float[] Values) curve;
            lock (_lock)
            if (_curves.TryGetValue(curveId, out curve) == false)
            {
                _curvePaths ??= ReadCurvePaths();
                if (_curvePaths.TryGetValue(curveId, out string path) == false || HasFile($"Calligraphy/{path}") == false)
                    return null;

                using BinaryReader reader = OpenFile($"Calligraphy/{path}");
                reader.ReadUInt32();    // CalligraphyHeader
                int min = reader.ReadInt32();
                int max = reader.ReadInt32();
                float[] values = new float[max - min + 1];
                for (int i = 0; i < values.Length; i++)
                    values[i] = (float)reader.ReadDouble();
                _curves[curveId] = curve = (min, values);
            }

            int position = Math.Clamp(index - curve.Min, 0, curve.Values.Length - 1);
            return curve.Values[position];
        }

        private Dictionary<ulong, string> ReadCurvePaths()
        {
            Dictionary<ulong, string> paths = new();
            using BinaryReader dir = OpenFile("Calligraphy/Curve.directory");
            dir.ReadUInt32();
            int recordCount = dir.ReadInt32();
            for (int i = 0; i < recordCount; i++)
            {
                ulong curveId = dir.ReadUInt64();
                dir.ReadUInt64();   // CurveGuid
                dir.ReadByte();     // flags
                paths[curveId] = ReadFixedString16(dir).Replace('\\', '/');
            }
            return paths;
        }

        private Dictionary<ulong, string> ReadFieldNames()
        {
            _blueprintPaths = new();
            _blueprintBindings = new();
            _memberTypes = new();
            List<(ulong Id, string Path)> blueprintFiles = new();
            using (BinaryReader dir = OpenFile("Calligraphy/Blueprint.directory"))
            {
                dir.ReadUInt32();
                int recordCount = dir.ReadInt32();
                for (int i = 0; i < recordCount; i++)
                {
                    ulong blueprintId = dir.ReadUInt64();
                    dir.ReadUInt64();   // BlueprintGuid
                    dir.ReadByte();     // flags
                    string blueprintPath = ReadFixedString16(dir).Replace('\\', '/');
                    _blueprintPaths[blueprintId] = blueprintPath;
                    blueprintFiles.Add((blueprintId, blueprintPath));
                }
            }

            Dictionary<ulong, string> names = new();
            foreach (var (blueprintId, blueprintFile) in blueprintFiles)
            {
                using BinaryReader bp = OpenFile($"Calligraphy/{blueprintFile}");
                bp.ReadUInt32();                // CalligraphyHeader
                _blueprintBindings[blueprintId] = ReadFixedString16(bp);
                bp.ReadUInt64();                // DefaultPrototypeId
                bp.ReadBytes(bp.ReadInt16() * 9);   // Parents (BlueprintId + copy count)
                bp.ReadBytes(bp.ReadInt16() * 9);   // ContributingBlueprints

                short numMembers = bp.ReadInt16();
                for (int i = 0; i < numMembers; i++)
                {
                    ulong fieldId = bp.ReadUInt64();
                    string fieldName = ReadFixedString16(bp);
                    byte baseType = bp.ReadByte();
                    bp.ReadByte();              // structure type
                    if (baseType is (byte)'A' or (byte)'C' or (byte)'P' or (byte)'R')
                        bp.ReadUInt64();        // subtype
                    names.TryAdd(fieldId, fieldName);
                    _memberTypes[(blueprintId, fieldId)] = baseType;
                }
            }
            return names;
        }

        private static string ReadFixedString16(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
        private static string ReadFixedString32(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
    }
}
