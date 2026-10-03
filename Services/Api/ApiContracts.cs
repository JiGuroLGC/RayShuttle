using System.Text.Json.Serialization;

namespace RayShuttle.Services.Api
{
    /// <summary>注册 / 登录的请求体。字段规则与 Worker 端 `parseCredentials` 一致。</summary>
    internal sealed class CredentialRequest
    {
        public string Username { get; set; } = string.Empty;

        public string InviteCode { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;
    }

    /// <summary>注册 / 登录的响应。</summary>
    internal sealed class SessionResponse
    {
        public string Token { get; set; } = string.Empty;

        /// <summary>过期时间（Unix 秒）。</summary>
        public long ExpiresAt { get; set; }
    }

    internal sealed class NodeResponse
    {
        /// <summary>Base64 的加密节点容器，格式与本地节点文件完全一致。</summary>
        public string Payload { get; set; } = string.Empty;

        public long ExpiresAt { get; set; }

        /// <summary>本次请求是否触发了滑动续期。</summary>
        public bool Renewed { get; set; }
    }

    /// <summary>
    /// 错误响应。服务端对鉴权失败恒返回同一句文案，
    /// 靠 <see cref="Code"/> 让客户端走不同的流程（该字段不展示给用户）。
    /// </summary>
    internal sealed class ApiErrorResponse
    {
        public string Error { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;
    }

    /// <summary>刷新通道的请求。</summary>
    internal sealed class RefreshRequest
    {
        /// <summary>要换掉的通道（s1…s7）。</summary>
        public string Slot { get; set; } = string.Empty;

        /// <summary>
        /// 客户端本地统计的用量（字节）。**仅供参考与排查**，服务端一律以供应商返回的为准。
        /// 客户端在用户手里，它报的数字证明不了任何事。
        /// </summary>
        public long ReportedBytes { get; set; }
    }

    /// <summary>刷新通道的响应：加密后的新账号凭据。</summary>
    internal sealed class RefreshResponse
    {
        public string Payload { get; set; } = string.Empty;
    }

    /// <summary>客户端需要区分的细粒度错误码（仅用于流程分支，不要显示给用户）。</summary>
    internal static class ApiErrorCodes
    {
        public const string Transport = "TRANSPORT";
        public const string TokenExpired = "TOKEN_EXPIRED";
        public const string TokenInvalid = "TOKEN_INVALID";
        public const string AuthFailed = "AUTH_FAILED";
        public const string FingerprintMismatch = "FINGERPRINT_MISMATCH";
        public const string AccountLocked = "ACCOUNT_LOCKED";
        public const string AccountDisabled = "ACCOUNT_DISABLED";

        /// <summary>通道不在该用户的记录里。</summary>
        public const string SlotNotFound = "SLOT_NOT_FOUND";

        /// <summary>服务端复核后认为还没到刷新的时候。客户端要重置本地基线。</summary>
        public const string NotExhausted = "NOT_EXHAUSTED";

        /// <summary>供应商接口不可用。</summary>
        public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";

        /// <summary>注册成功但新账号不可用。</summary>
        public const string RefreshFailed = "REFRESH_FAILED";

        /// <summary>该账号一个可用通道都没有。</summary>
        public const string NodeUnavailable = "NODE_UNAVAILABLE";
    }

    /// <summary>
    /// 用源生成器而不是反射式序列化：Release 构建开了 PublishTrimmed，
    /// 反射式 JsonSerializer 会把属性裁掉。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(CredentialRequest))]
    [JsonSerializable(typeof(SessionResponse))]
    [JsonSerializable(typeof(NodeResponse))]
    [JsonSerializable(typeof(RefreshRequest))]
    [JsonSerializable(typeof(RefreshResponse))]
    [JsonSerializable(typeof(ApiErrorResponse))]
    internal sealed partial class ApiJsonContext : JsonSerializerContext
    {
    }
}
