using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using Dapper;
using MHDBEditor.Forms;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Data
{
    // Changes applied to many players / items at once. Each call runs in one transaction, so it
    // either all lands or none of it does. Players must be logged out: the server writes its
    // in-memory copy back on logout and would undo these changes for anyone online.
    public class BulkActionService
    {
        private readonly DbConnectionManager _connectionManager;
        private readonly PrototypeResolver _resolver;

        public BulkActionService(DbConnectionManager connectionManager, PrototypeResolver resolver)
        {
            _connectionManager = connectionManager;
            _resolver = resolver;
        }

        // Takes items back. Items are leaf rows (nothing is stored inside an item), so deleting
        // the row is all the server does when an item is destroyed.
        public int DeleteItems(IEnumerable<long> itemDbGuids)
        {
            return InTransaction((connection, transaction) =>
                itemDbGuids.Distinct().Sum(id => connection.Execute("DELETE FROM Item WHERE DbGuid = @Id", new { Id = id }, transaction)));
        }

        // Puts a copy of the template item in each player's inventory the template came from
        // (normally the stash), in the next free slot - the same thing Simple Mode's Give Item does.
        public int GiveItem(ItemTemplate template, IEnumerable<long> accountIds)
        {
            return InTransaction((connection, transaction) =>
            {
                int given = 0;
                foreach (long accountId in accountIds.Distinct())
                {
                    uint? maxSlot = connection.QueryFirstOrDefault<uint?>(
                        "SELECT MAX(Slot) FROM Item WHERE ContainerDbGuid = @Container AND InventoryProtoGuid = @Inventory",
                        new { Container = accountId, Inventory = template.InventoryProtoGuid }, transaction);
                    DBEntity item = new()
                    {
                        DbGuid = IdGenerator.GenerateEntity(accountId),
                        ContainerDbGuid = accountId,
                        InventoryProtoGuid = template.InventoryProtoGuid,
                        Slot = maxSlot.HasValue ? maxSlot.Value + 1 : 0,
                        EntityProtoGuid = template.EntityProtoGuid,
                        ArchiveData = template.ArchiveData,
                    };
                    given += connection.Execute(
                        @"INSERT INTO Item (DbGuid, ContainerDbGuid, InventoryProtoGuid, Slot, EntityProtoGuid, ArchiveData)
                          VALUES (@DbGuid, @ContainerDbGuid, @InventoryProtoGuid, @Slot, @EntityProtoGuid, @ArchiveData)", item, transaction);
                }
                return given;
            });
        }

        // Adds Gazillionite; -1 ("not set up yet") counts as 0, like Simple Mode.
        public int GiveGazillionite(IEnumerable<long> accountIds, long amount)
        {
            return InTransaction((connection, transaction) => accountIds.Distinct().Sum(id => connection.Execute(
                "UPDATE Player SET GazillioniteBalance = MAX(COALESCE(GazillioniteBalance, 0), 0) + @Amount WHERE DbGuid = @Id",
                new { Amount = amount, Id = id }, transaction)));
        }

        // Sends players to a start location on their next login, optionally also making it
        // their last town (the same two changes as the Change Location button). Returns how many
        // players moved and how many also got the last town.
        public (int Moved, int LastTown) MovePlayers(IEnumerable<long> accountIds, PrototypeResolver.StartLocation location, bool alsoSetLastTown)
        {
            int lastTowns = 0;
            int moved = InTransaction((connection, transaction) =>
            {
                int count = 0;
                foreach (long accountId in accountIds.Distinct())
                {
                    count += connection.Execute("UPDATE Player SET StartTarget = @Target WHERE DbGuid = @Id",
                        new { Target = unchecked((long)location.DataRef), Id = accountId }, transaction);
                    if (alsoSetLastTown == false || location.IsTown == false) continue;

                    byte[] archive = connection.QueryFirstOrDefault<byte[]>("SELECT ArchiveData FROM Player WHERE DbGuid = @Id", new { Id = accountId }, transaction);
                    byte[] updated = StartLocationPickerForm.WithLastTown(_resolver, archive, location);
                    if (updated == null) continue;
                    lastTowns += connection.Execute("UPDATE Player SET ArchiveData = @Data WHERE DbGuid = @Id", new { Data = updated, Id = accountId }, transaction);
                }
                return count;
            });
            return (moved, lastTowns);
        }

        public int SetBanned(IEnumerable<long> accountIds, bool banned)
        {
            string sql = banned ? "UPDATE Account SET Flags = Flags | @Flag WHERE Id = @Id" : "UPDATE Account SET Flags = Flags & ~@Flag WHERE Id = @Id";
            return InTransaction((connection, transaction) => accountIds.Distinct().Sum(id =>
                connection.Execute(sql, new { Flag = (long)AccountFlags.IsBanned, Id = id }, transaction)));
        }

        private T InTransaction<T>(Func<SQLiteConnection, SQLiteTransaction, T> action)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            using SQLiteTransaction transaction = connection.BeginTransaction();
            T result = action(connection, transaction);
            transaction.Commit();
            return result;
        }
    }
}
