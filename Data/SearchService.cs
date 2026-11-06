using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using Dapper;
using MHDBEditor.ArchiveFormat;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Data
{
    // Read-only searches across every player and item in the database. Items are scanned in
    // full (decoding all 2.3M item blobs takes a few seconds); the affix check is the slow part
    // (~20k unique affix rolls a second) and only runs when an affix-problem filter is set.
    public class SearchService
    {
        public enum ItemPlace { Anywhere, PlayerInventories, OnHeroes, OnTeamUps, NoOwner }
        public enum AffixProblem { DontCheck, AnyProblem, OverCap, BreaksItem, DoesNothing }

        public class ItemQuery
        {
            public string Category { get; init; }         // null = any (e.g. "Rings", from GetItemCatalog)
            public long ItemGuid { get; init; }           // 0 = any
            public ulong RarityGuid { get; init; }
            public int? MinLevel { get; init; }
            public int? MaxLevel { get; init; }
            public ulong AffixGuid { get; init; }         // 0 = any
            public AffixProblem Problem { get; init; }
            public ItemPlace Place { get; init; }
            public long AccountId { get; init; }          // 0 = any
        }

        // One entry per item type / affix that exists in the database, for the dropdowns.
        public record CatalogEntry(ulong Guid, string Group, string Name, int Count);

        public class ItemHit
        {
            public long DbGuid { get; init; }
            public long AccountId { get; init; }          // 0 = no owner found
            public string PlayerName { get; init; }
            public string Where { get; init; }            // "Player" or the hero / team-up name
            public string Inventory { get; init; }
            public uint Slot { get; init; }
            public long EntityProtoGuid { get; init; }
            public string ItemName { get; init; }
            public string Rarity { get; init; }
            public int Level { get; init; }
            public string Affixes { get; init; }
            public string Problem { get; init; }
        }

        public class PlayerQuery
        {
            public long AccountId { get; init; }          // 0 = any
            public ulong RegionGuid { get; init; }        // 0 = anywhere
            public bool BrokenLocationOnly { get; init; }
            public ulong HeroGuid { get; init; }          // 0 = any
            public int? SeenWithinDays { get; init; }
            public int? NotSeenForDays { get; init; }
            public bool BannedOnly { get; init; }
            public HashSet<long> OnlyAccounts { get; init; }
        }

        public class PlayerHit
        {
            public long AccountId { get; init; }
            public string PlayerName { get; init; }
            public string Email { get; init; }
            public long StartTarget { get; init; }
            public string Location { get; init; }
            public bool LocationOk { get; init; }
            public int Heroes { get; init; }
            public DateTime? LastLogout { get; init; }
            public bool Banned { get; init; }
            public AccountUserLevel UserLevel { get; init; }
        }

        private readonly DbConnectionManager _connectionManager;
        private readonly PrototypeResolver _resolver;

        public SearchService(DbConnectionManager connectionManager, PrototypeResolver resolver)
        {
            _connectionManager = connectionManager;
            _resolver = resolver;
        }

        // ---------------------------------------------------------------- items

        public List<ItemHit> FindItems(ItemQuery q, int limit, IProgress<string> progress, CancellationToken cancel, out int totalMatches)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            progress?.Report("Loading owners...");

            Dictionary<long, string> players = connection.Query<(long Id, string Name)>("SELECT Id, PlayerName FROM Account").ToDictionary(a => a.Id, a => a.Name);
            Dictionary<long, (long Account, string Name, bool TeamUp)> holders = new();
            foreach (var (table, teamUp) in new[] { ("Avatar", false), ("TeamUp", true) })
                foreach (var (id, container, proto) in connection.Query<(long, long, long)>($"SELECT DbGuid, ContainerDbGuid, EntityProtoGuid FROM {table}"))
                    holders[id] = (container, ShortName(_resolver.Format(unchecked((ulong)proto))), teamUp);

            Dictionary<long, string> names = new();
            Dictionary<ulong, string> otherNames = new();
            string Name(long guid) { if (names.TryGetValue(guid, out string n) == false) names[guid] = n = _resolver.Format(unchecked((ulong)guid)); return n; }
            string OtherName(ulong guid) { if (otherNames.TryGetValue(guid, out string n) == false) otherNames[guid] = n = _resolver.Format(guid); return n; }

            Dictionary<long, string> categories = new();
            Dictionary<(ulong, ulong, ulong, int, int), PrototypeResolver.AffixReport> reports = new();

            List<ItemHit> hits = new();
            totalMatches = 0;
            long scanned = 0;
            long total = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM Item");

            using SQLiteCommand command = new("SELECT DbGuid, ContainerDbGuid, InventoryProtoGuid, Slot, EntityProtoGuid, ArchiveData FROM Item", connection);
            using SQLiteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (++scanned % 50000 == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress?.Report($"Scanned {scanned:N0} of {total:N0} items, {totalMatches:N0} match so far...");
                }

                long entityProto = reader.GetInt64(4);
                if (q.ItemGuid != 0 && entityProto != q.ItemGuid) continue;
                if (q.Category != null)
                {
                    if (categories.TryGetValue(entityProto, out string category) == false)
                        categories[entityProto] = category = ItemCategory(unchecked((ulong)entityProto));
                    if (category != q.Category) continue;
                }

                long container = reader.GetInt64(1);
                long accountId; string where; ItemPlace place;
                if (players.ContainsKey(container)) { accountId = container; where = "Player"; place = ItemPlace.PlayerInventories; }
                else if (holders.TryGetValue(container, out var holder) && players.ContainsKey(holder.Account))
                {
                    accountId = holder.Account; where = holder.Name; place = holder.TeamUp ? ItemPlace.OnTeamUps : ItemPlace.OnHeroes;
                }
                else { accountId = 0; where = "(no owner)"; place = ItemPlace.NoOwner; }

                if (q.Place != ItemPlace.Anywhere && q.Place != place) continue;
                if (q.AccountId != 0 && accountId != q.AccountId) continue;
                string playerName = accountId != 0 ? players[accountId] : "";

                ItemSpecRecord spec = null;
                bool needSpec = q.RarityGuid != 0 || q.MinLevel != null || q.MaxLevel != null || q.AffixGuid != 0 || q.Problem != AffixProblem.DontCheck;
                if (reader.IsDBNull(5) == false)
                {
                    try { spec = EntityArchiveCodec.Parse((byte[])reader[5]).ItemSpec; }
                    catch (Exception) { spec = null; }
                }
                if (needSpec && spec == null) continue;

                if (spec != null)
                {
                    if (q.RarityGuid != 0 && spec.RarityProtoGuid != q.RarityGuid) continue;
                    if (q.MinLevel != null && spec.ItemLevel < q.MinLevel) continue;
                    if (q.MaxLevel != null && spec.ItemLevel > q.MaxLevel) continue;
                    if (q.AffixGuid != 0 && spec.Affixes.Any(a => a.AffixProtoGuid == q.AffixGuid) == false) continue;
                }

                string problem = null;
                if (q.Problem != AffixProblem.DontCheck)
                {
                    problem = CheckAffixes(spec, reports, q.Problem);
                    if (problem == null) continue;
                }

                totalMatches++;
                if (hits.Count >= limit) continue;

                hits.Add(new ItemHit
                {
                    DbGuid = reader.GetInt64(0),
                    AccountId = accountId,
                    PlayerName = playerName,
                    Where = where,
                    Inventory = Name(reader.GetInt64(2)),
                    Slot = unchecked((uint)reader.GetInt64(3)),
                    EntityProtoGuid = entityProto,
                    ItemName = Name(entityProto),
                    Rarity = spec != null ? OtherName(spec.RarityProtoGuid) : "",
                    Level = spec?.ItemLevel ?? 0,
                    Affixes = spec == null ? "" : spec.Affixes.Count == 0 ? "(none)"
                        : $"{spec.Affixes.Count}: " + string.Join(", ", spec.Affixes.Take(4).Select(a => OtherName(a.AffixProtoGuid))) + (spec.Affixes.Count > 4 ? ", ..." : ""),
                    Problem = problem ?? "",
                });
            }

            progress?.Report($"Scanned {scanned:N0} items.");
            return hits;
        }

        // Same checks as the item editor's affix grid: a stat over its property cap (alone or
        // added up across affixes), an affix that breaks the item, or one that does nothing.
        private string CheckAffixes(ItemSpecRecord spec, Dictionary<(ulong, ulong, ulong, int, int), PrototypeResolver.AffixReport> cache, AffixProblem wanted)
        {
            if (_resolver.HasGameData == false || spec.Affixes.Count == 0) return null;

            List<PrototypeResolver.AffixReport> itemReports = new();
            bool broken = false;
            foreach (AffixSpecRecord affix in spec.Affixes)
            {
                var key = (affix.AffixProtoGuid, affix.ScopeProtoGuid, spec.EquippableByGuid, spec.ItemLevel, affix.Seed);
                if (cache.TryGetValue(key, out var report) == false)
                {
                    try { report = _resolver.GetAffixReport(affix.AffixProtoGuid, affix.ScopeProtoGuid, spec.EquippableByGuid, spec.ItemLevel, affix.Seed, savedAffix: true); }
                    catch (Exception) { report = null; }
                    cache[key] = report;
                }
                if (report == null) continue;
                // The server stops applying at the first breaking affix, so later ones don't count.
                if (report.Health == PrototypeResolver.AffixHealth.BreaksItem) { broken = true; break; }
                itemReports.Add(report);
            }

            List<string> problems = new();
            if (wanted is AffixProblem.AnyProblem or AffixProblem.BreaksItem && broken)
                problems.Add("breaks item");
            if (wanted is AffixProblem.AnyProblem or AffixProblem.OverCap)
            {
                List<PrototypeResolver.AffixStat> stats = itemReports.SelectMany(r => r.Stats).ToList();
                problems.AddRange(stats.Where(s => s.CappedAt != null && s.Skipped == false).Select(s => $"{s.Name} {s.Value} over cap {s.CappedAt}"));
                // Built-in stats only matter when they stack with a capped affix stat, and reading
                // them is slow, so skip items with no such stat.
                List<PrototypeResolver.BuiltInStat> builtIns = null;
                if (stats.Any(s => s.Number != null && s.SumsWhenStacked && (s.CapMax != null || s.CapMin != null)))
                {
                    try { builtIns = _resolver.GetBuiltInStats(spec.ItemProtoGuid, spec.RarityProtoGuid, spec.ItemLevel, spec.Seed, spec.EquippableByGuid); }
                    catch (Exception) { }
                }
                problems.AddRange(PrototypeResolver.FindStackedCaps(stats.Concat((builtIns ?? new()).Where(b => b.Report != null).SelectMany(b => b.Report.Stats))));
            }
            if (wanted is AffixProblem.AnyProblem or AffixProblem.DoesNothing && itemReports.Any(r => r.Health == PrototypeResolver.AffixHealth.Inert))
                problems.Add($"{itemReports.Count(r => r.Health == PrototypeResolver.AffixHealth.Inert)} affix(es) do nothing");

            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        // ---------------------------------------------------------------- players

        public List<PlayerHit> FindPlayers(PlayerQuery q)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();

            Dictionary<ulong, PrototypeResolver.StartLocation> targets = _resolver.HasGameData
                ? _resolver.GetStartLocations().GroupBy(l => l.DataRef).ToDictionary(g => g.Key, g => g.First())
                : new();

            Dictionary<long, int> heroCounts = connection.Query<(long, int)>("SELECT ContainerDbGuid, COUNT(*) FROM Avatar GROUP BY ContainerDbGuid").ToDictionary(x => x.Item1, x => x.Item2);
            HashSet<long> heroOwners = q.HeroGuid == 0 ? null
                : connection.Query<long>("SELECT ContainerDbGuid FROM Avatar WHERE EntityProtoGuid = @Guid", new { Guid = unchecked((long)q.HeroGuid) }).ToHashSet();

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            List<PlayerHit> hits = new();
            foreach (var row in connection.Query<(long Id, string Email, string PlayerName, long Flags, long UserLevel, long? StartTarget, long? LastLogout)>(
                "SELECT a.Id, a.Email, a.PlayerName, a.Flags, a.UserLevel, p.StartTarget, p.LastLogoutTime FROM Account a LEFT JOIN Player p ON p.DbGuid = a.Id ORDER BY a.PlayerName"))
            {
                if (q.OnlyAccounts != null && q.OnlyAccounts.Contains(row.Id) == false) continue;
                if (q.AccountId != 0 && row.Id != q.AccountId) continue;
                bool banned = (((AccountFlags)row.Flags) & AccountFlags.IsBanned) != 0;
                if (q.BannedOnly && banned == false) continue;
                if (heroOwners != null && heroOwners.Contains(row.Id) == false) continue;

                long lastLogoutMs = row.LastLogout ?? 0;
                if (q.SeenWithinDays != null && (lastLogoutMs <= 0 || nowMs - lastLogoutMs > q.SeenWithinDays * 86400000L)) continue;
                if (q.NotSeenForDays != null && lastLogoutMs > 0 && nowMs - lastLogoutMs < q.NotSeenForDays * 86400000L) continue;

                long startTarget = row.StartTarget ?? 0;
                targets.TryGetValue(unchecked((ulong)startTarget), out PrototypeResolver.StartLocation location);
                if (q.RegionGuid != 0 && location?.RegionGuid != q.RegionGuid) continue;
                var (text, ok) = _resolver.HasGameData ? _resolver.DescribeStartTarget(startTarget) : ($"0x{startTarget:X}", true);
                if (q.BrokenLocationOnly && ok) continue;

                hits.Add(new PlayerHit
                {
                    AccountId = row.Id,
                    PlayerName = row.PlayerName,
                    Email = row.Email,
                    StartTarget = startTarget,
                    Location = location != null ? location.RegionName : text,
                    LocationOk = ok,
                    Heroes = heroCounts.GetValueOrDefault(row.Id),
                    LastLogout = lastLogoutMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastLogoutMs).LocalDateTime : null,
                    Banned = banned,
                    UserLevel = (AccountUserLevel)row.UserLevel,
                });
            }
            return hits;
        }

        // Regions that have at least one start location, for the location filter.
        public List<(ulong Guid, string Name)> GetRegions() => _resolver.HasGameData
            ? _resolver.GetStartLocations().Where(l => l.RegionGuid != 0).GroupBy(l => l.RegionGuid)
                .Select(g => (g.Key, g.First().RegionName)).OrderBy(r => r.RegionName).ToList()
            : new();

        public List<(ulong Guid, string Name)> GetHeroes()
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.Query<long>("SELECT DISTINCT EntityProtoGuid FROM Avatar")
                .Select(g => (unchecked((ulong)g), ShortName(_resolver.Format(unchecked((ulong)g))))).OrderBy(h => h.Item2).ToList();
        }

        public List<(ulong Guid, string Name)> GetRarities() => _resolver.HasGameData
            ? _resolver.Find().Where(e => e.IsAbstract == false && _resolver.GetRuntimeClass(e.Guid) == "RarityPrototype")
                .Select(e => (e.Guid, e.DisplayName)).OrderBy(r => r.DisplayName).ToList()
            : new();

        // Every item type in the Item table with its category (the folder under Entity/Items/,
        // e.g. "Rings") and how many exist. A quick GROUP BY - no blobs are read.
        public List<CatalogEntry> GetItemCatalog()
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.Query<(long Guid, int Count)>("SELECT EntityProtoGuid, COUNT(*) FROM Item GROUP BY EntityProtoGuid")
                .Select(x => new CatalogEntry(unchecked((ulong)x.Guid), ItemCategory(unchecked((ulong)x.Guid)), _resolver.Format(unchecked((ulong)x.Guid)), x.Count))
                .OrderBy(e => e.Group).ThenBy(e => e.Name).ToList();
        }

        // Every affix rolled on at least one item, grouped by its folder under Entity/Items/Affixes/.
        // Reads every item blob (a few seconds), so call it in the background.
        public List<CatalogEntry> GetAffixCatalog(CancellationToken cancel)
        {
            Dictionary<ulong, int> counts = new();
            using SQLiteConnection connection = _connectionManager.GetConnection();
            using SQLiteCommand command = new("SELECT ArchiveData FROM Item WHERE ArchiveData IS NOT NULL", connection);
            using SQLiteDataReader reader = command.ExecuteReader();
            long read = 0;
            while (reader.Read())
            {
                if (++read % 100000 == 0) cancel.ThrowIfCancellationRequested();
                ItemSpecRecord spec;
                try { spec = EntityArchiveCodec.Parse((byte[])reader[0]).ItemSpec; }
                catch (Exception) { continue; }
                if (spec == null) continue;
                foreach (AffixSpecRecord affix in spec.Affixes)
                    counts[affix.AffixProtoGuid] = counts.GetValueOrDefault(affix.AffixProtoGuid) + 1;
            }

            return counts.Select(kv =>
            {
                string path = _resolver.TryGetEntry(kv.Key, out PrototypeResolver.Entry entry) ? entry.Path ?? "" : "";
                string[] parts = path.Split('/');
                int at = Array.IndexOf(parts, "Affixes");
                string group = at >= 0 && at + 2 < parts.Length ? parts[at + 1] : "Other";
                return new CatalogEntry(kv.Key, group, _resolver.Format(kv.Key), kv.Value);
            }).OrderBy(e => e.Group).ThenBy(e => e.Name).ToList();
        }

        public List<(long Id, string Name)> GetPlayerNames()
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.Query<(long, string)>("SELECT Id, PlayerName FROM Account ORDER BY PlayerName COLLATE NOCASE").ToList();
        }

        private string ItemCategory(ulong guid)
        {
            if (_resolver.TryGetEntry(guid, out PrototypeResolver.Entry entry) == false || entry.Path == null) return "Other";
            string[] parts = entry.Path.Split('/');
            int at = Array.IndexOf(parts, "Items");
            return at >= 0 && at + 2 < parts.Length ? parts[at + 1] : "Other";
        }

        // Hero prototypes are named by path ("Avatars/Shipping/Cyclops"); the last part is the hero.
        private static string ShortName(string name) => name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;

    }
}
