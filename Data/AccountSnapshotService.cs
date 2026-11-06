using System;
using System.Collections.Generic;
using System.Data.SQLite;
using Dapper;
using MHDBEditor.Models;

namespace MHDBEditor.Data
{
    // Builds a full DBAccountSnapshot from the database (for export, or before cloning), and
    // writes a snapshot back (for import/restore). This is what backs the "Export Account" /
    // "Import Account" buttons.
    public class AccountSnapshotService
    {
        private readonly DbConnectionManager _connectionManager;
        private readonly EntityRepository _entityRepository;

        public AccountSnapshotService(DbConnectionManager connectionManager, EntityRepository entityRepository)
        {
            _connectionManager = connectionManager;
            _entityRepository = entityRepository;
        }

        public DBAccountSnapshot BuildSnapshot(DBAccount account, DBPlayer player)
        {
            DBAccountSnapshot snapshot = new()
            {
                Account = account,
                Player = player ?? new DBPlayer { DbGuid = account.Id },
            };

            snapshot.Avatars = _entityRepository.GetForContainer(DBEntityCategory.Avatar, account.Id);
            snapshot.TeamUps = _entityRepository.GetForContainer(DBEntityCategory.TeamUp, account.Id);

            snapshot.Items.AddRange(_entityRepository.GetForContainer(DBEntityCategory.Item, account.Id));
            foreach (DBEntity avatar in snapshot.Avatars)
            {
                snapshot.Items.AddRange(_entityRepository.GetForContainer(DBEntityCategory.Item, avatar.DbGuid));
                snapshot.ControlledEntities.AddRange(_entityRepository.GetForContainer(DBEntityCategory.ControlledEntity, avatar.DbGuid));
            }
            foreach (DBEntity teamUp in snapshot.TeamUps)
                snapshot.Items.AddRange(_entityRepository.GetForContainer(DBEntityCategory.Item, teamUp.DbGuid));

            return snapshot;
        }

        // Upserts everything in a snapshot into the database: the Account and Player rows,
        // and every Avatar/TeamUp/Item/ControlledEntity row. Ids in the snapshot are written
        // as-is - this does not remap anything (see CloneWithNewIds for that).
        public void ApplySnapshot(DBAccountSnapshot snapshot)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            using SQLiteTransaction transaction = connection.BeginTransaction();

