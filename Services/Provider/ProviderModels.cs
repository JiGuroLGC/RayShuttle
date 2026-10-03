using System;
using System.Globalization;

namespace RayShuttle.Services.Provider
{
    /// <summary>云端下发的一个通道。只含「拿它去换订阅」所必需的东西。</summary>
    internal sealed class ProviderSlotRef
    {
        public string Slot { get; init; } = string.Empty;

        /// <summary>供应商侧的设备 ID（16 位十六进制）。客户端用它换订阅。</summary>
        public string Uuid { get; init; } = string.Empty;

        /// <summary>
        /// 该账号的注册时间（Unix 秒）。服务端顺手带上，客户端据此推算 7 天有效期，
        /// 不必为了「还剩多久到期」频繁去打 init（那个响应有 200KB 以上）。
        /// </summary>
        public long RegisteredAtUnix { get; init; }
    }

    /// <summary>供应商返回的账号用量。</summary>
    internal sealed class ProviderUsage
    {
        public string Uuid { get; init; } = string.Empty;

        public string SubUrl { get; init; } = string.Empty;

        public long? TransferEnableBytes { get; init; }

        public long? UnusedBytes { get; init; }

        public long? UsedBytes { get; init; }

        /// <summary>免费账号的有效期（Unix 秒）。过期后额度归零、节点连不上。</summary>
        public long? ExpireAtUnix { get; init; }

        public int? UserClass { get; init; }

        public bool Expired => ExpireAtUnix is not null
            && ExpireAtUnix.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    internal enum ProviderQueryStatus
    {
        /// <summary>查到了账号。</summary>
        Ok,

        /// <summary>账号不存在（未注册的 uuid：HTTP 200 但 userinfo 为 null）。</summary>
        Gone,

        /// <summary>网络失败 / 解密失败 / 格式变了：无法判断，调用方不能据此刷新。</summary>
        Unavailable
    }

    internal sealed record ProviderQuery(ProviderQueryStatus Status, ProviderUsage? Usage)
    {
        public static ProviderQuery Gone() => new(ProviderQueryStatus.Gone, null);

        public static ProviderQuery Unavailable() => new(ProviderQueryStatus.Unavailable, null);
    }

    /// <summary>
    /// 供应商账号的两条约束（实测值，改之前重跑模拟器核对）。
    /// </summary>
    internal static class ProviderLimits
    {
        /// <summary>每个账号的总流量。实测 transfer_enable = "5GB"。</summary>
        public const long QuotaBytes = 5L * 1024 * 1024 * 1024;

        /// <summary>
        /// 免费账号的有效期。实测注册 2026-09-27 15:28:35 → 到期 2026-10-04 15:28:35，正好 7 天。
        ///
        /// **它比 5GB 更容易先到**（实测有账号只用了 232MB 就因过期归零），
        /// 所以「到期」才是刷新的主因，别把有效期当成次要条件。
        /// </summary>
        public static readonly TimeSpan FreeTtl = TimeSpan.FromDays(7);

        /// <summary>过期时间是北京时间墙上时间，解析时要显式带 +08:00，否则会差 8 小时。</summary>
        public static readonly TimeSpan ProviderOffset = TimeSpan.FromHours(8);

        /// <summary>把 "2026-10-04 15:28:35" 解析成 Unix 秒。失败返回 null。</summary>
        public static long? ParseExpire(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (!DateTimeOffset.TryParseExact(
                    text!.Trim(),
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                return null;
            }

            return parsed.ToOffset(ProviderOffset).ToUnixTimeSeconds();
        }

        /// <summary>按注册时间推算有效期。用于「要不要去核实一次」的粗判，精确值仍以 init 为准。</summary>
        public static long EstimateExpireAtUnix(long registeredAtUnix) =>
            registeredAtUnix + (long)FreeTtl.TotalSeconds;
    }
}
