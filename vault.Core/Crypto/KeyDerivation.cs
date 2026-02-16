using Isopoh.Cryptography.Argon2;
using System;
using System.Linq;

namespace vault.Core.Crypto
{
    internal static class KeyDerivation
    {
        public static byte[] DeriveKey(byte[] password, byte[] salt)
        {
            if (password == null)
                throw new ArgumentNullException(nameof(password));
            if (salt == null)
                throw new ArgumentNullException(nameof(salt));

            var config = new Argon2Config
            {
                Type = Argon2Type.DataIndependentAddressing, // Argon2id
                Version = Argon2Version.Nineteen,
                TimeCost = 3,
                MemoryCost = 65536, // 64 MB
                Lanes = 4,
                Threads = 4,
                Password = password,
                Salt = salt,
                HashLength = 32 // AES-256
            };

            using var argon2 = new Argon2(config);
            using var hash = argon2.Hash();
            // SecureArray releases and zeroes its internal buffer on Dispose.
            // We must copy the bytes before leaving the using scope.
            return hash.Buffer.ToArray();
        }
    }
}
