using System;
using System.Collections.Generic;
using System.Data.SQLite;
using Dapper;
using System.IO;

namespace MHDBEditor.Data
{
    // Opens connections to an MHServerEmu account database file. Mirrors the connection
    // string used by SQLiteDBManager so behavior (sync mode, foreign keys) matches what the
    // live server expects.
    public class DbConnectionManager
    {
        public string DbFilePath { get; }
        private readonly string _connectionString;

        public DbConnectionManager(string dbFilePath)
        {
            if (File.Exists(dbFilePath) == false)
                throw new FileNotFoundException("Database file not found.", dbFilePath);

            DbFilePath = dbFilePath;
            _connectionString = $"Data Source={dbFilePath};Synchronous=NORMAL;foreign_keys=OFF;";
        }

        // Stock MHServerEmu and forks differ slightly (e.g. stock has no Account.LastKnownMachineId
        // or BannedHardware table), so features check the actual schema instead of assuming one.
        private readonly Dictionary<string, HashSet<string>> _columns = new(StringComparer.OrdinalIgnoreCase);

        public bool HasTable(string table) => ColumnsOf(table).Count > 0;
        public bool HasColumn(string table, string column) => ColumnsOf(table).Contains(column);
        public bool HasMachineId => HasColumn("Account", "LastKnownMachineId");
        public bool HasHardwareBans => HasTable("BannedHardware");

        private HashSet<string> ColumnsOf(string table)
        {
            lock (_columns)
            {
                if (_columns.TryGetValue(table, out HashSet<string> cached)) return cached;
                using SQLiteConnection connection = GetConnection();
                HashSet<string> columns = GetColumns(connection, table);
                _columns[table] = columns;
                return columns;
            }
        }

        public static HashSet<string> GetColumns(SQLiteConnection connection, string table) =>
            new(connection.Query<string>($"SELECT name FROM pragma_table_info('{table.Replace("'", "''")}')"), StringComparer.OrdinalIgnoreCase);

        public SQLiteConnection GetConnection()
        {
            SQLiteConnection connection = new(_connectionString);
            connection.Open();
            return connection;
        }

        // Creates a timestamped backup using the SQLite backup API - same approach as
        // SQLiteDBManager.CreateBackup(). Always do this before a batch of destructive edits.
        public string CreateBackup()
        {
            string backupPath = $"{DbFilePath}.{DateTime.Now:yyyyMMdd_HHmmss}.bak";

            using SQLiteConnection source = GetConnection();
            using SQLiteConnection backup = new($"Data Source={backupPath}");
            backup.Open();
            source.BackupDatabase(backup, "main", "main", -1, null, -1);

            return backupPath;
        }
    }
}
