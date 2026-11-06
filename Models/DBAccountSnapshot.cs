using System.Collections.Generic;

namespace MHDBEditor.Models
{
    // A full, self-contained snapshot of one account: its Account/Player rows plus every
    // Avatar/TeamUp/Item/ControlledEntity row that belongs to it (including items nested under
    // its avatars/team-ups). This is the object graph AccountSnapshotBinarySerializer reads
    // and writes, and what AccountSnapshotService builds from / applies back to the database.
    public class DBAccountSnapshot
    {
        public DBAccount Account { get; set; }
        public DBPlayer Player { get; set; }
        public List<DBEntity> Avatars { get; set; } = new();
        public List<DBEntity> TeamUps { get; set; } = new();
        public List<DBEntity> Items { get; set; } = new();
        public List<DBEntity> ControlledEntities { get; set; } = new();
    }
}
