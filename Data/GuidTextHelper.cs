using System;

namespace MHDBEditor.Data
{
    // Shared parsing for the "decimal or 0x hex" guid text fields used throughout the UI.
    public static class GuidTextHelper
    {
        public static long Parse(string text)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return unchecked((long)Convert.ToUInt64(text, 16));
            return long.Parse(text);
        }
    }
}
