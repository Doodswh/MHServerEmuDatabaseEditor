namespace MHDBEditor.Models
{
    // Mirrors MHServerEmu.DatabaseAccess.Models.DBPlayer. ArchiveData is intentionally left as
    // an opaque byte[] - editing what's inside it requires the game's Archive serializer,
    // which this tool does not implement yet (see README).
    public class DBPlayer
    {
        public long DbGuid { get; set; }
        public byte[] ArchiveData { get; set; }
        public long StartTarget { get; set; }
        public int AOIVolume { get; set; }
        public long GazillioniteBalance { get; set; }
        public long LastLogoutTime { get; set; }
        public long Flags { get; set; }   // stock only (DBPlayerFlags: 1 = Imported); not a column in the COA build
    }
}
