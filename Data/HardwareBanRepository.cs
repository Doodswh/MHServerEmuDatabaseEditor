using System;
using System.Collections.Generic;
using System.Data.SQLite;
using Dapper;
using MHDBEditor.Models;

namespace MHDBEditor.Data
{
    public class HardwareBanRepository
    {
        private readonly DbConnectionManager _connectionManager;

        public HardwareBanRepository(DbConnectionManager connectionManager)
        {
            _connectionManager = connectionManager;
        }

        public List<BannedHardwareEntry> GetAll()
        {
            if (_connectionManager.HasHardwareBans == false) return new List<BannedHardwareEntry>();   // stock schema
            using SQLiteConnection connection = _connectionManager.GetConnection();
            return connection.Query<BannedHardwareEntry>("SELECT * FROM BannedHardware ORDER BY BanDate DESC").AsList();
        }

        // Same text the server writes: time since 1970 as a TimeSpan ("20539.04:10:19.7441333").
        // Fixed-width days, so ORDER BY BanDate stays in date order.
        private static string BanDateNow() => TimeSpan.FromTicks((DateTime.UtcNow - DateTime.UnixEpoch).Ticks).ToString();

        public void Ban(string hwid, string bannedBy, string reason)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute(
                @"INSERT OR REPLACE INTO BannedHardware (Hwid, BannedBy, Reason, BanDate)
                  VALUES (@Hwid, @BannedBy, @Reason, @BanDate)",
                new { Hwid = hwid, BannedBy = bannedBy, Reason = reason, BanDate = BanDateNow() });
        }

        public void Unban(string hwid)
        {
            using SQLiteConnection connection = _connectionManager.GetConnection();
            connection.Execute("DELETE FROM BannedHardware WHERE Hwid = @Hwid", new { Hwid = hwid });
        }
    }
}
