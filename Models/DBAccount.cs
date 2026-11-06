namespace MHDBEditor.Models
{
    // Lightweight POCO mirroring MHServerEmu.DatabaseAccess.Models.DBAccount, trimmed of
    // server-only behavior (locking, id generation, JSON entity collections) since this tool
    // only needs to read/write the Account table's columns via Dapper.
    public class DBAccount
    {
        public long Id { get; set; }
        public string Email { get; set; }
        public string PlayerName { get; set; }
        public byte[] PasswordHash { get; set; }
        public byte[] Salt { get; set; }
        public AccountUserLevel UserLevel { get; set; }
        public AccountFlags Flags { get; set; }
        public string LastKnownMachineId { get; set; }

        public bool HasFlag(AccountFlags flag) => (Flags & flag) == flag;

        public override string ToString() => $"{PlayerName} (0x{Id:X})";
    }
}
