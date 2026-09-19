using System;
using System.Security.Cryptography;
using System.Text;

namespace RayShuttle.Services
{
    /// <summary>
    /// 由「用户名 + 邀请码」派生节点文件的 AES-256 密钥。
    ///
    /// 用它加密的节点文件放在公开仓库里，只有持有正确（用户名, 邀请码）的人才能解出来，
    /// 因此**本类与 tools/node_key.py 必须逐字节一致**——任何参数改动都要两边同步，
    /// 否则脚本加密的文件客户端会解不开。
    ///
    /// 规则（四个参数，任何一个不同都会得到完全不同的密钥）：
    ///   password   = 规范化后的邀请码，UTF-8（32 位大写字母数字）
    ///   salt       = 小写用户名 + "_rayshuttle"，UTF-8（例如 alice_rayshuttle）
    ///   算法       = PBKDF2-HMAC-SHA256
    ///   迭代次数   = 600000（OWASP 2023 对 PBKDF2-HMAC-SHA256 的建议值）
    ///   输出长度   = 32 字节（AES-256）
    /// </summary>
    public static class NodeKeyDerivation
    {
        /// <summary>软件标识，拼在盐值末尾。</summary>
        public const string SaltSuffix = "_rayshuttle";

        public const int Iterations = 600_000;

        public const int KeySizeBytes = 32;

        /// <summary>
        /// 用户名统一转小写后再进盐值。
        ///
        /// 否则 Alice 与 alice 会派生出两把不同的密钥，而用户根本看不出差别——
        /// 这类不一致极难排查。Python 脚本必须照做同一步。
        /// </summary>
        public static string NormalizeUserName(string userName) =>
            userName.Trim().ToLowerInvariant();

        public static string BuildSalt(string userName) =>
            NormalizeUserName(userName) + SaltSuffix;

        public static byte[] DeriveKey(string userName, string inviteCode)
        {
            var password = Encoding.UTF8.GetBytes(InviteCode.Normalize(inviteCode));
            var salt = Encoding.UTF8.GetBytes(BuildSalt(userName));

            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySizeBytes);
        }

        /// <summary>小写十六进制，与 Python 脚本 <c>key.hex()</c> 的输出一致。</summary>
        public static string DeriveKeyHex(string userName, string inviteCode) =>
            Convert.ToHexString(DeriveKey(userName, inviteCode)).ToLowerInvariant();
    }
}
