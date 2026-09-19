using System;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace RayShuttle.Services
{
    /// <summary>
    /// 用 Windows DPAPI（经 WinRT 的 DataProtectionProvider）加密本地敏感数据。
    ///
    /// 保护范围是「当前用户 + 本机」（LOCAL=user）：换用户或换机器都解不开，
    /// 因此**不需要在本地保存任何密钥**——比自管 AES 密钥更安全，也少一份要保管的东西。
    /// </summary>
    public static class SecretProtector
    {
        private const string ProtectionDescriptor = "LOCAL=user";

        /// <summary>加密后返回 Base64 密文，可直接写进 JSON。</summary>
        public static async Task<string> ProtectAsync(string plainText)
        {
            var provider = new DataProtectionProvider(ProtectionDescriptor);
            var input = CryptographicBuffer.ConvertStringToBinary(plainText, BinaryStringEncoding.Utf8);
            var output = await provider.ProtectAsync(input);
            return CryptographicBuffer.EncodeToBase64String(output);
        }

        /// <summary>
        /// 解密失败时返回 null 而不是抛异常：换了用户/机器、或数据损坏都属于正常情况，
        /// 调用方按「未登录」处理即可，不该让应用起不来。
        /// </summary>
        public static async Task<string?> UnprotectAsync(string protectedText)
        {
            try
            {
                // 无参构造用于解密——保护范围已经写在密文里了。
                var provider = new DataProtectionProvider();
                var input = CryptographicBuffer.DecodeFromBase64String(protectedText);
                var output = await provider.UnprotectAsync(input);
                return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, output);
            }
            catch
            {
                return null;
            }
        }
    }
}
