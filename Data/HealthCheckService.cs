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
    // Finds rows the server will ignore, skip or silently wipe, based on how it actually loads
    // an account (SQLiteDBManager.LoadPlayerData + PersistenceUtility.RestoreInventoryEntities):
    //   Avatar / TeamUp      rows are loaded only when ContainerDbGuid = the account id
    //   Item                 rows only under the account, one of its avatars, or one of its team-ups
    //   ControlledEntity     rows only under one of its avatars
    // Anything else is never read again. Rows that are loaded but whose entity/inventory
    // prototype isn't in the game data are skipped at login (RestoreContainer) and then deleted
    // by the next save, because SQLiteEntityTable.UpdateEntities removes ids that are no longer
    // in the live collection.
    public class HealthCheckService
    {
        public enum Severity { Info, Warning, Problem }

        public enum IssueKind
        {
            Unreachable,
            WipedOnNextSave,
            SlotCollision,
            UnreadableData,
            BrokenAffix,
            BadStartLocation,
            GuildMemberWithoutPlayer,
            GuildMemberWithoutGuild,
            EmptyGuild,
            PlayerWithoutAccount,
        }

        public class IssueRow
        {
            public string Table { get; init; }
            public long DbGuid { get; init; }
            public string Owner { get; init; }
            public string What { get; init; }
            public string Detail { get; init; }
            public long GuildId { get; init; }   // guild issues: the one membership row to remove
        }

        public class Issue
        {
            public IssueKind Kind { get; init; }
            public Severity Severity { get; init; }
            public string Title { get; init; }
            public string Explanation { get; init; }
            public string FixDescription { get; init; }   // null = report only
            public List<IssueRow> Rows { get; } = new();
            public bool CanFix => FixDescription != null;
        }

        public class Report
        {
            public List<Issue> Issues { get; } = new();
            public Dictionary<string, long> RowCounts { get; } = new();
            public bool UsedGameData { get; init; }
            public TimeSpan Elapsed { get; set; }
        }

        private record EntityRow(long DbGuid, long ContainerDbGuid, long InventoryProtoGuid, long Slot, long EntityProtoGuid);

        private readonly DbConnectionManager _connectionManager;
        private readonly PrototypeResolver _resolver;

        public HealthCheckService(DbConnectionManager connectionManager, PrototypeResolver resolver)
        {
            _connectionManager = connectionManager;
            _resolver = resolver;
        }

        public Report Scan(IProgress<string> progress, CancellationToken cancel)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool gameData = _resolver.HasGameData;
            Report report = new() { UsedGameData = gameData };
            using SQLiteConnection connection = _connectionManager.GetConnection();

            progress?.Report("Reading accounts...");
            Dictionary<long, string> accounts = connection.Query<(long Id, string PlayerName)>("SELECT Id, PlayerName FROM Account")
                .ToDictionary(a => a.Id, a => a.PlayerName);

            Dictionary<DBEntityCategory, List<EntityRow>> tables = new();
            foreach (DBEntityCategory category in Enum.GetValues<DBEntityCategory>())
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report($"Reading {category} rows...");
                tables[category] = connection.Query<EntityRow>(
                    $"SELECT DbGuid, ContainerDbGuid, InventoryProtoGuid, Slot, EntityProtoGuid FROM {category}").AsList();
                report.RowCounts[category.ToString()] = tables[category].Count;
            }
            report.RowCounts["Account"] = accounts.Count;

            // --- Reachability, mirroring the server's load order ---
            Dictionary<long, long> ownerOf = new();   // loaded avatar/team-up DbGuid -> account id
            foreach (EntityRow row in tables[DBEntityCategory.Avatar].Concat(tables[DBEntityCategory.TeamUp]))
                if (accounts.ContainsKey(row.ContainerDbGuid))
                    ownerOf[row.DbGuid] = row.ContainerDbGuid;
            HashSet<long> avatars = tables[DBEntityCategory.Avatar].Where(r => ownerOf.ContainsKey(r.DbGuid)).Select(r => r.DbGuid).ToHashSet();

            string OwnerName(long container)
            {
                if (accounts.TryGetValue(container, out string name)) return name;
                if (ownerOf.TryGetValue(container, out long account) && accounts.TryGetValue(account, out name)) return name;
                return null;
            }

            bool IsLoaded(DBEntityCategory category, EntityRow row) => category switch
            {
                DBEntityCategory.Avatar or DBEntityCategory.TeamUp => accounts.ContainsKey(row.ContainerDbGuid),
                DBEntityCategory.Item => accounts.ContainsKey(row.ContainerDbGuid) || ownerOf.ContainsKey(row.ContainerDbGuid),
                DBEntityCategory.ControlledEntity => avatars.Contains(row.ContainerDbGuid),
                _ => true,
            };

            Issue unreachable = new()
            {
                Kind = IssueKind.Unreachable,
                Severity = Severity.Warning,
                Title = "Rows the server never loads (orphaned)",
                Explanation = "Their container isn't an account, or a hero/team-up of one, so no login ever reads them. " +
                    "Usually gear left behind when the server deleted a hero row: it removes the hero but never the items stored under it.",
                FixDescription = "Delete these rows",
            };
            Issue wiped = new()
            {
                Kind = IssueKind.WipedOnNextSave,
                Severity = Severity.Problem,
                Title = "Loaded but skipped: wiped on the player's next save",
                Explanation = "The entity or inventory prototype isn't in the loaded game data. At login the server skips the row " +
                    "(RestoreContainer), then deletes it on the next save. If you removed a custom item/hero, put it back before these players log in.",
            };
            Issue collisions = new()
            {
                Kind = IssueKind.SlotCollision,
                Severity = Severity.Problem,
                Title = "Two entities in the same inventory slot",
                Explanation = "Only one can be placed at login; the other isn't restored and is deleted on the next save. Move one with Manage Items/Entities.",
            };

            HashSet<long> notLoaded = new();
            foreach (var (category, rows) in tables)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report($"Checking {category} rows...");
                foreach (EntityRow row in rows)
                {
                    string owner = OwnerName(row.ContainerDbGuid);
                    if (IsLoaded(category, row) == false)
                    {
                        notLoaded.Add(row.DbGuid);
                        unreachable.Rows.Add(new IssueRow
                        {
                            Table = category.ToString(),
                            DbGuid = row.DbGuid,
                            Owner = "(none)",
                            What = Name(row.EntityProtoGuid),
                            Detail = $"in {Name(row.InventoryProtoGuid)} of missing container 0x{row.ContainerDbGuid:X}",
                        });
                        continue;
                    }

                    if (gameData)
                    {
                        bool entityKnown = _resolver.TryGetEntry(unchecked((ulong)row.EntityProtoGuid), out _);
                        bool inventoryKnown = _resolver.TryGetEntry(unchecked((ulong)row.InventoryProtoGuid), out _);
                        if (entityKnown == false || inventoryKnown == false)
                        {
                            wiped.Rows.Add(new IssueRow
                            {
                                Table = category.ToString(),
                                DbGuid = row.DbGuid,
                                Owner = owner,
                                What = entityKnown ? Name(row.EntityProtoGuid) : $"unknown prototype 0x{row.EntityProtoGuid:X}",
                                Detail = inventoryKnown ? $"in {Name(row.InventoryProtoGuid)}" : $"in unknown inventory 0x{row.InventoryProtoGuid:X}",
                            });
                        }
                    }
                }

                foreach (var group in rows.Where(r => IsLoaded(category, r)).GroupBy(r => (r.ContainerDbGuid, r.InventoryProtoGuid, r.Slot)).Where(g => g.Count() > 1))
                {
                    foreach (EntityRow row in group)
                    {
                        collisions.Rows.Add(new IssueRow
                        {
                            Table = category.ToString(),
                            DbGuid = row.DbGuid,
                            Owner = OwnerName(row.ContainerDbGuid),
                            What = Name(row.EntityProtoGuid),
                            Detail = $"{Name(row.InventoryProtoGuid)} slot {row.Slot} ({group.Count()} share it)",
                        });
                    }
                }
            }

            // --- Archive blobs: readability + affixes that break an item ---
            Issue unreadable = new()
            {
                Kind = IssueKind.UnreadableData,
                Severity = Severity.Warning,
                Title = "ArchiveData the editor can't decode",
                Explanation = "Could be corrupt, or a format this editor doesn't understand. The server may still load it - check the server log for this player.",
            };
            Issue brokenAffix = new()
            {
                Kind = IssueKind.BrokenAffix,
                Severity = Severity.Problem,
                Title = "Items with an affix that stops the rest applying",
                Explanation = "A saved affix with seed 0 or a prototype that isn't an affix makes Item.ApplyItemSpec stop - that affix and every one after it never apply. " +
                    "The fix gives seed-0 affixes a random seed; a wrong prototype has to be fixed by hand in the item's Item Spec tab.",
                FixDescription = "Give seed-0 affixes a random seed",
            };

            Dictionary<long, (long Container, string Category)> rowInfo = new();
            foreach (var (category, rows) in tables)
                foreach (EntityRow row in rows)
                    rowInfo[row.DbGuid] = (row.ContainerDbGuid, category.ToString());

            foreach (DBEntityCategory category in Enum.GetValues<DBEntityCategory>())
            {
                long done = 0, total = report.RowCounts[category.ToString()];
                foreach (var (dbGuid, data) in connection.Query<(long DbGuid, byte[] ArchiveData)>($"SELECT DbGuid, ArchiveData FROM {category}", buffered: false))
                {
                    if (++done % 50000 == 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        progress?.Report($"Decoding {category} data... {done:N0} / {total:N0}");
                    }

                    if (notLoaded.Contains(dbGuid)) continue;   // already listed as orphaned
                    string owner = rowInfo.TryGetValue(dbGuid, out var info) ? OwnerName(info.Container) : null;
                    ParsedEntityArchive parsed;
                    try
                    {
                        parsed = EntityArchiveCodec.Parse(data);
                    }
                    catch (Exception ex)
                    {
                        unreadable.Rows.Add(new IssueRow { Table = category.ToString(), DbGuid = dbGuid, Owner = owner, What = $"{data?.Length ?? 0} bytes", Detail = ex.Message });
                        continue;
                    }

                    if (gameData && parsed.ItemSpec is ItemSpecRecord spec)
                    {
                        for (int i = 0; i < spec.Affixes.Count; i++)
                        {
                            AffixSpecRecord affix = spec.Affixes[i];
                            PrototypeResolver.AffixReport affixReport;
                            try { affixReport = _resolver.GetAffixReport(affix.AffixProtoGuid, affix.ScopeProtoGuid, spec.EquippableByGuid, spec.ItemLevel, affix.Seed, true); }
                            catch (Exception) { continue; }
                            if (affixReport?.Health != PrototypeResolver.AffixHealth.BreaksItem) continue;

                            brokenAffix.Rows.Add(new IssueRow
                            {
                                Table = category.ToString(),
                                DbGuid = dbGuid,
                                Owner = owner,
                                What = _resolver.Format(spec.ItemProtoGuid),
                                Detail = $"affix #{i + 1} of {spec.Affixes.Count} ({_resolver.Format(affix.AffixProtoGuid)}): {affixReport.Problems.FirstOrDefault()}",
                            });
                            break;
                        }
                    }
                }
            }

            // --- Players: start location + unreadable player archives ---
            Issue badStart = new()
            {
                Kind = IssueKind.BadStartLocation,
                Severity = Severity.Problem,
                Title = "Players whose start location is broken",
                Explanation = "Player.StartTarget doesn't resolve to a usable start location, so the player lands on the default start or gets stuck loading.",
                FixDescription = "Send them to the server fallback start (and set last town)",
            };
            progress?.Report("Checking players...");
            foreach (var (dbGuid, startTarget, data) in connection.Query<(long DbGuid, long StartTarget, byte[] ArchiveData)>("SELECT DbGuid, StartTarget, ArchiveData FROM Player", buffered: false))
            {
                accounts.TryGetValue(dbGuid, out string name);
                if (data != null && data.Length > 0)
                {
                    try { EntityArchiveCodec.Parse(data); }
                    catch (Exception ex) { unreadable.Rows.Add(new IssueRow { Table = "Player", DbGuid = dbGuid, Owner = name, What = $"{data.Length} bytes", Detail = ex.Message }); }
                }

                if (gameData)
                {
                    var (text, ok) = _resolver.DescribeStartTarget(startTarget);
                    if (ok == false)
                        badStart.Rows.Add(new IssueRow { Table = "Player", DbGuid = dbGuid, Owner = name, What = startTarget.ToString(), Detail = text });
                }
            }

            // --- Guilds ---
            progress?.Report("Checking guilds...");
            Issue memberNoPlayer = new()
            {
                Kind = IssueKind.GuildMemberWithoutPlayer,
                Severity = Severity.Warning,
                Title = "Guild members whose account no longer exists",
                Explanation = "They still count toward the guild's roster.",
                FixDescription = "Remove them from the guild",
            };
            foreach (var (player, guildId) in connection.Query<(long PlayerDbGuid, long GuildId)>(
                "SELECT PlayerDbGuid, GuildId FROM GuildMember WHERE PlayerDbGuid NOT IN (SELECT Id FROM Account)"))
                memberNoPlayer.Rows.Add(new IssueRow { Table = "GuildMember", DbGuid = player, Owner = "(none)", What = $"guild {guildId}", Detail = "account missing", GuildId = guildId });

            Issue playerNoAccount = new()
            {
                Kind = IssueKind.PlayerWithoutAccount,
                Severity = Severity.Warning,
                Title = "Player rows whose account no longer exists",
                Explanation = "Left over from a deleted account. Nothing can log in to them.",
                FixDescription = "Delete these Player rows",
            };
            foreach (long dbGuid in connection.Query<long>("SELECT DbGuid FROM Player WHERE DbGuid NOT IN (SELECT Id FROM Account)"))
                playerNoAccount.Rows.Add(new IssueRow { Table = "Player", DbGuid = dbGuid, Owner = "(none)", What = $"0x{dbGuid:X}", Detail = "account missing" });

            Issue memberNoGuild = new()
            {
                Kind = IssueKind.GuildMemberWithoutGuild,
                Severity = Severity.Warning,
                Title = "Guild memberships pointing at a deleted guild",
                Explanation = "The player shows as in a guild that doesn't exist.",
                FixDescription = "Remove these memberships",
            };
            foreach (var (player, guildId) in connection.Query<(long PlayerDbGuid, long GuildId)>(
                "SELECT PlayerDbGuid, GuildId FROM GuildMember WHERE GuildId NOT IN (SELECT Id FROM Guild)"))
                memberNoGuild.Rows.Add(new IssueRow { Table = "GuildMember", DbGuid = player, Owner = accounts.GetValueOrDefault(player), What = $"guild {guildId}", Detail = "guild row missing", GuildId = guildId });

            Issue emptyGuild = new()
            {
                Kind = IssueKind.EmptyGuild,
                Severity = Severity.Info,
                Title = "Guilds with no members",
                Explanation = "Harmless, but the name stays taken. Delete from the Guilds tab if you want it back.",
            };
            foreach (var (id, guildName) in connection.Query<(long Id, string Name)>("SELECT Id, Name FROM Guild WHERE Id NOT IN (SELECT GuildId FROM GuildMember)"))
                emptyGuild.Rows.Add(new IssueRow { Table = "Guild", DbGuid = id, Owner = "-", What = guildName, Detail = "" });

            foreach (Issue issue in new[] { wiped, collisions, brokenAffix, badStart, unreachable, unreadable, memberNoGuild, memberNoPlayer, playerNoAccount, emptyGuild })
                if (issue.Rows.Count > 0)
                    report.Issues.Add(issue);

            report.Elapsed = timer.Elapsed;
            return report;
        }

        // Applies the fixes for the given issues in one transaction. Returns a line per issue.
        public List<string> Fix(IEnumerable<Issue> issues, IProgress<string> progress)
        {
            List<string> results = new();
            using SQLiteConnection connection = _connectionManager.GetConnection();
            using SQLiteTransaction transaction = connection.BeginTransaction();

            foreach (Issue issue in issues.Where(i => i.CanFix))
            {
                progress?.Report($"Fixing: {issue.Title}...");
                int changed = 0;
                switch (issue.Kind)
                {
                    case IssueKind.Unreachable:
                        foreach (var table in issue.Rows.GroupBy(r => r.Table))
                        {
                            if (Enum.TryParse(table.Key, out DBEntityCategory category) == false) continue;
                            foreach (long[] chunk in table.Select(r => r.DbGuid).Chunk(500))
                                changed += connection.Execute($"DELETE FROM {category} WHERE DbGuid IN @Ids", new { Ids = chunk }, transaction);
                        }
                        results.Add($"Deleted {changed:N0} orphaned rows.");
                        break;

                    case IssueKind.BadStartLocation:
                        PrototypeResolver.StartLocation safe = _resolver.GetStartLocations().FirstOrDefault(l => l.Note?.Contains("Server fallback") == true)
                            ?? _resolver.GetStartLocations().FirstOrDefault(l => l.Note?.Contains("Server default") == true);
                        if (safe == null) { results.Add("Start locations: no fallback start found in the game data - nothing changed."); break; }
                        int lastTowns = 0;
                        foreach (IssueRow row in issue.Rows)
                        {
                            changed += connection.Execute("UPDATE Player SET StartTarget=@StartTarget WHERE DbGuid=@DbGuid",
                                new { StartTarget = unchecked((long)safe.DataRef), row.DbGuid }, transaction);
                            byte[] data = connection.ExecuteScalar<byte[]>("SELECT ArchiveData FROM Player WHERE DbGuid=@DbGuid", new { row.DbGuid }, transaction);
                            byte[] updated = Forms.StartLocationPickerForm.WithLastTown(_resolver, data, safe);
                            if (updated != null)
                                lastTowns += connection.Execute("UPDATE Player SET ArchiveData=@ArchiveData WHERE DbGuid=@DbGuid", new { ArchiveData = updated, row.DbGuid }, transaction);
                        }
                        results.Add($"Moved {changed:N0} player(s) to {safe} ({lastTowns:N0} last town(s) updated).");
                        break;

                    case IssueKind.BrokenAffix:
                        int stillBroken = 0;
                        foreach (IssueRow row in issue.Rows)
                        {
                            if (Enum.TryParse(row.Table, out DBEntityCategory category) == false) continue;
                            byte[] data = connection.ExecuteScalar<byte[]>($"SELECT ArchiveData FROM {category} WHERE DbGuid=@DbGuid", new { row.DbGuid }, transaction);
                            ParsedEntityArchive parsed;
                            try { parsed = EntityArchiveCodec.Parse(data); } catch (Exception) { stillBroken++; continue; }
                            if (parsed.ItemSpec == null) { stillBroken++; continue; }

                            int reseeded = 0;
                            foreach (AffixSpecRecord affix in parsed.ItemSpec.Affixes.Where(a => a.Seed == 0))
                            {
                                affix.Seed = Random.Shared.Next(1, int.MaxValue);
                                reseeded++;
                            }
                            if (reseeded == 0) { stillBroken++; continue; }

                            connection.Execute($"UPDATE {category} SET ArchiveData=@ArchiveData WHERE DbGuid=@DbGuid", new { ArchiveData = parsed.Rebuild(), row.DbGuid }, transaction);
                            changed++;
                        }
                        results.Add($"Re-seeded affixes on {changed:N0} item(s)." + (stillBroken > 0 ? $" {stillBroken:N0} need a hand fix (wrong prototype, not seed 0)." : ""));
                        break;

                    case IssueKind.GuildMemberWithoutPlayer:
                    case IssueKind.GuildMemberWithoutGuild:
                        foreach (IssueRow row in issue.Rows)
                            changed += connection.Execute("DELETE FROM GuildMember WHERE PlayerDbGuid=@DbGuid AND GuildId=@GuildId", new { row.DbGuid, row.GuildId }, transaction);
                        results.Add($"Removed {changed:N0} guild membership(s).");
                        break;

                    case IssueKind.PlayerWithoutAccount:
                        foreach (IssueRow row in issue.Rows)
                            changed += connection.Execute("DELETE FROM Player WHERE DbGuid=@DbGuid AND DbGuid NOT IN (SELECT Id FROM Account)", new { row.DbGuid }, transaction);
                        results.Add($"Deleted {changed:N0} leftover Player row(s).");
                        break;
                }
            }

            transaction.Commit();
            return results;
        }

        public void Compact(IProgress<string> progress)
        {
            progress?.Report("Compacting database (VACUUM)... this can take a few minutes on a large file.");
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("VACUUM");
        }

        private string Name(long protoGuid) => _resolver.HasGameData || _resolver.Count > 0 ? _resolver.Format(unchecked((ulong)protoGuid)) : $"0x{protoGuid:X}";
    }
}
