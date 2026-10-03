using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Models;
using RayShuttle.Services.Api;

namespace RayShuttle.Services.Provider
{
    /// <summary>一次加载的结果。</summary>
    internal sealed class SlotLoadResult
    {
        public List<ProxyNode> Nodes { get; } = new();

        /// <summary>无法识别 / 拿不到订阅的通道数。</summary>
        public int Skipped { get; set; }

        /// <summary>本次顺手换掉了几个通道。</summary>
        public int Refreshed { get; set; }
    }

    /// <summary>
    /// 通道（slot）的客户端编排：拿凭据换节点、判断该不该刷新、执行刷新。
    ///
    /// **用量以谁为准**：本地 Xray 统计只用来决定「要不要去核实一次」，
    /// 真正的判据是服务端复核过的供应商用量（客户端报的数字在用户手里，证明不了任何事）。
    /// 所以流程是：本地估算触发 → 查 init 核实 → 调 /refresh → 服务端再复核一次。
    ///
    /// **刷新时机**：能不在传输中换就不在传输中换。加载节点（启动 / 切页）时是最佳时机，
    /// 连接中只在「这条通道真的快没了」时才动，且换完立刻重连。
    /// </summary>
    internal sealed class ProviderSlotStore
    {
        /// <summary>用量缓存的有效期。init 的响应有 200KB 以上，别为了看一眼用量就打一次。</summary>
        private static readonly TimeSpan UsageCacheTtl = TimeSpan.FromMinutes(30);

        /// <summary>剩余额度低于此值就认为「将尽」（与服务端 REFRESH_TRAFFIC_BYTES 默认值一致）。</summary>
        private const long TrafficThresholdBytes = 200L * 1024 * 1024;

        /// <summary>剩余有效期低于此值就认为「将尽」（与服务端 REFRESH_TTL_SEC 默认值一致）。</summary>
        private static readonly TimeSpan TtlThreshold = TimeSpan.FromHours(12);

        /// <summary>
        /// 打散用的最大抖动。7 个通道是同时注册的，会**同一天集体过期**；
        /// 给每个通道加一个 0~48 小时的固定偏移，把刷新摊到两天里。
        /// 偏移必须由 slot 名确定地算出来：每次重启都变的话就打散不成了。
        /// </summary>
        private static readonly TimeSpan MaxJitter = TimeSpan.FromHours(48);

        public static ProviderSlotStore Current { get; } = new();

        /// <summary>
        /// 备用节点用的固定通道名。
        ///
        /// 它们**不是**供应商账号：不查用量、不刷新、不访问第三方 API，
        /// 只随 `/node` 一起下发，作为「通道全挂了还有得连」的兜底。
        /// 因为它不在 `_slots` 里，连接守卫的 `NeedsVerification` 会直接返回 false，
        /// 于是天然不会被引到刷新的逻辑里——这正是我们要的。
        /// </summary>
        public const string ExtraSlotId = "extra";

        private readonly Dictionary<string, ProviderSlotRef> _slots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long FetchedAtUnix, ProviderUsage Usage)> _usage =
            new(StringComparer.Ordinal);

        private ProviderSlotStore()
        {
        }

        /// <summary>
        /// 是否持有任何通道凭据。
        ///
        /// 本地文件 / 纯分享链接那两条路径不会经过这里，因此为 false——
        /// 那种情况下没有任何通道可查用量，界面也就不会有用量标签。
        /// </summary>
        public bool HasSlots => _slots.Count > 0;

        // ------------------------------------------------------------ 加载

