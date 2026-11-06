using System.Text;

namespace MHDBEditor.Utility
{
    // Ports MHServerEmu.Core.Helpers.HashHelper's HashPath() logic without taking a dependency
    // on Free.Ports.zLib. Adler-32 and CRC-32 are the same well-defined, standard algorithms
    // zlib implements (zlib is itself the reference implementation of both), so a correct
    // implementation of the algorithms as specified produces bit-identical output to the
    // server's own HashHelper.Adler32()/Crc32() for the same input - this isn't an approximation.
    //
    // The path-preparation rules (ToCalligraphyPath / resource '&' prefix) are taken verbatim
    // from the comment on the server's HashPath(): "Calligraphy: 1) Replace '.' with '?'
    // 2) Replace '/' with '.'" and "Resource: Insert '&' as the first char".
    public static class HashHelper
    {
        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Adler32(byte[] bytes)
        {
            const uint ModAdler = 65521;
            uint a = 1, b = 0;
            foreach (byte value in bytes)
            {
                a = (a + value) % ModAdler;
                b = (b + a) % ModAdler;
            }
            return (b << 16) | a;
        }

        public static uint Adler32(string str) => Adler32(Encoding.UTF8.GetBytes(str));

        public static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in bytes)
                crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        public static uint Crc32(string str) => Crc32(Encoding.UTF8.GetBytes(str));

        // "1) Replace '.' with '?' 2) Replace '/' with '.'" - from the HashPath() comment.
        // Use for normal Calligraphy prototype/blueprint paths (the common case, e.g. paths
        // ending in .prototype/.defaults).
        public static string ToCalligraphyPath(string path) => path.Replace('.', '?').Replace('/', '.');

        // "Insert '&' as the first char" - from the HashPath() comment. Use for resource paths.
        public static string ToResourcePath(string path) => "&" + path;

        // Matches MHServerEmu.Core.Helpers.HashHelper.HashPath() exactly, including the -1
        // adjustment so an empty string hashes to 0 (the invalid DataRef value). Pass the path
        // through ToCalligraphyPath() or ToResourcePath() first - this method does not do that
        // preparation itself, same as the original.
        public static ulong HashPath(string preparedPath)
        {
            string lower = preparedPath.ToLower();
            ulong adler = Adler32(lower);
            ulong crc = Crc32(lower);
            return (adler | (crc << 32)) - 1;
        }
    }
}
