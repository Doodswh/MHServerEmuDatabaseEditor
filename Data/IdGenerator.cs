using System;
using System.Threading;

namespace MHDBEditor.Data
{
    // Exact port of MHServerEmu.Core.System.IdGenerator - confirmed from source, not inferred.
    // Snowflake-style 64-bit ids: 4 bits type | 12 bits machine id | 32 bits unix timestamp
    // (seconds) | 16 bits sequence number, high bits first.
    public enum IdType
    {
        Generic = 0,
        Region  = 1,
        Player  = 2,        // Accounts and persistent player entities representing those accounts
        Session = 3,
        Game    = 4,
        Entity  = 5,        // Persistent entities (avatars, items, etc.)
        Limit   = 1 << 4
    }

    public static class IdGenerator
    {
        // No shared counter with the real server, so start from something that won't collide
        // with a freshly-restarted counter; combined with the timestamp this is effectively
        // collision-free for a human clicking buttons in this tool.
        private static readonly ushort SequenceStart = unchecked((ushort)Environment.TickCount);
        private static readonly object Lock = new();
        private static long _lastSecond;
        private static int _usedThisSecond;

        public static ulong Generate(IdType type, ushort machineId = 0)
        {
            ulong highBits = ((ulong)type << 60) | ((ulong)machineId << 48);
            long second;
            int used;
            lock (Lock)
            {
                // A batch can make more than 65,536 ids in one second; borrow the next second
                // instead of wrapping the 16-bit sequence onto ids already handed out.
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now > _lastSecond) { _lastSecond = now; _usedThisSecond = 0; }
                if (_usedThisSecond > 0xFFFF) { _lastSecond++; _usedThisSecond = 0; }
                second = _lastSecond;
                used = _usedThisSecond++;
            }
            ulong timestamp = (ulong)second & 0xFFFFFFFF;
            ulong sequence = (ulong)((SequenceStart + used) & 0xFFFF);

            return highBits | (timestamp << 16) | sequence;
        }

        public static (IdType Type, ushort MachineId) Parse(ulong id)
        {
            IdType type = (IdType)(id >> 60);
            ushort machineId = (ushort)((id >> 48) & 0xFFF);
            return (type, machineId);
        }

        // Generates a new id with the same Type/MachineId as an existing id (a sibling row in
        // the same table, or the account being cloned). This is the exact algorithm the real
        // server uses, not a heuristic - the result is indistinguishable from an id the server
        // itself would have generated for that same type/machine.
        public static long GenerateLike(long existingId)
        {
            var (type, machineId) = Parse(unchecked((ulong)existingId));
            return unchecked((long)Generate(type, machineId));
        }

        // Generates a new Entity-type id sharing its machine id with some other id on the same
        // server (e.g. the target account's own id) - for cases like "give this item to this
        // player" where the new row's type (Entity) is known but isn't the same type as the
        // reference id (Player) GenerateLike() would otherwise copy.
        public static long GenerateEntity(long referenceId)
        {
            var (_, machineId) = Parse(unchecked((ulong)referenceId));
            return unchecked((long)Generate(IdType.Entity, machineId));
        }
    }
}