        /// <summary>
        /// 依次取回各通道的节点，顺手把「将尽」的通道换掉。
        /// 单个通道失败只跳过它自己，不影响其它通道——7 个里坏 1 个不该让用户一个节点都没有。
        /// </summary>
        public async Task<SlotLoadResult> LoadAsync(
            IReadOnlyList<ProviderSlotRef> slots,
            CancellationToken cancellationToken = default)
        {
            var result = new SlotLoadResult();

            foreach (var incoming in slots)
            {
                _slots[incoming.Slot] = incoming;

                var current = incoming;
                var usage = await GetUsageAsync(current, cancellationToken);

                if (usage is null)
                {
                    NodeDiagnostics.Log($"通道 {current.Slot}：用量查询失败（供应商 init 接口不可达或账号没了）。");
                }
                else
                {
                    NodeDiagnostics.Log($"通道 {current.Slot}：用量已取，SubUrl={(usage.SubUrl.Length == 0 ? "空" : usage.SubUrl)}，过期={usage.Expired}。");
                }

                // 账号没了 / 快到期 / 快没流量 → 趁现在没在传数据，换掉它。
                if (NeedsRefresh(usage, 0))
                {
                    var refreshed = await RefreshAsync(current.Slot, cancellationToken);
                    if (refreshed is not null)
                    {
                        current = refreshed;
                        result.Refreshed++;
                        usage = await GetUsageAsync(current, cancellationToken, force: true);
                        NodeDiagnostics.Log($"通道 {current.Slot}：已刷新为新账号。");
                    }
                    else
                    {
                        NodeDiagnostics.Log($"通道 {current.Slot}：需要刷新但刷新失败（未消耗新账号）。");
                    }
                }

                var slotNodes = await LoadNodesAsync(current, usage, result, cancellationToken);
                NodeDiagnostics.Log($"通道 {current.Slot}：取到 {slotNodes.Count} 个节点。");
                result.Nodes.AddRange(slotNodes);
            }

            return result;
        }

        /// <summary>单个通道的节点（刷新后重新取订阅时用）。</summary>
        public async Task<List<ProxyNode>> LoadNodesAsync(
            string slotId,
            CancellationToken cancellationToken = default)
        {
            if (!_slots.TryGetValue(slotId, out var slot))
            {
                return new List<ProxyNode>();
            }

            var usage = await GetUsageAsync(slot, cancellationToken);
            return await LoadNodesAsync(slot, usage, null, cancellationToken);
        }

        private async Task<List<ProxyNode>> LoadNodesAsync(
            ProviderSlotRef slot,
            ProviderUsage? usage,
            SlotLoadResult? result,
            CancellationToken cancellationToken)
        {
            if (usage is null || usage.SubUrl.Length == 0)
            {
                if (result is not null)
                {
                    result.Skipped++;
                }

                return new List<ProxyNode>();
            }

            var subscription = await ProviderClient.FetchSubscriptionAsync(usage.SubUrl, cancellationToken);
            if (subscription is null)
            {
                NodeDiagnostics.Log($"通道 {slot.Slot}：订阅拉取失败（供应商 sub 接口不可达）。");
                if (result is not null)
                {
                    result.Skipped++;
                }

                return new List<ProxyNode>();
            }

            NodeDiagnostics.Log($"通道 {slot.Slot}：订阅拉取成功，{subscription.Length} 字节。");

            var links = ClashSubscriptionParser.ToShareLinks(subscription);
            var nodes = new List<ProxyNode>();

            // 一个通道 = 供应商的一个账号，通道里所有节点共用这份用量，算一次即可。
            var usageText = DescribeUsage(usage);

            for (var index = 0; index < links.Count; index++)
            {
                // id 固定到「通道 + 序号」：刷新会换掉整条订阅（服务器地址、uuid 都可能变），
                // 按内容算出的 id 会跟着变，用户选中的节点就找不回来了。
                if (ShareLinkParser.TryParse(links[index], out var node, $"{slot.Slot}#{index}", slot.Slot)
                    && node is not null)
                {
                    node.UsageText = usageText;
                    nodes.Add(node);
                }
                else if (result is not null)
                {
                    result.Skipped++;
                }
            }

            if (links.Count == 0 && result is not null)
            {
                result.Skipped++;
            }

            return nodes;
        }

        // ------------------------------------------------------------ 判定

        /// <summary>
        /// 本地估算：这条通道是不是该去核实一次了。
        /// </summary>
        /// <param name="slotId">通道。</param>
        /// <param name="usedThisConnection">本次连接已用的字节（来自 Xray 统计）。</param>
        public bool NeedsVerification(string slotId, long usedThisConnection)
        {
            if (!_slots.TryGetValue(slotId, out var slot))
            {
                return false;
            }

            var usage = TryGetCachedUsage(slot.Uuid);

            // 没缓存就先去查一次：连着却不知道还剩多少，本身就是该核实的情况。
            if (usage is null)
            {
                return true;
            }

            if (usage.Expired)
            {
                return true;
            }

            if (usage.UnusedBytes is not null
                && usage.UnusedBytes.Value - usedThisConnection < TrafficThresholdBytes)
            {
                return true;
            }

            // 有效期粗判用注册时间推算，再加上这个通道的固定抖动。
            var remain = usage.ExpireAtUnix
                ?? ProviderLimits.EstimateExpireAtUnix(slot.RegisteredAtUnix);

            return remain - DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                <= (long)(TtlThreshold + JitterFor(slot.Slot)).TotalSeconds;
        }

