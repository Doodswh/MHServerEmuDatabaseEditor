using System;

namespace MHDBEditor.Models
{
    // Mirrors MHServerEmu.DatabaseAccess.Models.AccountUserLevel. These values are persisted
    // to the database - keep in sync with the server project, do not renumber.
    public enum AccountUserLevel : byte
    {
        User = 0,
        Moderator = 1,
        Admin = 2,
        Dev = 3
    }

    // Mirrors MHServerEmu.DatabaseAccess.Models.AccountFlags. Do not renumber existing values.
    [Flags]
    public enum AccountFlags
    {
        None                = 0,
        IsBanned            = 1 << 0,
        IsArchived          = 1 << 1,
        IsPasswordExpired   = 1 << 2,
        // 1 << 3 reserved (legacy 0.x Linux compatibility mode) - do not reuse
        IsWhitelisted       = 1 << 4,
        BypassLoginQueue    = 1 << 5,
        IsDev               = 1 << 6,
    }

    // Mirrors MHServerEmu.DatabaseAccess.Models.DBEntityCategory. These names are used
    // directly as SQLite table names by the server - do not rename.
    public enum DBEntityCategory
    {
        Avatar,
        TeamUp,
        Item,
        ControlledEntity,
    }
}
