namespace MHDBEditor.Models
{
    public class DBGuildMember
    {
        public long PlayerDbGuid { get; set; }
        public long GuildId { get; set; }
        public long Membership { get; set; }

        public override string ToString() => $"PlayerDbGuid=0x{PlayerDbGuid:X}, Membership={Membership}";
    }
}
