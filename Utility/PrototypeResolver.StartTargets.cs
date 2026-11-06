using System;
using System.Collections.Generic;
using System.Linq;

namespace MHDBEditor.Utility
{
    // Places a player can be sent to on next login. Player.StartTarget holds the DataRef
    // (not the guid) of a RegionConnectionTargetPrototype - PlayerHandle.BeginRegionTransferToStartTarget()
    // reads it on login and only falls back to the default start when the id isn't a
    // connection target at all. A target that exists but points at a region that can't be
    // built (e.g. a custom map that was removed) leaves the player on the loading screen.
    public partial class PrototypeResolver
    {
        public class StartLocation
        {
            public ulong DataRef { get; init; }
            public string Path { get; init; }
            public string Name { get; init; }
            public ulong RegionDataRef { get; init; }
            public ulong RegionGuid { get; init; }
            public string RegionName { get; init; }
            public string RegionPath { get; init; }
            public string Behavior { get; init; }   // Town, PublicCombatZone, PrivateStory, ...
            public bool IsRegionStart { get; init; }    // the region's own StartTarget (its normal entry point)
            public string Note { get; set; }            // "Server default start", "Server fallback", ...

            public bool IsTown => string.Equals(Behavior, "Town", StringComparison.OrdinalIgnoreCase);
            public override string ToString() => $"{RegionName} - {Name}";
        }

        private List<StartLocation> _startLocations;

        public List<StartLocation> GetStartLocations()
        {
            if (_gameData == null) return new List<StartLocation>();
            lock (_byGuid) return _startLocations ??= BuildStartLocations();
        }

        private List<StartLocation> BuildStartLocations()
        {

            List<StartLocation> result = new();
            foreach (Entry entry in _byGuid.Values)
            {
                if (entry.IsAbstract || GetRuntimeClass(entry.Guid) != "RegionConnectionTargetPrototype")
                    continue;
                StartLocation location = BuildStartLocation(entry);
                if (location != null)
                    result.Add(location);
            }

            // Mark the targets the server itself uses (GlobalsPrototype).
            Entry globals = _byGuid.Values.FirstOrDefault(e => GetRuntimeClass(e.Guid) == "GlobalsPrototype");
            if (globals != null && GetPrototypeData(globals.DataRef) is PrototypeData globalsData)
            {
                foreach (var (field, note) in new[]
                {
                    ("DefaultStartTargetStartingRegion", "Server default (new players / bad id)"),
                    ("DefaultStartTargetFallbackRegion", "Server fallback (safe pick)"),
                    ("DefaultStartTargetPrestigeRegion", "Prestige start"),
                })
                {
                    ulong dataRef = GetInheritedSimple(globalsData, field);
                    StartLocation match = result.FirstOrDefault(l => l.DataRef == dataRef);
                    if (match != null)
                        match.Note = match.Note == null ? note : $"{match.Note}; {note}";
                }
            }

            return result
                .OrderByDescending(l => l.Note != null)
                .ThenByDescending(l => l.IsTown)
                .ThenBy(l => l.RegionName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(l => l.IsRegionStart)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public ulong DefaultStartTarget => GetStartLocations().FirstOrDefault(l => l.Note?.Contains("Server default") == true)?.DataRef ?? 0;

        // What a raw Player.StartTarget value points at, in words, plus whether login will work.
        public (string Text, bool Ok) DescribeStartTarget(long value)
        {
            if (_gameData == null)
                return ("Load Calligraphy.sip to see where this is.", true);

            ulong dataRef = unchecked((ulong)value);
            if (dataRef == 0)
                return ("Not set - the server uses its default start.", true);

            StartLocation known = GetStartLocations().FirstOrDefault(l => l.DataRef == dataRef);
            if (known != null)
                return ($"{known.RegionName} - {known.Name} ({known.Behavior ?? "?"})", true);

            if (_byDataRef.TryGetValue(dataRef, out Entry entry))
            {
                if (GetRuntimeClass(entry.Guid) == "RegionConnectionTargetPrototype")
                    return ($"{entry.DisplayName} - its region is missing or isn't a region. Players will likely get stuck loading.", false);
                return ($"{entry.DisplayName} isn't a start location - the server falls back to its default start.", false);
            }

            if (_byGuid.TryGetValue(dataRef, out Entry byGuid))
                return ($"This is the guid of {byGuid.DisplayName}, not its id - the server falls back to its default start.", false);

            return ("Not in the game data (removed/custom map?) - the server falls back to its default start.", false);
        }

        private StartLocation BuildStartLocation(Entry entry)
        {
            if (GetPrototypeData(entry.DataRef) is not PrototypeData data) return null;

            ulong regionRef = GetInheritedSimple(data, "Region");
            if (regionRef == 0 || _byDataRef.TryGetValue(regionRef, out Entry region) == false || region.IsAbstract)
                return null;
            if (GetPrototypeData(region.DataRef) is not PrototypeData regionData) return null;

            ulong behaviorAsset = GetInheritedSimple(regionData, "Behavior");
            string behavior = behaviorAsset != 0 && _assetIdToName.TryGetValue(behaviorAsset, out string b) ? b : null;

            return new StartLocation
            {
                DataRef = entry.DataRef,
                Path = entry.Path,
                Name = ShortName(StripExtension(entry.Path)),
                RegionDataRef = regionRef,
                RegionGuid = region.Guid,
                RegionName = ShortName(StripExtension(region.Path)),
                RegionPath = region.Path,
                Behavior = behavior,
                IsRegionStart = GetInheritedSimple(regionData, "StartTarget") == entry.DataRef,
            };
        }
    }
}
