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

            byte[] result = new byte[plaintext.Length + TagSize];
            Span<byte> ciphertext = result.AsSpan(0, plaintext.Length);
            Span<byte> tag = result.AsSpan(plaintext.Length, TagSize);

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

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

            byte[] plaintext = new byte[cipherLen];
            ReadOnlySpan<byte> ciphertext = cipherAndTag.AsSpan(0, cipherLen);
            ReadOnlySpan<byte> tag = cipherAndTag.AsSpan(cipherLen, TagSize);

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            }

            return plaintext;
        }
    }
}