        /// <summary>核实一次；确实将尽就换新账号。返回是否换成了新账号。</summary>
        public async Task<bool> VerifyAndRefreshAsync(
            string slotId,
            long usedThisConnection,
            CancellationToken cancellationToken = default)
        {
            if (!_slots.TryGetValue(slotId, out var slot))
            {
                return false;
            }

            var usage = await GetUsageAsync(slot, cancellationToken, force: true);

            if (!NeedsRefresh(usage, usedThisConnection))
            {
                // 服务端复核后如果也说没到阈值，会返回 NOT_EXHAUSTED。
                // 这里已经用真实用量刷新过缓存，下一轮不会再误判。
                return false;
            }

            return await RefreshAsync(slotId, cancellationToken) is not null;
        }

        /// <summary>
        /// 换掉一个通道。会消耗供应商的一个账号，所以只在确实将尽时才调，
        /// 失败也不重试——重试太勤等于烧账号，下一轮自然会再试。
        /// </summary>
        public async Task<ProviderSlotRef?> RefreshAsync(
            string slotId,
            CancellationToken cancellationToken = default)
        {
            if (!_slots.TryGetValue(slotId, out var slot))
            {
                return null;
            }

            var usage = TryGetCachedUsage(slot.Uuid);
            var updated = await NodeApi.RefreshSlotAsync(slotId, usage?.UsedBytes ?? 0, cancellationToken);

            if (updated is null)
            {
                return null;
            }

            _slots[slotId] = updated;
            _usage.Remove(slot.Uuid);
            await GetUsageAsync(updated, cancellationToken, force: true);

            return updated;
        }

        /// <summary>服务端会把这条判据再复核一遍，这里只是避免明显没必要的一次刷新。</summary>
        private static bool NeedsRefresh(ProviderUsage? usage, long usedThisConnection)
        {
            if (usage is null)
            {
                // 查不到（账号没了）就换：反正它也连不上。
                return true;
            }

            if (usage.Expired)
            {
                return true;
            }

            return usage.UnusedBytes is not null
                && usage.UnusedBytes.Value - usedThisConnection < TrafficThresholdBytes;
        }

        private static TimeSpan JitterFor(string slotId)
        {
            // 由 slot 名确定地推出 0~48 小时：同一台机器上每次算出的偏移相同。
            var hash = 0;
            foreach (var ch in slotId)
            {
                hash = (hash * 31 + ch) & 0x7fffffff;
            }

            return TimeSpan.FromSeconds(hash % (long)MaxJitter.TotalSeconds);
        }

        // ------------------------------------------------------------ 用量缓存

        private ProviderUsage? TryGetCachedUsage(string uuid)
        {
            if (_usage.TryGetValue(uuid, out var cached)
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - cached.FetchedAtUnix
                    < (long)UsageCacheTtl.TotalSeconds)
            {
                return cached.Usage;
            }

            return null;
        }

