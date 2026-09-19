using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Models;

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
    /// 正常路径是从远端拉取（见 <see cref="NodeFetcher"/>），
    /// 拉不到时退回本地文件 `%LOCALAPPDATA%\RayShuttle\nodes.enc`（主要供本地联调使用）。
    ///
    /// **设计上刻意不提供内置演示节点兜底。** 失败就是空列表：
    /// 曾经的兜底会让用户看到一堆并不存在的节点，还会误以为账号是好的。
    ///
    /// 两类失败必须分开（<see cref="NodeLoadOutcome"/>）：
    /// - 账号不存在（所有源都 404）或解不开 → 凭据问题，退回登录页；
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

        private NodeRepository()
        {
        }

        public IReadOnlyList<ProxyNode> Nodes { get; private set; } = Array.Empty<ProxyNode>();

        public IReadOnlyList<ServerGroup> Groups { get; private set; } = Array.Empty<ServerGroup>();

        /// <summary>失败或尚未加载时为 true。</summary>
        public bool HasNodes => Nodes.Count > 0;

        public string StatusMessage { get; private set; } = "尚未获取节点";

        public NodeLoadOutcome Outcome { get; private set; } = NodeLoadOutcome.NotLoaded;

        /// <summary>节点列表变化时触发，供已打开的页面刷新。</summary>
        public event EventHandler? NodesChanged;

        public async Task<NodeLoadOutcome> LoadAsync(
            byte[]? nodeKey,
            string? userName,
            CancellationToken cancellationToken = default)
        {
            if (nodeKey is null || string.IsNullOrWhiteSpace(userName))
            {
                ClearNodes(FailureMessage, NodeLoadOutcome.NotSignedIn);
                return Outcome;
            }

            // 拉取顺序由设置决定（默认主源优先）：当前源 3 次 → 另一个源 3 次 → 报错。
            var preference = AppSettings.Current.PreferredNodeSource;
            var fetch = await NodeFetcher.FetchAsync(userName, preference, cancellationToken);

            if (fetch.Success && fetch.Content is not null)
            {
                return ApplyRemotePayload(nodeKey, fetch.Content, fetch.Source?.Name ?? "远端");
            }

            // 所有源都明确说文件不存在 = 这个账号不存在，按凭据问题处理。
            if (fetch.Failure == NodeFetchFailure.NotFound)
            {
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            // 网络问题：不能据此判断凭据对错，先试本地文件。
            var localOutcome = await TryApplyLocalFileAsync(nodeKey, cancellationToken);
            if (localOutcome == NodeLoadOutcome.Loaded)
            {
                return localOutcome;
            }

            ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
            return Outcome;
        }

        /// <summary>远端拉取成功后的处理。解不开或读不出内容，都判定为凭据问题。</summary>
        private NodeLoadOutcome ApplyRemotePayload(byte[] nodeKey, byte[] content, string sourceName)
        {
            var json = NodeFileCrypto.TryDecryptToJson(nodeKey, content);

            if (json is null)
            {
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            if (!ParseAndApply(json, sourceName))
            {
                // 能解开却读不出节点：配置本身有问题，同样按凭据不可用处理。
                ClearNodes(FailureMessage, NodeLoadOutcome.Failed);
                return Outcome;
            }

            return Outcome;
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
