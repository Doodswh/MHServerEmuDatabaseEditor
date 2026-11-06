using System;
using System.Collections.Generic;
using System.Linq;

namespace MHDBEditor.Utility
{
    // Game-data lookups for the Player Data window (missions, stash tabs, difficulty override).
    public partial class PrototypeResolver
    {
        public class StashTab
        {
            public ulong Guid { get; init; }
            public string Name { get; init; }
            public string Path { get; init; }
            public bool LockedByDefault { get; init; }
            public string ForAvatar { get; init; }
        }

        // Every stash inventory. Only LockedByDefault ones are ever put in the player's unlocked
        // list - the server adds the rest to everyone (Player.InitInventories).
        public List<StashTab> GetStashTabs()
        {
            List<StashTab> result = new();
            if (_gameData == null) return result;

            foreach (Entry entry in _byGuid.Values)
            {
                if (entry.IsAbstract || GetRuntimeClass(entry.Guid) != "PlayerStashInventoryPrototype") continue;
                if (GetPrototypeData(entry.DataRef) is not PrototypeData data) continue;

                ulong avatarRef = GetInheritedSimple(data, "ForAvatar");
                result.Add(new StashTab
                {
                    Guid = entry.Guid,
                    Name = ShortName(entry.Path),
                    Path = entry.Path,
                    LockedByDefault = GetInheritedSimple(data, "LockedByDefault") != 0,
                    ForAvatar = avatarRef != 0 && _byDataRef.TryGetValue(avatarRef, out Entry avatar) ? ShortName(avatar.Path) : null,
                });
            }
            return result.OrderBy(t => t.ForAvatar != null).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // DifficultyTier prototypes as (DataRef, name) - AdminDifficultyOverride stores the DataRef.
        public List<(ulong DataRef, string Name)> GetDifficultyTiers()
        {
            if (_gameData == null) return new();
            return _byGuid.Values
                .Where(e => e.IsAbstract == false && GetRuntimeClass(e.Guid) == "DifficultyTierPrototype")
                .Select(e => (e.DataRef, ShortName(e.Path)))
                .OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string FormatDataRef(ulong dataRef) =>
            dataRef == 0 ? "(none)" : _byDataRef.TryGetValue(dataRef, out Entry entry) ? entry.DisplayName : $"0x{dataRef:X}";

        // "Missions/Prototypes/PVEEndgamePatrolMissions/..." -> "PVEEndgamePatrolMissions"
        public string MissionGroup(ulong missionGuid)
        {
            if (_byGuid.TryGetValue(missionGuid, out Entry entry) == false) return "(unknown)";
            string[] parts = entry.Path.Split('/');
            int start = parts.Length > 1 && parts[0].Equals("Missions", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (start < parts.Length - 1 && parts[start].Equals("Prototypes", StringComparison.OrdinalIgnoreCase)) start++;
            return start < parts.Length - 1 ? parts[start] : "(root)";
        }
    }
}
