using System;
using System.Collections.Generic;
using System.Data.SQLite;
using Dapper;
using MHDBEditor.Models;

namespace MHDBEditor.Data
{
    // Generic access to the Avatar / TeamUp / Item / ControlledEntity tables. All four share
    // the exact same DBEntity shape and are only ever addressed by table name - category.ToString()
    // is used as the literal table name, same as the server's own SQLiteEntityTable does, so it's
    // only ever fed a DBEntityCategory enum value, never free-form user input.
    public class EntityRepository
    {
        private readonly DbConnectionManager _connectionManager;

        public EntityRepository(DbConnectionManager connectionManager)
        {
            _connectionManager = connectionManager;
        }

        public List<DBEntity> GetForContainer(DBEntityCategory category, long containerDbGuid)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.Query<DBEntity>(
                $"SELECT * FROM {category} WHERE ContainerDbGuid = @ContainerDbGuid",
                new { ContainerDbGuid = containerDbGuid }).AsList();
        }

        public DBEntity GetByGuid(DBEntityCategory category, long dbGuid)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.QueryFirstOrDefault<DBEntity>(
                $"SELECT * FROM {category} WHERE DbGuid = @DbGuid", new { DbGuid = dbGuid });
        }

        // Moves an entity to a different container/inventory/slot. Safe to do without touching
        // ArchiveData - covers "move item to a stash tab", re-slotting, unequipping, etc., as
        // long as the target InventoryProtoGuid is a real inventory the game knows about (this
        // tool can't validate that yet - see README).
        public void Move(DBEntityCategory category, long dbGuid, long newContainerDbGuid, long newInventoryProtoGuid, uint newSlot)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                $@"UPDATE {category} SET ContainerDbGuid=@ContainerDbGuid, InventoryProtoGuid=@InventoryProtoGuid, Slot=@Slot
                   WHERE DbGuid=@DbGuid",
                new { ContainerDbGuid = newContainerDbGuid, InventoryProtoGuid = newInventoryProtoGuid, Slot = newSlot, DbGuid = dbGuid });
        }

        public void Delete(DBEntityCategory category, long dbGuid)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute($"DELETE FROM {category} WHERE DbGuid = @DbGuid", new { DbGuid = dbGuid });
        }

        // Clones an existing row (including its ArchiveData verbatim) into a new row with a
        // fresh DbGuid at the given destination. Because ArchiveData is copied byte-for-byte
        // rather than reconstructed, the clone is a faithful copy of the source item/entity's
        // full runtime state (affixes, rank, etc.) even though this tool can't read that state.
        public DBEntity Duplicate(DBEntityCategory category, long sourceDbGuid, long destContainerDbGuid, long destInventoryProtoGuid, uint destSlot)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();

            DBEntity source = connection.QueryFirstOrDefault<DBEntity>(
                $"SELECT * FROM {category} WHERE DbGuid = @DbGuid", new { DbGuid = sourceDbGuid });
            if (source == null)
                throw new InvalidOperationException($"No {category} row found for DbGuid 0x{sourceDbGuid:X}");

            DBEntity clone = new()
            {
                DbGuid = IdGenerator.GenerateLike(sourceDbGuid),
                ContainerDbGuid = destContainerDbGuid,
                InventoryProtoGuid = destInventoryProtoGuid,
                Slot = destSlot,
                EntityProtoGuid = source.EntityProtoGuid,
                ArchiveData = source.ArchiveData,
            };

            Insert(category, clone);

            return clone;
        }

        // Inserts a fully-formed row - e.g. one built from an in-memory item template rather
        // than copied from an existing DB row (see Duplicate() for that case). Caller is
        // responsible for DbGuid being unique (IdGenerator.GenerateEntity() is the usual source).
        public void Insert(DBEntityCategory category, DBEntity entity)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                $@"INSERT INTO {category} (DbGuid, ContainerDbGuid, InventoryProtoGuid, Slot, EntityProtoGuid, ArchiveData)
                   VALUES (@DbGuid, @ContainerDbGuid, @InventoryProtoGuid, @Slot, @EntityProtoGuid, @ArchiveData)",
                entity);
        }

        // Returns a slot number that isn't already used by another row in the same
        // container+inventory - one past whatever the highest existing slot is there, or 0 if
        // that inventory is currently empty. Used when placing a new item somewhere without a
        // specific slot in mind (e.g. "give this item to this player's stash").
        public uint GetNextFreeSlot(DBEntityCategory category, long containerDbGuid, long inventoryProtoGuid)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            uint? maxSlot = connection.QueryFirstOrDefault<uint?>(
                $"SELECT MAX(Slot) FROM {category} WHERE ContainerDbGuid = @ContainerDbGuid AND InventoryProtoGuid = @InventoryProtoGuid",
                new { ContainerDbGuid = containerDbGuid, InventoryProtoGuid = inventoryProtoGuid });
            return maxSlot.HasValue ? maxSlot.Value + 1 : 0;
        }

        public void SetRawArchiveData(DBEntityCategory category, long dbGuid, byte[] archiveData)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                $"UPDATE {category} SET ArchiveData = @ArchiveData WHERE DbGuid = @DbGuid",
                new { ArchiveData = archiveData, DbGuid = dbGuid });
        }
    }
}
