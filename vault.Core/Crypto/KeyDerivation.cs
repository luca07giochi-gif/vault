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

            // "Threads" affects execution strategy, not vault format compatibility.
            // On WebAssembly/browser environments we often have a single logical CPU
            // and multithreaded execution may stall or be unsupported.
            int workerThreads = Math.Clamp(Environment.ProcessorCount, 1, 4);

            var config = new Argon2Config
            {
                Type = Argon2Type.DataIndependentAddressing, // Argon2id
                Version = Argon2Version.Nineteen,
                TimeCost = 3,
                MemoryCost = 65536, // 64 MB
                Lanes = 4,
                Threads = workerThreads,
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