            try
            {
                UpsertAccount(connection, transaction, snapshot.Account);
                UpsertPlayer(connection, transaction, snapshot.Player);
                DeleteOwnedEntities(connection, transaction, snapshot.Account.Id);

                UpsertEntities(connection, transaction, DBEntityCategory.Avatar, snapshot.Avatars);
                UpsertEntities(connection, transaction, DBEntityCategory.TeamUp, snapshot.TeamUps);
                UpsertEntities(connection, transaction, DBEntityCategory.Item, snapshot.Items);
                UpsertEntities(connection, transaction, DBEntityCategory.ControlledEntity, snapshot.ControlledEntities);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        // Produces a copy of a snapshot with a brand-new account id (and new ids for every
        // avatar/team-up/item/controlled-entity, generated via IdGenerator), remapping every
        // ContainerDbGuid so the whole object graph still points at the right (new) parents.
        // Does NOT write anything to the database by itself - pass the result to
        // ApplySnapshot() to actually create the new account.
        public DBAccountSnapshot CloneWithNewIds(DBAccountSnapshot source, long newAccountId, string newEmail, string newPlayerName)
        {
            Dictionary<long, long> idMap = new() { [source.Account.Id] = newAccountId };

            DBAccountSnapshot clone = new()
            {
                Account = new DBAccount
                {
                    Id = newAccountId,
                    Email = newEmail,
                    PlayerName = newPlayerName,
                    PasswordHash = source.Account.PasswordHash,
                    Salt = source.Account.Salt,
                    UserLevel = source.Account.UserLevel,
                    Flags = source.Account.Flags,
                    LastKnownMachineId = null,
                },
                Player = new DBPlayer
                {
                    DbGuid = newAccountId,
                    ArchiveData = source.Player.ArchiveData,
                    StartTarget = source.Player.StartTarget,
                    AOIVolume = source.Player.AOIVolume,
                    GazillioniteBalance = source.Player.GazillioniteBalance,
                    LastLogoutTime = source.Player.LastLogoutTime,
                    Flags = source.Player.Flags,
                },
            };

            // Avatars/team-ups are direct children of the account - remap first so idMap has
            // their new ids before we process items/controlled-entities that live under them.
            clone.Avatars = RemapContainer(source.Avatars, idMap, newAccountId);
            clone.TeamUps = RemapContainer(source.TeamUps, idMap, newAccountId);

            clone.Items = RemapChildren(source.Items, idMap);
            clone.ControlledEntities = RemapChildren(source.ControlledEntities, idMap);

            return clone;
        }

        private static List<DBEntity> RemapContainer(List<DBEntity> entities, Dictionary<long, long> idMap, long newParentContainerId)
        {
            List<DBEntity> result = new(entities.Count);
            foreach (DBEntity entity in entities)
            {
                long newId = IdGenerator.GenerateLike(entity.DbGuid);
                idMap[entity.DbGuid] = newId;

                result.Add(new DBEntity
                {
                    DbGuid = newId,
                    ContainerDbGuid = newParentContainerId,
                    InventoryProtoGuid = entity.InventoryProtoGuid,
                    Slot = entity.Slot,
                    EntityProtoGuid = entity.EntityProtoGuid,
                    ArchiveData = entity.ArchiveData,
                });
            }
            return result;
        }

        private static List<DBEntity> RemapChildren(List<DBEntity> entities, Dictionary<long, long> idMap)
        {
            List<DBEntity> result = new(entities.Count);
            foreach (DBEntity entity in entities)
            {
                long newId = IdGenerator.GenerateLike(entity.DbGuid);
                idMap[entity.DbGuid] = newId;

                long newContainer = idMap.TryGetValue(entity.ContainerDbGuid, out long mapped) ? mapped : entity.ContainerDbGuid;

                result.Add(new DBEntity
                {
                    DbGuid = newId,
                    ContainerDbGuid = newContainer,
                    InventoryProtoGuid = entity.InventoryProtoGuid,
                    Slot = entity.Slot,
                    EntityProtoGuid = entity.EntityProtoGuid,
                    ArchiveData = entity.ArchiveData,
                });
            }
            return result;
        }

        private static void UpsertAccount(SQLiteConnection connection, SQLiteTransaction transaction, DBAccount account)
        {
            // Stock MHServerEmu has no LastKnownMachineId column; only write it where it exists.
            bool machineId = DbConnectionManager.GetColumns(connection, "Account").Contains("LastKnownMachineId");
            int updated = connection.Execute(
                @"UPDATE Account SET Email=@Email, PlayerName=@PlayerName, PasswordHash=@PasswordHash, Salt=@Salt,
                  UserLevel=@UserLevel, Flags=@Flags" + (machineId ? ", LastKnownMachineId=@LastKnownMachineId" : "") + " WHERE Id=@Id",
                account, transaction);

            if (updated == 0)
            {
                connection.Execute(
                    "INSERT INTO Account (Id, Email, PlayerName, PasswordHash, Salt, UserLevel, Flags" + (machineId ? ", LastKnownMachineId" : "") + ") " +
                    "VALUES (@Id, @Email, @PlayerName, @PasswordHash, @Salt, @UserLevel, @Flags" + (machineId ? ", @LastKnownMachineId" : "") + ")",
                    account, transaction);
            }
        }

        private static void UpsertPlayer(SQLiteConnection connection, SQLiteTransaction transaction, DBPlayer player)
        {
            connection.Execute("INSERT OR IGNORE INTO Player (DbGuid) VALUES (@DbGuid)", player, transaction);
            bool flags = DbConnectionManager.GetColumns(connection, "Player").Contains("Flags");   // stock only
            connection.Execute(
                @"UPDATE Player SET ArchiveData=@ArchiveData, StartTarget=@StartTarget, AOIVolume=@AOIVolume,
                  GazillioniteBalance=@GazillioniteBalance, LastLogoutTime=@LastLogoutTime" + (flags ? ", Flags=@Flags" : "") + " WHERE DbGuid=@DbGuid",
                player, transaction);
        }

        // Restoring over an existing account replaces it: anything it got after the export goes,
        // otherwise it would sit next to the restored copies (shared slots, merged inventories).
        private static void DeleteOwnedEntities(SQLiteConnection connection, SQLiteTransaction transaction, long accountId)
        {
            List<long> containers = connection.Query<long>(
                "SELECT DbGuid FROM Avatar WHERE ContainerDbGuid=@Id UNION SELECT DbGuid FROM TeamUp WHERE ContainerDbGuid=@Id",
                new { Id = accountId }, transaction).AsList();
            containers.Add(accountId);
            foreach (string table in new[] { "Item", "ControlledEntity", "Avatar", "TeamUp" })
                connection.Execute($"DELETE FROM {table} WHERE ContainerDbGuid IN @Containers", new { Containers = containers }, transaction);
        }

        private static void UpsertEntities(SQLiteConnection connection, SQLiteTransaction transaction, DBEntityCategory category, List<DBEntity> entities)
        {
            foreach (DBEntity entity in entities)
            {
                connection.Execute(
                    $@"INSERT OR REPLACE INTO {category} (DbGuid, ContainerDbGuid, InventoryProtoGuid, Slot, EntityProtoGuid, ArchiveData)
                       VALUES (@DbGuid, @ContainerDbGuid, @InventoryProtoGuid, @Slot, @EntityProtoGuid, @ArchiveData)",
                    entity, transaction);
            }
        }
    }
}
