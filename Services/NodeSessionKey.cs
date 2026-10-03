using System.Security.Cryptography;
using System.Text;

namespace RayShuttle.Services
{
    /// <summary>
    /// 由长期 token 派生节点加密密钥。
    ///
    /// 服务端只保存邀请码的哈希，推导不出客户端那把 PBKDF2 节点密钥，
    /// 所以 /node 下发的内容是用**这把会话密钥**加密的：
    ///
    ///     HKDF-SHA256(ikm = token, salt = "rayshuttle-node", info = "node") → 32 字节
    ///
    /// salt 与 info 必须与 `server/src/crypto.ts` 的 deriveNodeKey 完全一致，
    /// 否则两边解不开。
    ///
    /// 客户端原有的 `NodeKeyDerivation`（PBKDF2，邀请码派生）**保留**，
    /// 继续用于本地 `nodes.enc` 兜底文件。
    /// </summary>
    public static class NodeSessionKey
    {
        /// <summary>与服务端约定的盐。</summary>
        public const string Salt = "rayshuttle-node";

        public const string Info = "node";

        public const int KeySizeBytes = 32;

        public static byte[] Derive(string token) =>
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(token),
                KeySizeBytes,
                Encoding.UTF8.GetBytes(Salt),
                Encoding.UTF8.GetBytes(Info));
    }
}
