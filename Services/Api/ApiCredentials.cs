namespace RayShuttle.Services.Api
{
    /// <summary>
    /// 内嵌在客户端里的签名密钥与版本信息。
    ///
    /// **诚实说明它的作用边界**：客户端是分发出去的桌面程序，这里的东西一定能被逆向出来。
    /// 所以 HMAC 共享密钥挡不住决心伪造请求的攻击者，它只挡得住随手 curl 的脚本，
    /// 并提供「协议指纹 + 版本门禁」。真正的安全边界是邀请码（32 位随机码，高熵）+ 设备绑定 +
    /// 服务端限流。**任何只有服务端才该知道的东西都不能放在这里。**
    ///
    /// 轮换密钥时：先让 Worker 同时接受新旧两个密钥（按 X-Client-Version 分支），
    /// 再发新版客户端，最后撤掉旧密钥 —— 否则旧版本用户会集体 401。
    /// </summary>
    internal static class ApiCredentials
    {
        /// <summary>
        /// 必须与 Worker 的 `HMAC_SECRET` 完全相同，否则所有请求都是 400 `MALFORMED`。
        /// 轮换时先让服务端同时接受新旧两个密钥，再发新版客户端，最后撤旧的。
        /// </summary>
        public const string HmacSecret = "2ae35c9f35653324bba07e22bb488661e8a2328d33ca208a9fe91d59c6a77d51";

        public const int ClientVersion = 1;

        /// <summary>Worker 端用正则 `^RayShuttle/\d+` 校验，改这里要同步服务端。</summary>
        public const string UserAgent = "RayShuttle/1.0";
    }
}
