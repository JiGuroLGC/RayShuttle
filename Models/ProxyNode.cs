using System.Collections.Generic;
using System.Linq;

namespace RayShuttle.Models
{
    /// <summary>节点使用的协议。均为 Xray 原生支持的主流协议。</summary>
    public enum NodeProtocol
    {
        Vmess,
        Vless,
        Trojan,
        Shadowsocks
    }

    /// <summary>传输层。默认 TCP，其余为 Xray 的 streamSettings.network 取值。</summary>
    public enum NodeTransport
    {
        Tcp,
        WebSocket,
        Grpc,
        HttpUpgrade,

        /// <summary>
        /// XHTTP。Xray 26 起的官方推荐传输方式，也是唯一没有被标记弃用的那个。
        /// 内部协议名仍叫 splithttp。
        /// </summary>
        Xhttp
    }

    /// <summary>
    /// 一个可连接的节点。字段直接对应 Xray outbound 配置所需的参数，
    /// 由加密节点文件解析而来（见 Services/NodeRepository）。
    /// </summary>
    public sealed class ProxyNode
    {
        /// <summary>节点标识，Xray 配置里用作 outbound tag，也用于记住用户选中的节点。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>显示名，通常是城市。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>所属分组，用于节点列表分段。</summary>
        public string Group { get; init; } = string.Empty;

        public string Country { get; init; } = string.Empty;

        public NodeProtocol Protocol { get; init; } = NodeProtocol.Vmess;

        public string Address { get; init; } = string.Empty;

        public int Port { get; init; }

        // ---- 凭据：按协议取用 ----

        /// <summary>VMess / VLESS 的用户 ID。</summary>
        public string Uuid { get; init; } = string.Empty;

        /// <summary>VMess 的 alterId。VMess AEAD 下固定为 0。</summary>
        public int AlterId { get; init; }

        /// <summary>VMess 加密方式，Xray 建议 auto。</summary>
        public string Security { get; init; } = "auto";

        /// <summary>VLESS 的 flow，例如 xtls-rprx-vision。留空表示不使用。</summary>
        public string Flow { get; init; } = string.Empty;

        /// <summary>Trojan / Shadowsocks 的密码。</summary>
        public string Password { get; init; } = string.Empty;

        /// <summary>Shadowsocks 的加密方法。</summary>
        public string Method { get; init; } = string.Empty;

        // ---- 传输层 ----

        public NodeTransport Transport { get; init; } = NodeTransport.Tcp;

        /// <summary>WebSocket / HTTPUpgrade 的路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>WebSocket / HTTPUpgrade 的 Host 头。</summary>
        public string Host { get; init; } = string.Empty;

        /// <summary>gRPC 的 serviceName。</summary>
        public string ServiceName { get; init; } = string.Empty;

        /// <summary>
        /// XHTTP 的 mode，取值 auto / packet-up / stream-up / stream-one。
        /// 留空表示用内核默认值（auto）。
        /// </summary>
        public string Mode { get; init; } = string.Empty;

        // ---- TLS ----

        public bool Tls { get; init; }

        /// <summary>TLS SNI。留空时回退到 Address。</summary>
        public string ServerName { get; init; } = string.Empty;

        /// <summary>是否跳过证书校验。**默认 false，不建议开启**，仅为自签证书场景保留。</summary>
        public bool AllowInsecure { get; init; }

        /// <summary>uTLS 指纹，例如 chrome。留空表示不使用。</summary>
        public string Fingerprint { get; init; } = string.Empty;

        // ---- 展示相关 ----

        /// <summary>延迟毫秒数，0 表示尚未测得。</summary>
        public int LatencyMs { get; init; }

        public bool IsRecommended { get; init; }

        public string LatencyText => LatencyMs <= 0 ? "--" : $"{LatencyMs} ms";

        /// <summary>延迟徽章配色，取值是 Themes/Brand.xaml 中的画笔键名。</summary>
        public string LatencyColorKey => LatencyMs switch
        {
            <= 0 => "TextTertiaryBrush",
            <= 80 => "StatusOkBrush",
            <= 180 => "StatusWarnBrush",
            _ => "StatusBadBrush"
        };

        public string Subtitle => string.IsNullOrEmpty(Country) ? Name : $"{Country} · {Name}";

        /// <summary>协议与传输的简短描述，列表里做次要信息展示。</summary>
        public string ProtocolText => Transport == NodeTransport.Tcp
            ? Protocol.ToString().ToUpperInvariant()
            : $"{Protocol.ToString().ToUpperInvariant()} · {TransportText}";

        private string TransportText => Transport switch
        {
            NodeTransport.WebSocket => "WS",
            NodeTransport.Grpc => "gRPC",
            NodeTransport.HttpUpgrade => "HTTPUpgrade",
            NodeTransport.Xhttp => "XHTTP",
            _ => "TCP"
        };
    }

    /// <summary>
    /// 按地区分组的节点集合。命名继承自 List，并暴露 Key，
    /// 以便 CollectionViewSource 的 IsSourceGrouped 直接识别分组名。
    /// </summary>
    public sealed class ServerGroup : List<ProxyNode>
    {
        public ServerGroup(string key, IEnumerable<ProxyNode> nodes)
            : base(nodes)
        {
            Key = key;
        }

        /// <summary>分组标题，同时作为分组键。</summary>
        public string GroupName => Key;

        public string Key { get; }

        public string CountText => $"{Count} 个节点";
    }

    /// <summary>节点的静态工具方法。</summary>
    public static class ProxyNodes
    {
        /// <summary>按 Group 字段分组，保持原有顺序。</summary>
        public static IReadOnlyList<ServerGroup> GroupByRegion(IEnumerable<ProxyNode> nodes)
        {
            var groups = new List<ServerGroup>();
            var index = new Dictionary<string, ServerGroup>();

            foreach (var node in nodes)
            {
                var key = string.IsNullOrWhiteSpace(node.Group) ? "其他" : node.Group;

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
    }
}
