using System.Security.Cryptography;

namespace MHDBEditor.Utility
{
    // Ports the password half of MHServerEmu.Core.Helpers.CryptographyHelper exactly:
    // PBKDF2-HMAC-SHA512, 210000 iterations, a fresh 64-byte random salt, 64-byte derived key.
    // The length limits come from AccountManager.ValidatePassword (3..64 chars). Any drift in
    // these constants produces a hash the server silently rejects, so keep them in sync with
    // the server source rather than "improving" them.
    public static class PasswordHasher
    {
        public const int PasswordMinLength = 3;
        public const int PasswordMaxLength = 64;

        private const int PasswordKeySize = 64;
        private const int PasswordIterationCount = 210000;

        public static byte[] HashPassword(string password, out byte[] salt)
        {
            salt = RandomNumberGenerator.GetBytes(PasswordKeySize);
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterationCount, HashAlgorithmName.SHA512, PasswordKeySize);
        }

        public static bool VerifyPassword(string password, byte[] hash, byte[] salt)
        {
            if (hash == null || salt == null || hash.Length == 0 || salt.Length == 0)
                return false;

            byte[] hashToCompare = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterationCount, HashAlgorithmName.SHA512, PasswordKeySize);
            return CryptographicOperations.FixedTimeEquals(hashToCompare, hash);
        }

        public static bool IsValidPassword(string password)
        {
            return password != null && password.Length >= PasswordMinLength && password.Length <= PasswordMaxLength;
        }
    }
}
