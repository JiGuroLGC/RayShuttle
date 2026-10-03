using System;
using System.Security.Cryptography;
using System.Text;

namespace RayShuttle.Services.Api
{
    /// <summary>
    /// 请求签名。签名原文必须与 Worker 端 `src/sign.ts` 的 canonicalString **完全一致**：
    ///
    ///     METHOD\nPATH\nTIMESTAMP\nNONCE\nBODY
    ///
    /// GET 没有请求体，改用「用户名\n指纹」顶上 —— 否则这两个头不在签名范围内，
    /// 攻击者换一个用户名就能借别人的会话。
    /// </summary>
    internal static class ApiSigner
    {
        public static string CanonicalString(string method, string path, long timestampSeconds, string nonce, string body) =>
            $"{method}\n{path}\n{timestampSeconds}\n{nonce}\n{body}";

        public static string ComputeSignature(string canonical)
        {
            var key = Encoding.UTF8.GetBytes(ApiCredentials.HmacSecret);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }

        /// <summary>32 位十六进制，与 Worker 端 `/^[0-9a-f]{32}$/` 的校验一致。</summary>
        public static string NewNonce() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }
}
