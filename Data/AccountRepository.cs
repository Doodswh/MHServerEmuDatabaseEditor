using System;
using System.Collections.Generic;
using System.Data.SQLite;
using Dapper;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Data
{
    public class AccountRepository
    {
        private readonly DbConnectionManager _connectionManager;

        public AccountRepository(DbConnectionManager connectionManager)
        {
            _connectionManager = connectionManager;
        }

        public List<DBAccount> Search(string term)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();

            if (string.IsNullOrWhiteSpace(term))
                return connection.Query<DBAccount>("SELECT * FROM Account ORDER BY PlayerName").AsList();

            string pattern = $"%{term}%";
            return connection.Query<DBAccount>(
                @"SELECT * FROM Account
                  WHERE Email LIKE @Pattern COLLATE NOCASE
                     OR PlayerName LIKE @Pattern COLLATE NOCASE
                     OR Id IN @Ids
                  ORDER BY PlayerName",
                new { Pattern = pattern, Ids = IdsFromText(term) }).AsList();
        }

        // The grid shows ids as hex without 0x, so accept decimal, 0x hex and bare hex.
        private static List<long> IdsFromText(string term)
        {
            term = term.Trim();
            List<long> ids = new() { long.MinValue };   // never matches; keeps IN () valid
            try { ids.Add(GuidTextHelper.Parse(term)); } catch (Exception) { }
            if (ulong.TryParse(term, System.Globalization.NumberStyles.HexNumber, null, out ulong hex)) ids.Add(unchecked((long)hex));
            return ids;
        }

        public DBAccount GetById(long id)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.QueryFirstOrDefault<DBAccount>("SELECT * FROM Account WHERE Id = @Id", new { Id = id });
        }

        public DBPlayer GetPlayer(long accountId)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.QueryFirstOrDefault<DBPlayer>("SELECT * FROM Player WHERE DbGuid = @Id", new { Id = accountId });
        }

        public void UpdateFlags(long accountId, AccountFlags flags)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("UPDATE Account SET Flags = @Flags WHERE Id = @Id", new { Flags = flags, Id = accountId });
        }

        public void UpdateUserLevel(long accountId, AccountUserLevel level)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("UPDATE Account SET UserLevel = @UserLevel WHERE Id = @Id", new { UserLevel = level, Id = accountId });
        }

        public void Rename(long accountId, string newPlayerName)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("UPDATE Account SET PlayerName = @PlayerName WHERE Id = @Id", new { PlayerName = newPlayerName, Id = accountId });
        }

        public void UpdateEmail(long accountId, string newEmail)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("UPDATE Account SET Email = @Email WHERE Id = @Id", new { Email = newEmail, Id = accountId });
        }

        public void ClearMachineId(long accountId)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            if (_connectionManager.HasMachineId == false) return;   // stock schema has no such column
            connection.Execute("UPDATE Account SET LastKnownMachineId = NULL WHERE Id = @Id", new { Id = accountId });
        }

        public void UpdatePlayerFields(DBPlayer player)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            // Accounts that never logged in have no Player row yet - create it so the edit isn't lost.
            connection.Execute("INSERT OR IGNORE INTO Player (DbGuid) VALUES (@DbGuid)", player);
            connection.Execute(
                @"UPDATE Player SET StartTarget=@StartTarget, AOIVolume=@AOIVolume,
                  GazillioniteBalance=@GazillioniteBalance, LastLogoutTime=@LastLogoutTime WHERE DbGuid=@DbGuid",
                player);
        }

        public void SetPlayerArchiveData(long accountId, byte[] archiveData)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                "UPDATE Player SET ArchiveData = @ArchiveData WHERE DbGuid = @DbGuid",
                new { ArchiveData = archiveData, DbGuid = accountId });
        }

        // Mirrors AccountManager.ChangeAccountPassword: new hash + salt via the server's exact
        // PBKDF2 scheme, and IsPasswordExpired cleared in the same statement so a forced reset
        // doesn't leave the player locked out with PasswordExpired.
        public void SetPassword(long accountId, string newPassword)
        {
            byte[] passwordHash = PasswordHasher.HashPassword(newPassword, out byte[] salt);

            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                "UPDATE Account SET PasswordHash=@PasswordHash, Salt=@Salt, Flags = Flags & ~@ExpiredFlag WHERE Id=@Id",
                new { PasswordHash = passwordHash, Salt = salt, ExpiredFlag = (int)AccountFlags.IsPasswordExpired, Id = accountId });
        }

        public void SetPasswordHash(long accountId, byte[] passwordHash, byte[] salt)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                "UPDATE Account SET PasswordHash=@PasswordHash, Salt=@Salt WHERE Id=@Id",
                new { PasswordHash = passwordHash, Salt = salt, Id = accountId });
        }

        // Full cascading delete of an account and everything that references it: its Player
        // row, all Avatar/TeamUp/ControlledEntity/Item rows owned by the account or by its
        // avatars/team-ups, and its guild membership. Mirrors the container relationships used
        // by SQLiteDBManager.LoadPlayerData/DoSavePlayerData.
        public void DeleteAccountCascade(long accountId)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            using SQLiteTransaction transaction = connection.BeginTransaction();

            try
            {
                List<long> avatarIds = connection.Query<long>(
                    "SELECT DbGuid FROM Avatar WHERE ContainerDbGuid = @Id", new { Id = accountId }, transaction).AsList();
                List<long> teamUpIds = connection.Query<long>(
                    "SELECT DbGuid FROM TeamUp WHERE ContainerDbGuid = @Id", new { Id = accountId }, transaction).AsList();

                List<long> itemContainers = new(avatarIds.Count + teamUpIds.Count + 1) { accountId };
                itemContainers.AddRange(avatarIds);
                itemContainers.AddRange(teamUpIds);

                connection.Execute("DELETE FROM Item WHERE ContainerDbGuid IN @Ids", new { Ids = itemContainers }, transaction);

                if (avatarIds.Count > 0)
                    connection.Execute("DELETE FROM ControlledEntity WHERE ContainerDbGuid IN @Ids", new { Ids = avatarIds }, transaction);

                connection.Execute("DELETE FROM Avatar WHERE ContainerDbGuid = @Id", new { Id = accountId }, transaction);
                connection.Execute("DELETE FROM TeamUp WHERE ContainerDbGuid = @Id", new { Id = accountId }, transaction);
                connection.Execute("DELETE FROM Player WHERE DbGuid = @Id", new { Id = accountId }, transaction);
                connection.Execute("DELETE FROM GuildMember WHERE PlayerDbGuid = @Id", new { Id = accountId }, transaction);
                connection.Execute("DELETE FROM Account WHERE Id = @Id", new { Id = accountId }, transaction);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
