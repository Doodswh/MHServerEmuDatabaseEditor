using System.Collections.Generic;
using System.Data.SQLite;
using Dapper;
using MHDBEditor.Models;

namespace MHDBEditor.Data
{
    public class GuildRepository
    {
        private readonly DbConnectionManager _connectionManager;

        public GuildRepository(DbConnectionManager connectionManager)
        {
            _connectionManager = connectionManager;
        }

        public List<DBGuild> GetAll()
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();

            List<DBGuild> guilds = connection.Query<DBGuild>("SELECT * FROM Guild ORDER BY Name").AsList();
            List<DBGuildMember> members = connection.Query<DBGuildMember>("SELECT * FROM GuildMember").AsList();

            Dictionary<long, DBGuild> lookup = new();
            foreach (DBGuild guild in guilds)
                lookup[guild.Id] = guild;

            foreach (DBGuildMember member in members)
            {
                if (lookup.TryGetValue(member.GuildId, out DBGuild guild))
                    guild.Members.Add(member);
            }

            return guilds;
        }

        public void UpdateNameAndMotd(long guildId, string name, string motd)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("UPDATE Guild SET Name=@Name, Motd=@Motd WHERE Id=@Id", new { Name = name, Motd = motd, Id = guildId });
        }

        public void Delete(long guildId)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            using SQLiteTransaction transaction = connection.BeginTransaction();

            try
            {
                connection.Execute("DELETE FROM GuildMember WHERE GuildId = @Id", new { Id = guildId }, transaction);
                connection.Execute("DELETE FROM Guild WHERE Id = @Id", new { Id = guildId }, transaction);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        // A player has one GuildMember row (PlayerDbGuid is the key), so this is their guild, if any.
        public long? GetGuildOf(long playerDbGuid)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.QueryFirstOrDefault<long?>("SELECT GuildId FROM GuildMember WHERE PlayerDbGuid=@Id", new { Id = playerDbGuid });
        }

        // Puts the player in this guild with this rank - moving them out of any other guild,
        // since a player can only be in one.
        public void SetMembership(long playerDbGuid, long guildId, long membership)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                @"INSERT INTO GuildMember (PlayerDbGuid, GuildId, Membership) VALUES (@PlayerDbGuid, @GuildId, @Membership)
                  ON CONFLICT(PlayerDbGuid) DO UPDATE SET GuildId=excluded.GuildId, Membership=excluded.Membership",
                new { PlayerDbGuid = playerDbGuid, GuildId = guildId, Membership = membership });
        }

        public void RemoveMember(long playerDbGuid, long guildId)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("DELETE FROM GuildMember WHERE PlayerDbGuid = @Id AND GuildId = @GuildId", new { Id = playerDbGuid, GuildId = guildId });
        }
    }
}
