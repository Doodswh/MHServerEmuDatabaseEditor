namespace MHDBEditor.Models
{
    // Mirrors the BannedHardware table used by SQLiteDBManager.BanHardwareId/IsHardwareBanned.
    public class BannedHardwareEntry
    {
        public string Hwid { get; set; }
        public string BannedBy { get; set; }
        public string Reason { get; set; }
        public string BanDate { get; set; }
    }
}
