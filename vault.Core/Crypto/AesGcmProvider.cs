using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
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

            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
                return result;
            }
            catch (PlatformNotSupportedException)
            {
                return EncryptWithBouncyCastle(key, nonce, plaintext, aad);
            }
            catch (CryptographicException ex) when (IsAesGcmAlgorithmUnsupported(ex))
            {
                return EncryptWithBouncyCastle(key, nonce, plaintext, aad);
            }
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

            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                return plaintext;
            }
            catch (PlatformNotSupportedException)
            {
                return DecryptWithBouncyCastle(key, nonce, cipherAndTag, aad);
            }
            catch (CryptographicException ex) when (IsAesGcmAlgorithmUnsupported(ex))
            {
                return DecryptWithBouncyCastle(key, nonce, cipherAndTag, aad);
            }
        }

        private static byte[] EncryptWithBouncyCastle(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, aad);
            cipher.Init(true, parameters);

            byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
            int written = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            written += cipher.DoFinal(output, written);

            if (written == output.Length)
                return output;

            var resized = new byte[written];
            Buffer.BlockCopy(output, 0, resized, 0, written);
            CryptographicOperations.ZeroMemory(output);
            return resized;
        }

        private static byte[] DecryptWithBouncyCastle(byte[] key, byte[] nonce, byte[] cipherAndTag, byte[] aad)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, aad);
            cipher.Init(false, parameters);

            try
            {
                byte[] output = new byte[cipher.GetOutputSize(cipherAndTag.Length)];
                int written = cipher.ProcessBytes(cipherAndTag, 0, cipherAndTag.Length, output, 0);
                written += cipher.DoFinal(output, written);

                if (written == output.Length)
                    return output;

                var resized = new byte[written];
                Buffer.BlockCopy(output, 0, resized, 0, written);
                CryptographicOperations.ZeroMemory(output);
                return resized;
            }
            catch (InvalidCipherTextException ex)
            {
                throw new CryptographicException(VaultText.T("core.error.passwordOrCorrupted"), ex);
            }
        }

        private static bool IsAesGcmAlgorithmUnsupported(CryptographicException ex)
        {
            string msg = ex.Message ?? string.Empty;
            return msg.Contains("AlgorithmNotSupported", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("AesGcm", StringComparison.OrdinalIgnoreCase);
        }
    }
}
