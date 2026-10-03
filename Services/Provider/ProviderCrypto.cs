using System;
using System.Security.Cryptography;
using System.Text;

namespace RayShuttle.Services.Provider
{
    /// <summary>
    /// 供应商 init 响应的解密：base64 → AES-128-CBC（PKCS#7）→ 明文 JSON。
    ///
    /// **密钥与 IV 就写在客户端里**，这不是疏忽：客户端必须自己解密才能拿到订阅地址，
    /// 而订阅地址本来就是要交给用户的东西。真正不能进客户端的是**注册接口**
    /// （它在 Worker 侧，见 server/src/provider.ts），那才决定「一个账号 5GB」这个约束。
    ///
    /// 解密失败时返回原文（供应商偶发直接发明文），与模拟器 `decrypt_auto` 的行为一致。
    /// </summary>
    internal static class ProviderCrypto
    {
        private const string Key = "pz8yvtyidpxv97a6";
        private const string Iv = "pe8y9tx1guglgke3";

        public static string Decrypt(string body)
        {
            var trimmed = body.Trim();
            if (trimmed.Length == 0)
            {
                return trimmed;
            }

            try
            {
                var raw = Convert.FromBase64String(trimmed);

                // AES-CBC 的密文必然是 16 字节的整数倍；不满足说明这不是密文。
                if (raw.Length == 0 || raw.Length % 16 != 0)
                {
                    return trimmed;
                }

                using var aes = Aes.Create();
                aes.Key = Encoding.UTF8.GetBytes(Key);
                aes.IV = Encoding.UTF8.GetBytes(Iv);
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(raw, 0, raw.Length));
            }
            catch (FormatException)
            {
                return trimmed;
            }
            catch (CryptographicException)
            {
                return trimmed;
            }
        }
    }
}
