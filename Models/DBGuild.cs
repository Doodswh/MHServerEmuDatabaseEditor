using System.Collections.Generic;

namespace MHDBEditor.Models
{
    // Mirrors MHServerEmu.DatabaseAccess.Models.DBGuild. Members is populated manually by
    // GuildRepository.GetAll() (there's no single query that returns it).
    public class DBGuild
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Motd { get; set; }
        public long CreatorDbGuid { get; set; }
        public long CreationTime { get; set; }
        public long TotalKills { get; set; }

        public List<DBGuildMember> Members { get; } = new();

        // Not a real column - convenience for the grid.
        public int MemberCount => Members.Count;

        public override string ToString() => $"{Name} ({Id})";
    }
}
