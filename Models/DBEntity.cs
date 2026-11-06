namespace MHDBEditor.Models
{
    // Mirrors MHServerEmu.DatabaseAccess.Models.DBEntity. Represents one row in one of the
    // Avatar / TeamUp / Item / ControlledEntity tables (all four share this exact shape).
    // ArchiveData is left opaque - see README for what that means for item editing.
    public class DBEntity
    {
        public long DbGuid { get; set; }
        public long ContainerDbGuid { get; set; }
        public long InventoryProtoGuid { get; set; }
        public uint Slot { get; set; }
        public long EntityProtoGuid { get; set; }
        public byte[] ArchiveData { get; set; }

        // Not a real column - convenience for the grid, since we can't show anything more
        // meaningful about the blob's contents yet.
        public int ArchiveDataLength => ArchiveData?.Length ?? 0;

        public override string ToString()
            => $"DbGuid=0x{DbGuid:X}, ContainerDbGuid=0x{ContainerDbGuid:X}, InventoryProtoGuid=0x{InventoryProtoGuid:X}, Slot={Slot}, EntityProtoGuid=0x{EntityProtoGuid:X}";
    }
}
