using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MHDBEditor.ArchiveFormat
{
    // Everything Player.Serialize() writes to a Database archive after the property block:
    //
    //   MissionManager            ISerialize: avatar ref guid, missions, legendary blacklist
    //   _avatarProperties         ISerialize: a property block (same format as the entity's)
    //   hasCommunityData          bool
    //   _community                ISerialize: circle names + members (name, db id, circles)
    //   AdminDifficultyOverride   ulong raw DataRef, archive version >= 10 (AddedAdminDifficulty)
    //   unkBool                   bool, always false
    //   _unlockedInventoryList    List<PrototypeId> as guids
    //   GameplayOptions           ISerialize: chat filters (guid, bool), chat tabs, settings, vaporize thresholds
    //   AchievementState          ISerialize: (id, count, completed time) per achievement
    //   _stashTabOptionsDict      count + (inventory guid, ISerialize: name, icon, sort order, color)
    //   _mapDiscoveryDict         count + (key, ISerialize: region id, region guid, access time, entities, low-res map)
    //   vendor purchase data      uint count + ISerialize each (kept raw: inventory guid, counts, bits)
    //   _loginCount               uint
    //   _loginRewardCooldownTimeStart  TimeSpan (long microseconds)
    //   _eventDailyGiftLastGivenTime   TimeSpan, archive version >= 12 (AddedEventDailyGiftTime)
    //
    // Versions 11 and 12 aren't in the server source this was written from; they come from the
    // running server's MHServerEmu.Core/Games.dll (read from their IL): v11 AddedMissionEventInstance
    // adds an int after each mission's state, v12 adds the trailing event gift time.
    //
    // Stock MHServerEmu numbers its versions differently - the same number means a different layout:
    //   COA build:  9 Initial, 10 AddedAdminDifficulty, 11 AddedMissionEventInstance, 12 AddedEventDailyGiftTime
    //   stock:      9 Initial, 10 AddedMissionEventInstance (no admin difficulty, no gift time)
    // So a v10 blob can be either. TryDecode tries the preferred layout (picked from the database
    // schema when it's opened), then the other, and keeps whichever reproduces the bytes exactly.
    //
    // Bools are bit-packed exactly like Archive.Transfer(ref bool): up to 5 consecutive bool
    // transfers share the byte written at the first one, even with other data in between (the
    // community bools share a byte with the first chat filters, for instance). BoolReader/BoolWriter
    // below reproduce that, so every bool in the tail is decoded and re-packed the way the server
    // would write it. The only parts kept as raw bytes (mission bodies, vendor purchase entries)
    // contain no bools.
    //
    // TryDecode only succeeds when Encode() reproduces the input exactly, so an edit can never
    // be based on a misread.
    public enum ServerLayout { Coa, Stock }

    public class PlayerTail
    {
        public class Mission
        {
            public ulong Guid { get; set; }
            public int Version { get; set; }
            public uint HeaderState { get; set; }
            public int EventInstance { get; set; }   // only when PlayerTail.HasMissionEventInstance
            public byte[] Body { get; set; }         // ISerialize content (without the size prefix)
            public int BodyState { get; set; }       // first field of the body, for display
        }

        public class CommunityMember
        {
            public string Name { get; set; }
            public ulong DbId { get; set; }
            public List<int> Circles { get; set; } = new();   // archive circle ids -> index into CircleNames
        }

        public class Achievement
        {
            public uint Id { get; set; }
            public uint Count { get; set; }
            public long CompletedTime { get; set; }   // microseconds since the unix epoch, 0 = not completed
        }

        public class StashTab
        {
            public ulong InventoryGuid { get; set; }
            public string DisplayName { get; set; }
            public ulong IconAssetGuid { get; set; }
            public int SortOrder { get; set; }
            public int Color { get; set; }            // StashTabColor
        }

        public class MapDiscovery
        {
            public ulong Key { get; set; }
            public ulong RegionId { get; set; }
            public ulong RegionGuid { get; set; }
            public long AccessTime { get; set; }
            public List<ulong> DiscoveredEntities { get; set; } = new();
            public bool RevealAll { get; set; }
            public uint MapBitCount { get; set; }     // GBitArray size, only when RevealAll is false
            public byte[] MapBits { get; set; } = Array.Empty<byte>();
        }

        public uint ArchiveVersion { get; set; }
        public ServerLayout Layout { get; set; }

        // Which version-gated fields this blob has, for its layout.
        public bool HasAdminDifficulty => Layout == ServerLayout.Coa && ArchiveVersion >= 10;
        public bool HasMissionEventInstance => ArchiveVersion >= (Layout == ServerLayout.Coa ? 11u : 10u);
        public bool CanHaveEventDailyGiftTime => Layout == ServerLayout.Coa && ArchiveVersion >= 12;

        public ulong MissionAvatarGuid { get; set; }
        public List<Mission> Missions { get; } = new();
        public List<(ulong CategoryGuid, List<ulong> Missions)> LegendaryBlacklist { get; } = new();

        public List<PropertyRecord> AvatarProperties { get; set; } = new();

        public bool HasCommunityData { get; set; }
        public bool UnusedBool { get; set; }
        public List<string> CircleNames { get; } = new();
        public List<CommunityMember> Members { get; } = new();
        public ulong AdminDifficultyOverride { get; set; }    // DataRef, 0 = none
        public List<ulong> UnlockedInventories { get; } = new();

        public List<(ulong ChannelGuid, bool Enabled)> ChatFilters { get; } = new();
        public List<ulong> ChatTabChannels { get; } = new();
        public List<long> OptionSettings { get; } = new();             // indexed by GameplayOptionSetting
        public List<(ulong Slot, ulong RarityGuid)> VaporizeThresholds { get; } = new();   // EquipmentInvUISlot -> rarity

        public List<Achievement> Achievements { get; } = new();
        public List<StashTab> StashTabs { get; } = new();
        public List<MapDiscovery> Maps { get; } = new();
        public List<byte[]> VendorPurchases { get; } = new();
        public uint LoginCount { get; set; }
        public long LoginRewardCooldownStart { get; set; }
        public bool HasEventDailyGiftTime { get; set; }
        public long EventDailyGiftLastGivenTime { get; set; }

        // VendorPurchaseData.Serialize starts with the inventory proto guid.
        public static ulong VendorInventoryGuid(byte[] body) => new ArchiveCursor(body).ReadVarUInt64();

        // GameplayOptionSetting (stock MHServerEmu names). Game 1.52 saves 19 settings; 1.53 appends 8 more.
        public static readonly string[] OptionSettingNames =
        {
            "AutoPartyEnabled", "PreferLowPopulationRegions", "DisableHeroSynergyBonusXP", "QueueWithoutSeekingOtherPlayers",
            "VaporizeCredits", "VaporizeMedKits", "ShowPlayerFloatingDamageNumbers", "ShowEnemyFloatingDamageNumbers",
            "ShowExperienceFloatingNumbers", "ShowBossIndicator", "ShowPartyMemberArrows", "MusicLevel", "SfxLevel",
            "ShowMovieSubtitles", "MicLevel", "SpeakerLevel", "Gamma", "ShowPlayerHealingNumbers", "ShowPlayerIndicator",
            // 1.53 only
            "Vibration", "ShowPlayerName", "ShowPlayerTargetIndicator", "CameraShake", "MocoLevel", "VoiceoverLevel",
            "PlayerDialogLevel", "ModifierButtonInstantPowerActivation",
        };

        public static readonly string[] EquipmentSlotNames =
        {
            "Costume", "Gear01", "Gear02", "Gear03", "Gear04", "Gear05", "Artifact01", "Medal", "Artifact02", "Relic",
            "Insignia", "Artifact03", "Ring", "Legendary",
        };

        public static readonly string[] StashTabColorNames = { "White", "Cyan", "Blue", "Green", "Orange", "Purple", "Red", "Yellow" };

        // A bogus huge value shows as no date instead of crashing the window.
        public static DateTime? FromArchiveTime(long microseconds) => AddMicroseconds(DateTime.UnixEpoch, microseconds);

        private static DateTime? AddMicroseconds(DateTime epoch, long microseconds)
        {
            if (microseconds <= 0 || microseconds > (DateTime.MaxValue.Ticks - epoch.Ticks) / 10 - TimeSpan.TicksPerDay) return null;
            return epoch.AddTicks(microseconds * 10).ToLocalTime();
        }

        public static long ToArchiveTime(DateTime time) => (time.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10;

        // Map discovery access times are Clock.GameTime: microseconds since the game epoch (Sep 22 2012), not unix time.
        private static readonly DateTime GameTimeEpoch = DateTime.UnixEpoch.AddTicks(1348306278045983L * 10);
        public static DateTime? FromGameTime(long microseconds) => AddMicroseconds(GameTimeEpoch, microseconds);
    }

    public static class PlayerTailCodec
    {
        // Highest archive version each layout is known to write.
        private const uint MaxCoaVersion = 12, MaxStockVersion = 10;

        // Tried first when a blob could be either layout. Set when a database is opened.
        public static ServerLayout PreferredLayout { get; set; } = ServerLayout.Coa;

        public static PlayerTail TryDecode(byte[] tail, uint archiveVersion, out string error)
        {
            ServerLayout other = PreferredLayout == ServerLayout.Coa ? ServerLayout.Stock : ServerLayout.Coa;
            PlayerTail result = TryDecode(tail, archiveVersion, PreferredLayout, out error);
            if (result != null) return result;
            PlayerTail fallback = TryDecode(tail, archiveVersion, other, out _);
            if (fallback != null) error = null;
            return fallback;
        }

        public static PlayerTail TryDecode(byte[] tail, uint archiveVersion, ServerLayout layout, out string error)
        {
            error = null;
            uint max = layout == ServerLayout.Coa ? MaxCoaVersion : MaxStockVersion;
            if (archiveVersion > max)
            {
                error = $"Archive version {archiveVersion} is newer than the editor knows for this server type (up to {max}).";
                return null;
            }
            try
            {
                PlayerTail result = Decode(tail, archiveVersion, layout);
                byte[] again = Encode(result);
                if (again.AsSpan().SequenceEqual(tail) == false)
                {
                    error = "Re-encoding didn't reproduce the saved bytes - this player's data has a layout the editor doesn't fully understand.";
                    return null;
                }
                return result;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // ---------------------------------------------------------------- decode

        public static PlayerTail Decode(byte[] tail, uint archiveVersion, ServerLayout layout)
        {
            BoolReader r = new(new ArchiveCursor(tail));
            ArchiveCursor c = r.Cursor;
            PlayerTail t = new() { ArchiveVersion = archiveVersion, Layout = layout };

            // MissionManager
            int missionsEnd = c.ReadSizePrefixHeader();
            t.MissionAvatarGuid = c.ReadVarUInt64();
            ulong numMissions = c.ReadVarUInt64();
            for (ulong i = 0; i < numMissions; i++)
            {
                PlayerTail.Mission mission = new() { Guid = c.ReadVarUInt64(), Version = c.ReadZigZagInt32(), HeaderState = c.ReadVarUInt32() };
                if (t.HasMissionEventInstance)
                    mission.EventInstance = c.ReadZigZagInt32();
                mission.Body = ReadSerializeBody(c);
                try { mission.BodyState = new ArchiveCursor(mission.Body).ReadZigZagInt32(); } catch (Exception) { }
                t.Missions.Add(mission);
            }
            int numCategories = c.ReadZigZagInt32();
            for (int i = 0; i < numCategories; i++)
            {
                ulong category = c.ReadVarUInt64();
                t.LegendaryBlacklist.Add((category, ReadULongList(c)));
            }
            c.ExpectPosition(missionsEnd, "MissionManager");

            // Avatar properties (ReplicatedPropertyCollection -> PropertyCollection.Serialize)
            int avatarPropsEnd = c.ReadSizePrefixHeader();
            t.AvatarProperties = PropertyBlockCodec.Decode(c);
            c.ExpectPosition(avatarPropsEnd, "AvatarProperties");

            t.HasCommunityData = r.ReadBool();
            if (t.HasCommunityData)
            {
                int communityEnd = c.ReadSizePrefixHeader();
                int numCircles = c.ReadZigZagInt32();
                for (int i = 0; i < numCircles; i++) t.CircleNames.Add(ReadString(c));
                int numMembers = c.ReadZigZagInt32();
                for (int i = 0; i < numMembers; i++)
                {
                    PlayerTail.CommunityMember member = new() { Name = ReadString(c), DbId = c.ReadVarUInt64() };
                    int memberEnd = c.ReadSizePrefixHeader();
                    int memberCircles = c.ReadZigZagInt32();
                    for (int j = 0; j < memberCircles; j++) member.Circles.Add(c.ReadZigZagInt32());
                    c.ExpectPosition(memberEnd, "CommunityMember");
                    t.Members.Add(member);
                }
                c.ExpectPosition(communityEnd, "Community");
            }

            if (t.HasAdminDifficulty)
                t.AdminDifficultyOverride = c.ReadVarUInt64();

            t.UnusedBool = r.ReadBool();
            t.UnlockedInventories.AddRange(ReadULongList(c));

            // GameplayOptions
            int optionsEnd = c.ReadSizePrefixHeader();
            ulong numFilters = c.ReadVarUInt64();
            for (ulong i = 0; i < numFilters; i++)
            {
                ulong channel = c.ReadVarUInt64();
                t.ChatFilters.Add((channel, r.ReadBool()));
            }
            t.ChatTabChannels.AddRange(ReadULongList(c));
            ulong numSettings = c.ReadVarUInt64();
            for (ulong i = 0; i < numSettings; i++) t.OptionSettings.Add(c.ReadZigZagInt64());
            ulong numVaporize = c.ReadVarUInt64();
            for (ulong i = 0; i < numVaporize; i++) t.VaporizeThresholds.Add((c.ReadVarUInt64(), c.ReadVarUInt64()));
            c.ExpectPosition(optionsEnd, "GameplayOptions");

            // AchievementState
            int achievementsEnd = c.ReadSizePrefixHeader();
            uint numAchievements = c.ReadVarUInt32();
            for (uint i = 0; i < numAchievements; i++)
                t.Achievements.Add(new PlayerTail.Achievement { Id = c.ReadVarUInt32(), Count = c.ReadVarUInt32(), CompletedTime = c.ReadZigZagInt64() });
            c.ExpectPosition(achievementsEnd, "AchievementState");

            // Stash tab options
            ulong numStash = c.ReadVarUInt64();
            for (ulong i = 0; i < numStash; i++)
            {
                PlayerTail.StashTab tab = new() { InventoryGuid = c.ReadVarUInt64() };
                int end = c.ReadSizePrefixHeader();
                tab.DisplayName = ReadString(c);
                tab.IconAssetGuid = c.ReadVarUInt64();
                tab.SortOrder = c.ReadZigZagInt32();
                tab.Color = c.ReadZigZagInt32();
                c.ExpectPosition(end, "StashTabOptions");
                t.StashTabs.Add(tab);
            }

            // Map discovery
            ulong numMaps = c.ReadVarUInt64();
            for (ulong i = 0; i < numMaps; i++)
            {
                PlayerTail.MapDiscovery map = new() { Key = c.ReadVarUInt64() };
                int end = c.ReadSizePrefixHeader();
                map.RegionId = c.ReadVarUInt64();
                map.RegionGuid = c.ReadVarUInt64();
                map.AccessTime = c.ReadZigZagInt64();
                map.DiscoveredEntities = ReadULongList(c);
                int lowResEnd = c.ReadSizePrefixHeader();
                map.RevealAll = r.ReadBool();
                if (map.RevealAll == false)
                {
                    int bitArrayEnd = c.ReadSizePrefixHeader();
                    map.MapBitCount = c.ReadVarUInt32();
                    int words = (int)((map.MapBitCount + 63) / 64);
                    map.MapBits = c.ReadRawBytes(words * 8);
                    c.ExpectPosition(bitArrayEnd, "GBitArray");
                }
                c.ExpectPosition(lowResEnd, "LowResMap");
                c.ExpectPosition(end, "MapDiscoveryData");
                t.Maps.Add(map);
            }

            uint numVendor = c.ReadVarUInt32();
            for (uint i = 0; i < numVendor; i++) t.VendorPurchases.Add(ReadSerializeBody(c));

            t.LoginCount = c.ReadVarUInt32();
            t.LoginRewardCooldownStart = c.ReadZigZagInt64();
            if (t.CanHaveEventDailyGiftTime && c.HasMore)
            {
                t.HasEventDailyGiftTime = true;
                t.EventDailyGiftLastGivenTime = c.ReadZigZagInt64();
            }

            if (c.HasMore)
                throw new InvalidDataException($"{c.Length - c.Position} unexpected bytes after the player data.");
            return t;
        }

        // ---------------------------------------------------------------- encode

        public static byte[] Encode(PlayerTail t)
        {
            BoolWriter b = new(new ArchiveCursorWriter());
            ArchiveCursorWriter w = b.Writer;

            int missionsStart = w.ReserveUnencodedUInt32();
            w.WriteVarUInt64(t.MissionAvatarGuid);
            w.WriteVarUInt64((ulong)t.Missions.Count);
            foreach (PlayerTail.Mission mission in t.Missions)
            {
                w.WriteVarUInt64(mission.Guid);
                w.WriteZigZagInt32(mission.Version);
                w.WriteVarUInt32(mission.HeaderState);
                if (t.HasMissionEventInstance)
                    w.WriteZigZagInt32(mission.EventInstance);
                WriteSerializeBody(w, mission.Body);
            }
            w.WriteZigZagInt32(t.LegendaryBlacklist.Count);
            foreach (var (category, list) in t.LegendaryBlacklist)
            {
                w.WriteVarUInt64(category);
                WriteULongList(w, list);
            }
            End(w, missionsStart);

            int avatarPropsStart = w.ReserveUnencodedUInt32();
            PropertyBlockCodec.Encode(w, t.AvatarProperties);
            End(w, avatarPropsStart);

            b.WriteBool(t.HasCommunityData);
            if (t.HasCommunityData)
            {
                int communityStart = w.ReserveUnencodedUInt32();
                w.WriteZigZagInt32(t.CircleNames.Count);
                foreach (string name in t.CircleNames) WriteString(w, name);
                w.WriteZigZagInt32(t.Members.Count);
                foreach (PlayerTail.CommunityMember member in t.Members)
                {
                    WriteString(w, member.Name);
                    w.WriteVarUInt64(member.DbId);
                    int memberStart = w.ReserveUnencodedUInt32();
                    w.WriteZigZagInt32(member.Circles.Count);
                    foreach (int circle in member.Circles) w.WriteZigZagInt32(circle);
                    End(w, memberStart);
                }
                End(w, communityStart);
            }

            if (t.HasAdminDifficulty)
                w.WriteVarUInt64(t.AdminDifficultyOverride);

            b.WriteBool(t.UnusedBool);
            WriteULongList(w, t.UnlockedInventories);

            int optionsStart = w.ReserveUnencodedUInt32();
            w.WriteVarUInt64((ulong)t.ChatFilters.Count);
            foreach (var (channel, enabled) in t.ChatFilters)
            {
                w.WriteVarUInt64(channel);
                b.WriteBool(enabled);
            }
            WriteULongList(w, t.ChatTabChannels);
            w.WriteVarUInt64((ulong)t.OptionSettings.Count);
            foreach (long setting in t.OptionSettings) w.WriteZigZagInt64(setting);
            w.WriteVarUInt64((ulong)t.VaporizeThresholds.Count);
            foreach (var (slot, rarity) in t.VaporizeThresholds) { w.WriteVarUInt64(slot); w.WriteVarUInt64(rarity); }
            End(w, optionsStart);

            int achievementsStart = w.ReserveUnencodedUInt32();
            w.WriteVarUInt32((uint)t.Achievements.Count);
            foreach (PlayerTail.Achievement achievement in t.Achievements)
            {
                w.WriteVarUInt32(achievement.Id);
                w.WriteVarUInt32(achievement.Count);
                w.WriteZigZagInt64(achievement.CompletedTime);
            }
            End(w, achievementsStart);

            w.WriteVarUInt64((ulong)t.StashTabs.Count);
            foreach (PlayerTail.StashTab tab in t.StashTabs)
            {
                w.WriteVarUInt64(tab.InventoryGuid);
                int start = w.ReserveUnencodedUInt32();
                WriteString(w, tab.DisplayName);
                w.WriteVarUInt64(tab.IconAssetGuid);
                w.WriteZigZagInt32(tab.SortOrder);
                w.WriteZigZagInt32(tab.Color);
                End(w, start);
            }

            w.WriteVarUInt64((ulong)t.Maps.Count);
            foreach (PlayerTail.MapDiscovery map in t.Maps)
            {
                w.WriteVarUInt64(map.Key);
                int start = w.ReserveUnencodedUInt32();
                w.WriteVarUInt64(map.RegionId);
                w.WriteVarUInt64(map.RegionGuid);
                w.WriteZigZagInt64(map.AccessTime);
                WriteULongList(w, map.DiscoveredEntities);
                int lowResStart = w.ReserveUnencodedUInt32();
                b.WriteBool(map.RevealAll);
                if (map.RevealAll == false)
                {
                    int bitArrayStart = w.ReserveUnencodedUInt32();
                    w.WriteVarUInt32(map.MapBitCount);
                    w.WriteRawBytes(map.MapBits);
                    End(w, bitArrayStart);
                }
                End(w, lowResStart);
                End(w, start);
            }

            w.WriteVarUInt32((uint)t.VendorPurchases.Count);
            foreach (byte[] body in t.VendorPurchases) WriteSerializeBody(w, body);

            w.WriteVarUInt32(t.LoginCount);
            w.WriteZigZagInt64(t.LoginRewardCooldownStart);
            if (t.HasEventDailyGiftTime && t.CanHaveEventDailyGiftTime)
                w.WriteZigZagInt64(t.EventDailyGiftLastGivenTime);
            return w.ToArray();
        }

        // ---------------------------------------------------------------- helpers

        // Archive.Transfer(ref bool) unpacking: a byte holds up to 5 bools in its top bits and the
        // number still unread in its low 3 bits; a new byte is read only once the last one is used up.
        private sealed class BoolReader
        {
            public ArchiveCursor Cursor { get; }
            private byte _buffer;
            private byte _bitsRead;

            public BoolReader(ArchiveCursor cursor) => Cursor = cursor;

            public bool ReadBool()
            {
                if (_buffer == 0)
                    _buffer = Cursor.ReadRawByte();

                byte remaining = (byte)(_buffer & 0x7);
                if (remaining == 0 || remaining > 5 || _bitsRead >= 5)
                    throw new InvalidDataException("Bool decoding failed.");

                bool value = (_buffer & (1 << (7 - _bitsRead))) != 0;
                remaining--;
                _buffer = (byte)((_buffer & 0xF8) | remaining);
                _bitsRead++;
                if (remaining == 0)
                {
                    _buffer = 0;
                    _bitsRead = 0;
                }
                return value;
            }
        }

        // Archive.Transfer(ref bool) packing: the first bool of a group writes a new byte and
        // remembers where; later ones patch that byte until it holds 5.
        private sealed class BoolWriter
        {
            public ArchiveCursorWriter Writer { get; }
            private int _lastBitOffset = -1;

            public BoolWriter(ArchiveCursorWriter writer) => Writer = writer;

            public void WriteBool(bool value)
            {
                byte bitBuffer = _lastBitOffset < 0 ? (byte)0 : Writer.ByteAt(_lastBitOffset);
                byte numEncoded = (byte)(bitBuffer & 0x7);
                if (numEncoded >= 5) throw new InvalidOperationException("Bool encoding failed.");

                bitBuffer |= (byte)((value ? 1 : 0) << (7 - numEncoded));
                bitBuffer &= 0xF8;
                bitBuffer |= ++numEncoded;

                if (_lastBitOffset < 0)
                {
                    _lastBitOffset = Writer.Position;
                    Writer.WriteRawByte(bitBuffer);
                }
                else
                {
                    Writer.PatchByte(_lastBitOffset, bitBuffer);
                    if (numEncoded >= 5)
                        _lastBitOffset = -1;
                }
            }
        }

        private static List<ulong> ReadULongList(ArchiveCursor c)
        {
            ulong count = c.ReadVarUInt64();
            List<ulong> list = new();
            for (ulong i = 0; i < count; i++) list.Add(c.ReadVarUInt64());
            return list;
        }

        private static void WriteULongList(ArchiveCursorWriter w, List<ulong> list)
        {
            w.WriteVarUInt64((ulong)list.Count);
            foreach (ulong value in list) w.WriteVarUInt64(value);
        }

        private static void End(ArchiveCursorWriter w, int start) => w.PatchUnencodedUInt32(start, (uint)(w.Position - start));

        private static byte[] ReadSerializeBody(ArchiveCursor c)
        {
            int end = c.ReadSizePrefixHeader();
            return c.ReadRawBytes(end - c.Position);
        }

        private static void WriteSerializeBody(ArchiveCursorWriter w, byte[] body)
        {
            w.WriteUnencodedUInt32((uint)(body.Length + 4));
            w.WriteRawBytes(body);
        }

        private static string ReadString(ArchiveCursor c) => Encoding.UTF8.GetString(c.ReadRawBytes((int)c.ReadVarUInt32()));

        private static void WriteString(ArchiveCursorWriter w, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            w.WriteVarUInt32((uint)bytes.Length);
            w.WriteRawBytes(bytes);
        }
    }
}
