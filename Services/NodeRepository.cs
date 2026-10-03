using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Models;
using RayShuttle.Services.Api;
using RayShuttle.Services.Provider;

namespace RayShuttle.Services
{
    /// <summary>节点加载的结果。上层据此决定是提示用户、还是退回登录页。</summary>
    public enum NodeLoadOutcome
    {
        /// <summary>尚未加载。</summary>
        NotLoaded,

        /// <summary>成功拿到可用节点。</summary>
        Loaded,

        /// <summary>
        /// 没拿到可用配置：账号不存在、凭据解不开、或网络问题。
        ///
        /// 这几种**刻意不再细分行为差异**——曾经会在「凭据错误」时把用户登出，
        /// 但既然用户已经拿不到节点，保持登录状态并没有额外风险，而自动登出反而会让用户
        /// 在十几秒后被莫名其妙地踢回登录页。
        /// </summary>
        Failed,

        /// <summary>未登录，没有派生密钥。</summary>
        NotSignedIn
    }

    /// <summary>
    /// 获取并解密节点配置。
    ///
    /// 两类来源，按顺序尝试：
    ///   1. **云端 API**（`NodeApi`）：节点由服务端用「会话密钥」加密下发，
    ///      只需要登录态，不需要邀请码派生的密钥；
    ///   2. **本地文件** `%LOCALAPPDATA%\RayShuttle\nodes.enc`：仅本地联调 / 网络不通时的兜底，
    ///      用邀请码派生的密钥解密。
    ///
    /// 旧的「静态源」分发（主源 / 备用源，按 `{用户名}.txt` 从远端拉取）已彻底移除——
    /// 节点配置现在**只**来自新 API。
    ///
    /// **设计上刻意不提供内置演示节点兜底。** 失败就是空列表：
    /// 曾经的兜底会让用户看到一堆并不存在的节点，还会误以为账号是好的。
    ///
    /// 两类失败必须分开（<see cref="NodeLoadOutcome"/>）：
    /// - 账号不存在（云端明确说没有）或解不开 → 凭据问题，退回登录页；
    /// - 网络问题 → 只提示，**不登出**，因为网络失败证明不了凭据有问题。
    ///
    /// 对外的 <see cref="StatusMessage"/> 只有一句「无法获取节点配置」，
    /// **不暴露具体原因**：区分「账号不存在」与「邀请码错误」等于告诉外人某个用户名是否已注册。
    /// </summary>
    public sealed class NodeRepository
    {
        /// <summary>展示给用户的统一失败文案。不要往这里拼具体原因。</summary>
        public const string FailureMessage = "无法获取节点配置";

        /// <summary>本地兜底路径，也用于本地联调时手工放置文件。</summary>
        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle",
            "nodes.enc");

        public static NodeRepository Current { get; } = new();

        /// <summary>
        /// 两次「进节点页刷新用量」之间的最小间隔。
        /// 一个通道一次 init 请求、响应 200KB 以上，用户来回切页不该变成对供应商的连续轰炸。
        /// 想让它更实时/更省，改这一个值即可。
        /// </summary>
        private static readonly TimeSpan UsageRefreshInterval = TimeSpan.FromSeconds(30);

        private readonly SemaphoreSlim _usageRefreshGate = new(1, 1);

        private DateTime _lastUsageRefreshUtc = DateTime.MinValue;

        private NodeRepository()
        {
        }

        public IReadOnlyList<ProxyNode> Nodes { get; private set; } = Array.Empty<ProxyNode>();

        public IReadOnlyList<ServerGroup> Groups { get; private set; } = Array.Empty<ServerGroup>();

        /// <summary>失败或尚未加载时为 true。</summary>
        public bool HasNodes => Nodes.Count > 0;

        public string StatusMessage { get; private set; } = "尚未获取节点";

        public NodeLoadOutcome Outcome { get; private set; } = NodeLoadOutcome.NotLoaded;

        private bool _isLoading;

        /// <summary>是否正在拉取节点。页面据此显示「正在加载」而非「连接失败」。</summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value)
                {
                    return;
                }

