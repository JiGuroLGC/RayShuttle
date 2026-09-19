using System;
using System.Security.Cryptography;
using System.Text;

namespace RayShuttle.Services
{
    /// <summary>
    /// 加密节点文件的容器格式，必须与 tools/node_key.py 完全一致。
    ///
    /// 文件本身是 **Base64 文本**，解码后的二进制结构为：
    ///
    ///     MAGIC(8) "RSNODE01" | NONCE(12) | AES-256-GCM 密文（尾部 16 字节认证标签）
    ///
    /// 为什么要套一层 Base64：这个文件要上传到 GitHub，扩展名还是 .txt。
    /// 裸二进制一旦被 Git 的换行转换或编辑器碰过就会损坏，而 AES-GCM 的密文
    /// 只要有一位变化，整个文件就再也解不开了。Base64 化之后是纯 ASCII 文本，
    /// 中间环节怎么折腾都不会坏。
    ///
    /// MAGIC 同时作为 AAD，防止有人篡改文件头。
    /// 本类只负责解密——加密是发放方的事，由 Python 脚本完成。
    /// </summary>
    public static class NodeFileCrypto
    {
        private static readonly byte[] Magic = "RSNODE01"u8.ToArray();

        private const int NonceBytes = 12;
        private const int TagBytes = 16;

        /// <summary>
        /// 解密失败（密钥不对或文件被改动）返回 null，由调用方给出可读提示。
        /// </summary>
        public static string? TryDecryptToJson(byte[] key, byte[] fileBytes)
        {
            if (!TryGetBinaryPayload(fileBytes, out var blob))
            {
                return null;
            }

            var nonce = blob.AsSpan(Magic.Length, NonceBytes);
            var payload = blob.AsSpan(Magic.Length + NonceBytes);

            var ciphertext = payload[..^TagBytes];
            var tag = payload[^TagBytes..];
            var plaintext = new byte[ciphertext.Length];

            try
            {
                using var aes = new AesGcm(key, TagBytes);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (CryptographicException)
            {
                // 认证标签不匹配：密钥错误，或文件被篡改。
                return null;
            }
        }

        /// <summary>
        /// 取出真正要解密的二进制段。
        /// 先按 Base64 文本解（当前格式），认不出来再按裸二进制处理（兼容早期文件）。
        /// **以 MAGIC 是否匹配作为判据**，而不是"能不能解码"——避免二进制文件
        /// 恰好由合法 Base64 字符组成时被误判。
        /// </summary>
        private static bool TryGetBinaryPayload(byte[] fileBytes, out byte[] blob)
        {
            var decoded = TryDecodeBase64(fileBytes);
            if (decoded is not null && HasMagic(decoded))
            {
                blob = decoded;
                return true;
            }

            if (HasMagic(fileBytes))
            {
                blob = fileBytes;
                return true;
            }

            blob = Array.Empty<byte>();
            return false;
        }

        private static byte[]? TryDecodeBase64(byte[] fileBytes)
        {
            try
            {
                var text = Encoding.ASCII.GetString(fileBytes).Trim();
                return text.Length == 0 ? null : Convert.FromBase64String(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool HasMagic(byte[] blob) =>
            blob.Length >= Magic.Length + NonceBytes + TagBytes
            && blob.AsSpan(0, Magic.Length).SequenceEqual(Magic);
    }
}
