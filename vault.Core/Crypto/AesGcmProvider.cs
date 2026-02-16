using System;
using System.Security.Cryptography;
using vault.Core;

namespace vault.Core.Crypto
{
    internal static class AesGcmProvider
    {
        private const int TagSize = 16; // 128-bit authentication tag

        public static byte[] Encrypt(
            byte[] key,
            byte[] nonce,
            byte[] plaintext,
            byte[] aad)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (nonce == null) throw new ArgumentNullException(nameof(nonce));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (aad == null) throw new ArgumentNullException(nameof(aad));
            if (plaintext.Length > int.MaxValue - TagSize)
            {
                throw new InvalidOperationException(
                    VaultText.T("core.crypto.vaultTooLargeForEncryption"));
            }

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

            // ciphertext || tag
            byte[] result = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, ciphertext.Length, tag.Length);

            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);

            return result;
        }

        public static byte[] Decrypt(
            byte[] key,
            byte[] nonce,
            byte[] cipherAndTag,
            byte[] aad)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (nonce == null) throw new ArgumentNullException(nameof(nonce));
            if (cipherAndTag == null) throw new ArgumentNullException(nameof(cipherAndTag));
            if (aad == null) throw new ArgumentNullException(nameof(aad));

            if (cipherAndTag.Length < TagSize)
                throw new CryptographicException(VaultText.T("core.crypto.invalidCiphertext"));

            int cipherLen = cipherAndTag.Length - TagSize;

            byte[] ciphertext = new byte[cipherLen];
            byte[] tag = new byte[TagSize];
            byte[] plaintext = new byte[cipherLen];

            Buffer.BlockCopy(cipherAndTag, 0, ciphertext, 0, cipherLen);
            Buffer.BlockCopy(cipherAndTag, cipherLen, tag, 0, TagSize);

            try
            {
                using (var aes = new AesGcm(key, TagSize))
                {
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                }

                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
            }
        }
    }
}