                _isLoading = value;
                LoadStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>节点列表变化时触发，供已打开的页面刷新。</summary>
        public event EventHandler? NodesChanged;

        /// <summary>加载状态（<see cref="IsLoading"/>）变化时触发，供页面切换加载圈。</summary>
        public event EventHandler? LoadStateChanged;

        public async Task<NodeLoadOutcome> LoadAsync(
            byte[]? nodeKey,
            string? userName,
            CancellationToken cancellationToken = default)
        {
            var signedIn = AccountStore.Current.IsSignedIn;

            NodeDiagnostics.Reset();
            NodeDiagnostics.Log($"LoadAsync 开始：signedIn={signedIn}");

            IsLoading = true;

            try
            {
            if (signedIn)
            {
                // 云端 API：节点由服务端用「会话密钥」加密下发，只需要登录态，不需要邀请码派生的密钥。
                var api = await NodeApi.FetchAsync(cancellationToken);

                NodeDiagnostics.Log(
                    api.Success
                        ? $"云端取节点成功：token={(api.Token is null ? "null" : "有")}，content={(api.Content is null ? "null" : api.Content.Length + "字节")}"
                        : $"云端取节点失败：code={api.Code}，将回落到本地文件。");

                if (!api.Success)
                {
                    // 开发者诊断：对外文案统一为「无法获取节点配置」，但失败码（TOKEN_EXPIRED /
                    // NO_SESSION / 网络错误码等）对排查很有用，打印到调试输出，不影响用户可见文案。
                    System.Diagnostics.Debug.WriteLine(
                        $"[NodeRepository] 云端取节点失败：code={api.Code}，将回落到本地文件。");
                }

                if (api.Success && api.Content is not null && api.Token is not null
                    && await ApplyRemotePayload(NodeSessionKey.Derive(api.Token), api.Content, "云端") == NodeLoadOutcome.Loaded)
                {
                    return Outcome;
                }

                // 云端失败：没有邀请码派生的密钥就走不了本地文件，直接判失败。
                if (nodeKey is null || string.IsNullOrWhiteSpace(userName))
                {
                    NodeDiagnostics.Log("回落失败：缺少本地密钥（邀请码解不开或未登录），无法读本地文件。");
                    ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                    return Outcome;
                }

                // 回落到本地文件（仅供本地联调 / 网络不通时兜底）。
                var local = await TryApplyLocalFileAsync(nodeKey, cancellationToken);
                if (local == NodeLoadOutcome.Loaded)
                {
                    NodeDiagnostics.Log("本地文件 nodes.enc 加载成功。");
                    return local;
                }

                NodeDiagnostics.Log("本地文件 nodes.enc 不存在或解不开，彻底失败。");
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            // 未登录：只能用本地文件（需要邀请码派生的密钥）。
            if (nodeKey is null || string.IsNullOrWhiteSpace(userName))
            {
                ClearNodes(FailureMessage, NodeLoadOutcome.NotSignedIn);
                return Outcome;
            }

            var localOutcome = await TryApplyLocalFileAsync(nodeKey, cancellationToken);
            if (localOutcome == NodeLoadOutcome.Loaded)
            {
                return localOutcome;
            }

            ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
            return Outcome;
            }
            finally
            {
                // 无论成功、失败还是异常，加载结束都要复位，页面由此隐藏加载圈。
                IsLoading = false;
            }
        }

        /// <summary>
        /// 重新拉一遍各通道的已用流量，并**就地**更新节点上的用量标签。
        ///
        /// 节点页每次进入时调用：切页是最自然的刷新时机（此刻通常没在传数据、
        /// 也没在换通道）。**不重拉订阅**——订阅那一步会重建整个节点列表，
        /// 用户选中的节点、列表滚动位置都会被打断，而"已用流量变了吗"只需要一次用量请求。
        ///
        /// 就地更新靠的是 <see cref="ProxyNode.UsageText"/> 的 setter 会广播属性变化，
        /// 因此列表不需要重建，滚动位置与选中项都不动。
        ///
        /// 节流（<see cref="UsageRefreshInterval"/>）与重入都在这层挡掉，
        /// 页面可以放心地在每次 <c>Loaded</c> 里直接调。
        /// </summary>
        public async Task RefreshUsageAsync(CancellationToken cancellationToken = default)
        {
            if (Nodes.Count == 0)
            {
                return;
            }

            // 本地文件 / 纯分享链接那两条路径没有通道概念，也就没有用量可刷。
            if (!ProviderSlotStore.Current.HasSlots)
            {
                return;
            }

            if (DateTime.UtcNow - _lastUsageRefreshUtc < UsageRefreshInterval)
            {
                return;
            }

            if (!await _usageRefreshGate.WaitAsync(0, cancellationToken))
            {
                // 已经有一轮在跑（用户连续切页），等它自己结束即可。
                return;
            }

            try
            {
                _lastUsageRefreshUtc = DateTime.UtcNow;

                // 不再请求供应商接口：用加载时缓存的基线用量 + 本地实测会话流量推算（见
                // ProviderSlotStore.ComputeUsageLabels）。既让「已用流量」随本地消耗增长、
                // 打开节点页即刷新，又不滥用供应商 API。真正需要核实额度（将尽换号）的链路
                // 仍走 ProviderSlotStore 的核实接口，与这里无关。
                var connection = VpnConnectionService.Current;
                var localUsed = connection.SessionDownloadBytes + connection.SessionUploadBytes;
                var activeSlot = connection.TargetNode?.SlotId;
                var labels = ProviderSlotStore.Current.ComputeUsageLabels(localUsed, activeSlot);

                // **只就地改标签，不重建列表。** 用量标签绑的是 ProxyNode.UsageText，
                // 它的 setter 会广播属性变化，所以数字自己就刷新了；
                // 一旦在这里 RaiseNodesChanged，节点页会重建分组数据源，
                // 用户的滚动位置会被拉回顶部——为一个数字付这个代价不值得。
                foreach (var node in Nodes)
                {
                    if (labels.TryGetValue(node.SlotId, out var text))
                    {
                        node.UsageText = text;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 调用方取消（例如页面已离开），什么都不做。
            }
            finally
            {
                _usageRefreshGate.Release();
            }
        }

        /// <summary>云端下发的一种新载荷：只有通道凭据，节点要客户端自己去换。</summary>
        private const string ProviderSlotsKind = "provider-slots";

        /// <summary>远端拉取成功后的处理。解不开或读不出内容，都判定为凭据问题。</summary>
        private async Task<NodeLoadOutcome> ApplyRemotePayload(byte[] nodeKey, byte[] content, string sourceName)
        {
            var json = NodeFileCrypto.TryDecryptToJson(nodeKey, content);

            if (json is null)
            {
                NodeDiagnostics.Log("云端载荷解密失败：token 派生密钥与服务端不一致，或载荷损坏。");
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            NodeDiagnostics.Log($"云端载荷解密成功，kind={ReadKind(json)}。");

            // 两种载荷必须显式区分：通道凭据也是 JSON，只按「{ 开头」判断会走进节点分支，
            // 结果 nodes 为空、整份配置被判成坏数据。
            if (ReadKind(json) == ProviderSlotsKind)
            {
                return await ApplyProviderSlotsAsync(json, sourceName);
            }

            if (!ParseAndApply(json, sourceName))
            {
                // 能解开却读不出节点：配置本身有问题，同样按凭据不可用处理。
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            return Outcome;
        }

        private static string ReadKind(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    ? kind.GetString() ?? string.Empty
                    : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        /// <summary>按通道分组，标题用 ProviderSlotStore 的描述（含剩余额度）。</summary>
        private static IReadOnlyList<ServerGroup> GroupBySlot(IReadOnlyList<ProxyNode> nodes)
        {
            var groups = new List<ServerGroup>();
            var index = new Dictionary<string, ServerGroup>(StringComparer.Ordinal);

            foreach (var node in nodes)
            {
                var key = ProviderSlotStore.Current.DescribeSlot(node.SlotId);

                if (!index.TryGetValue(key, out var group))
                {
                    group = new ServerGroup(key, Enumerable.Empty<ProxyNode>());
                    index[key] = group;
                    groups.Add(group);
                }

                group.Add(node);
            }

            return groups;
        }

        /// <summary>
        /// 用通道凭据换节点：每个通道拿自己的 uuid 去供应商那儿取订阅，再解析成节点。
        ///
        /// 单个通道失败只跳过它自己——7 个里坏 1 个不该让用户一个节点都没有。
        /// </summary>
        private async Task<NodeLoadOutcome> ApplyProviderSlotsAsync(string json, string sourceName)
        {
            var slots = new List<ProviderSlotRef>();

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("slots", out var array)
                    && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        var slot = item.GetProperty("slot").GetString();
                        var uuid = item.GetProperty("uuid").GetString();

                        if (string.IsNullOrEmpty(slot) || string.IsNullOrEmpty(uuid))
                        {
                            continue;
                        }

                        slots.Add(new ProviderSlotRef
                        {
                            Slot = slot!,
                            Uuid = uuid!,
                            RegisteredAtUnix = item.TryGetProperty("registeredAt", out var at)
                                && at.ValueKind == JsonValueKind.Number
                                ? at.GetInt64()
                                : 0
                        });
                    }
                }
            }
            catch (JsonException)
            {
                slots.Clear();
            }
            catch (KeyNotFoundException)
            {
                slots.Clear();
            }
            catch (InvalidOperationException)
            {
                slots.Clear();
            }

            if (slots.Count == 0)
            {
                NodeDiagnostics.Log("云端返回的 slots 为空：账号未预注册通道（服务端应返回 503 NODE_UNAVAILABLE）。");
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            NodeDiagnostics.Log($"收到 {slots.Count} 个通道凭据，开始逐个换订阅。");

            var result = await ProviderSlotStore.Current.LoadAsync(slots, CancellationToken.None);
            var nodes = new List<ProxyNode>(result.Nodes);

            // 备用节点拼在通道节点后面。它们不走供应商，解析不出来就跳过，
            // 也不参与成功/失败判定——主流程该怎样还怎样。
            var extraSkipped = AppendExtraNodes(json, nodes);

            if (nodes.Count == 0)
            {
                NodeDiagnostics.Log("所有通道都未能取到任何节点（供应商订阅拉取全部失败）。");
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            NodeDiagnostics.Log($"节点加载完成：{nodes.Count} 个节点，跳过 {result.Skipped + extraSkipped} 个通道。");

            var totalSkipped = result.Skipped + extraSkipped;
            Nodes = nodes;

            // 按通道分组（而不是按地区）：一个通道 = 一个供应商账号，
            // 分组标题直接带上剩余额度，用户才知道哪个快没了。
            Groups = GroupBySlot(Nodes);
            Outcome = NodeLoadOutcome.Loaded;

            StatusMessage = result.Refreshed > 0
                ? $"已从{sourceName}加载 {Nodes.Count} 个节点，并更换了 {result.Refreshed} 个到期通道"
                : $"已从{sourceName}加载 {Nodes.Count} 个节点";

            if (totalSkipped > 0)
            {
                StatusMessage += $"，{totalSkipped} 个通道不可用";
            }

            RaiseNodesChanged();
            return Outcome;
        }

        /// <summary>
        /// 把服务端附带的备用节点（分享链接）解析出来，追加到列表末尾。返回跳过条数。
        ///
        /// 这些节点**没有用量、没有到期、不刷新**：`SlotId` 固定为 `extra`，
        /// 连接守卫在 `_slots` 里查不到它，于是天然不会触发任何刷新逻辑。
        /// </summary>
        private static int AppendExtraNodes(string json, List<ProxyNode> nodes)
        {
            var skipped = 0;

            try
            {
                using var document = JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty("extra", out var extra)
                    || !extra.TryGetProperty("links", out var links)
                    || links.ValueKind != JsonValueKind.Array)
                {
                    return 0;
                }

                var index = 0;
                foreach (var item in links.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        skipped++;
                        continue;
                    }

                    var link = item.GetString();

                    // id 同样固定到「extra#序号」，避免重启后选中项漂移。
                    if (ShareLinkParser.TryParse(link, out var node, $"extra#{index}", ProviderSlotStore.ExtraSlotId)
                        && node is not null)
                    {
                        nodes.Add(node);
                    }
                    else
                    {
                        skipped++;
                    }

                    index++;
                }
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return skipped;
        }

        private async Task<NodeLoadOutcome> TryApplyLocalFileAsync(
            byte[] nodeKey,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(FilePath))
            {
                return NodeLoadOutcome.Failed;
            }

            try
            {
                var blob = await File.ReadAllBytesAsync(FilePath, cancellationToken);
                var json = NodeFileCrypto.TryDecryptToJson(nodeKey, blob);

                // 本地文件可能属于别的账号，解不开不代表凭据有问题，这里只按「没拿到配置」处理。
                if (json is not null && ParseAndApply(json, "本地文件"))
                {
                    return NodeLoadOutcome.Loaded;
                }
            }
            catch (Exception)
            {
                // 读本地文件失败同样只说明「没拿到可用配置」。
            }

            return NodeLoadOutcome.Failed;
        }

        /// <summary>解析并应用。返回是否拿到了至少一个可用节点。</summary>
        private bool ParseAndApply(string payload, string sourceDescription)
        {
            return payload.TrimStart().StartsWith('{')
                ? ApplyJson(payload, sourceDescription)
                : ApplyShareLinks(payload, sourceDescription);
        }

        private bool ApplyJson(string json, string sourceDescription)
        {
            NodeFileDto? file;
            try
            {
                file = JsonSerializer.Deserialize(json, NodeFileJsonContext.Default.NodeFileDto);
            }
            catch (JsonException)
            {
                return false;
            }

            if (file?.Nodes is null || file.Nodes.Count == 0)
            {
                return false;
            }

            var nodes = new List<ProxyNode>(file.Nodes.Count);
            var skipped = 0;

            foreach (var dto in file.Nodes)
            {
                var node = NodeFactory.Create(dto);
                if (node is null)
                {
                    skipped++;
                    continue;
                }

                nodes.Add(node);
            }

            return ApplyNodes(nodes, skipped, sourceDescription);
        }

        private bool ApplyShareLinks(string payload, string sourceDescription)
        {
            var lines = payload.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var nodes = new List<ProxyNode>(lines.Length);
            var skipped = 0;

            foreach (var line in lines)
            {
                // 备注里可能有中文，链接本身也可能是 URL-safe Base64——这些都由解析器处理。
                if (ShareLinkParser.TryParse(line, out var node) && node is not null)
                {
                    nodes.Add(node);
                }
                else
                {
                    skipped++;
                }
            }

            return ApplyNodes(nodes, skipped, sourceDescription);
        }

        private bool ApplyNodes(List<ProxyNode> nodes, int skipped, string sourceDescription)
        {
            if (nodes.Count == 0)
            {
                return false;
            }

            Nodes = nodes;
            Groups = ProxyNodes.GroupByRegion(nodes);
            Outcome = NodeLoadOutcome.Loaded;
            StatusMessage = skipped == 0
                ? $"已从{sourceDescription}加载 {nodes.Count} 个节点"
                : $"已从{sourceDescription}加载 {nodes.Count} 个节点，跳过 {skipped} 条无法识别的条目";

            RaiseNodesChanged();
            return true;
        }

        /// <summary>失败时清空列表——不留任何内置或上一次的残留节点。</summary>
        private void ClearNodes(string message, NodeLoadOutcome outcome)
        {
            Nodes = Array.Empty<ProxyNode>();
            Groups = Array.Empty<ServerGroup>();
            Outcome = outcome;
            StatusMessage = message;

            RaiseNodesChanged();
        }

        private void RaiseNodesChanged() => NodesChanged?.Invoke(this, EventArgs.Empty);
    }
}