        private async Task<ProviderUsage?> GetUsageAsync(
            ProviderSlotRef slot,
            CancellationToken cancellationToken,
            bool force = false)
        {
            if (!force)
            {
                var cached = TryGetCachedUsage(slot.Uuid);
                if (cached is not null)
                {
                    return cached;
                }
            }

            var query = await ProviderClient.QueryUsageAsync(slot.Uuid, cancellationToken);
            if (query.Status != ProviderQueryStatus.Ok || query.Usage is null)
            {
                return null;
            }

            _usage[slot.Uuid] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds(), query.Usage);
            return query.Usage;
        }

        // ------------------------------------------------------------ 界面展示

        /// <summary>
        /// 节点上挂的用量标签，例如「1.2 GB / 5 GB」（已用 / 总量）。
        /// 取不到总量时返回空串 —— 界面据此不显示标签，好过显示一个误导性的数字。
        /// </summary>
        /// <param name="extraUsed">本地累计已用的额外字节（如本次会话实测流量），加到「已用」上。</param>
        private static string DescribeUsage(ProviderUsage? usage, long extraUsed = 0)
        {
            if (usage is null || usage.TransferEnableBytes is not { } total || total <= 0)
            {
                return string.Empty;
            }

            // 已用优先取供应商直接给的；没给就用「总量 − 剩余」倒推。
            var used = (usage.UsedBytes
                ?? (usage.UnusedBytes is { } unused ? Math.Max(0, total - unused) : 0))
                + extraUsed;

            // TrafficText.Format 会把 <=0 显示成 "--"；这里想看到实实在在的 0，所以单独处理。
            var usedText = used > 0 ? TrafficText.Format(used) : "0 B";

            return $"{usedText} / {TrafficText.Format(total)}";
        }

        /// <summary>
        /// 不请求供应商接口，直接基于「加载时缓存的基线用量」推算各通道的用量标签。
        ///
        /// 已用 = 基线已用（<see cref="ProviderUsage.UsedBytes"/>，加载 / 上次核实后缓存下来的值）
        ///        + 本地实测已用（<paramref name="localUsedBytes"/>，只对当前**正在连接**的通道加）。
        /// 总量仍取自基线。
        ///
        /// 这样既能让「已用流量」随本地实际消耗增长（打开节点页即刷新、有连通感），
        /// 又彻底不再为看一眼用量去打供应商接口——后者一个通道一次约 200KB 的 init 响应，
        /// 开页就打属于滥用。基线取「最近一次缓存」、**忽略 TTL**：宁可显示稍旧的基线，
        /// 也不要在缓存过期后让界面上的数字凭空消失。
        /// </summary>
        /// <param name="localUsedBytes">本地实测的本次会话累计流量（下载 + 上传）。</param>
        /// <param name="activeSlotId">当前正在连接的通道；只有它才叠加本地流量。</param>
        public Dictionary<string, string> ComputeUsageLabels(long localUsedBytes, string? activeSlotId)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var slot in _slots.Values)
            {
                var usage = LastKnownUsage(slot.Uuid);
                if (usage is null)
                {
                    continue;
                }

                var extra = string.Equals(slot.Slot, activeSlotId, StringComparison.Ordinal)
                    ? localUsedBytes
                    : 0;

                labels[slot.Slot] = DescribeUsage(usage, extra);
            }

            return labels;
        }

        /// <summary>取最近一次缓存的用量（不论是否过期）——仅用于界面展示，绝不触发网络请求。</summary>
        private ProviderUsage? LastKnownUsage(string uuid) =>
            _usage.TryGetValue(uuid, out var cached) ? cached.Usage : null;

        /// <summary>
        /// 通道的分组标题，例如「通道 1 · 剩余 4.2 GB」。
        /// 用它在节点页分组，用户才知道每个通道还剩多少——换账号是有成本的，不该是黑盒。
        /// </summary>
        public string DescribeSlot(string slotId)
        {
            if (slotId == ExtraSlotId)
            {
                return "备用节点";
            }

            var label = string.IsNullOrWhiteSpace(slotId) ? "其他" : $"通道 {IndexPart(slotId)}";

            if (!_slots.TryGetValue(slotId, out var slot))
            {
                return label;
            }

            var usage = TryGetCachedUsage(slot.Uuid);
            if (usage is null)
            {
                return label;
            }

            if (usage.Expired)
            {
                return label + " · 已到期";
            }

            var parts = new List<string>();
            if (usage.UnusedBytes is not null)
            {
                parts.Add("剩余 " + TrafficText.Format(usage.UnusedBytes));
            }

            if (usage.ExpireAtUnix is not null)
            {
                var days = (usage.ExpireAtUnix.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 86400;
                parts.Add(days <= 0 ? "今天到期" : $"{days} 天后到期");
            }

            return parts.Count == 0 ? label : label + " · " + string.Join(" · ", parts);
        }

        private static string IndexPart(string slotId) =>
            slotId.StartsWith("s", StringComparison.OrdinalIgnoreCase) && slotId.Length > 1
                ? slotId[1..]
                : slotId;
    }
}
