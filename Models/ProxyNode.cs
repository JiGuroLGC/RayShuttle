using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

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
    ///
    /// 除 <see cref="LatencyMs"/> / <see cref="IsRecommended"/> 外均为不可变：
    /// 延迟是连接后续测出来的，需要回写；其余由解析一次性确定。
    /// 实现 <see cref="INotifyPropertyChanged"/> 让节点列表的胶囊能就地刷新延迟，无需重建列表。
    /// </summary>
    public sealed class ProxyNode : INotifyPropertyChanged
    {
        /// <summary>节点标识，Xray 配置里用作 outbound tag，也用于记住用户选中的节点。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// 所属通道（s1…s7）。一个通道 = 供应商的一个账号 = 5GB + 7 天。
        ///
        /// 通道内的节点会随刷新整组换掉（换账号就换订阅），所以**选中项必须按通道记**，
        /// 不能按节点内容记——否则刷新一次，用户选中的节点就找不回来了。
        /// </summary>
        public string SlotId { get; init; } = string.Empty;

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

        /// <summary>uTLS 指纹，例如 chrome / ios。留空表示不使用。</summary>
        public string Fingerprint { get; init; } = string.Empty;

        /// <summary>
        /// TLS ALPN 列表（逗号分隔，例如 "h2,http/1.1"）。留空表示不显式指定。
        /// 部分 CDN 前置的 ws / grpc + TLS 节点要求客户端声明 ALPN，缺失会握手失败。
        /// </summary>
        public string Alpn { get; init; } = string.Empty;

        /// <summary>
        /// 是否使用 REALITY（分享链接 `security=reality`）。
        /// REALITY **不是**普通 TLS：必须写 <c>realitySettings</c> 而非 <c>tlsSettings</c>，
        /// 且依赖 <see cref="PublicKey"/>。漏掉时握手会被服务端当成「未授权探测」转发到 fallback，
        /// 表现为浏览器 ERR_SSL_PROTOCOL_ERROR、流量几乎为零。
        /// </summary>
        public bool Reality { get; init; }

        /// <summary>REALITY 的 publicKey（分享链接 `pbk=`）。REALITY 下必填。</summary>
        public string PublicKey { get; init; } = string.Empty;

        /// <summary>REALITY 的 shortId（分享链接 `sid=`）。可空。</summary>
        public string ShortId { get; init; } = string.Empty;

        /// <summary>REALITY 的 spiderX（分享链接 `spx=`）。留空由内核默认 "/"。</summary>
        public string SpiderX { get; init; } = string.Empty;

        // ---- 展示相关 ----

        private int _latencyMs;

        /// <summary>延迟毫秒数，0 表示尚未测得。由延迟探测回写，因此可写并通知。</summary>
        public int LatencyMs
        {
            get => _latencyMs;
            set
            {
                if (_latencyMs == value)
                {
                    return;
                }

                _latencyMs = value;
                // 广播全部属性：列表胶囊绑定的是 LatencyText / LatencyColorKey 等计算方法，
                // 它们依赖 LatencyMs，但单独的属性名通知不会触发其重算，故直接全量刷新。
                OnPropertyChanged(null);
            }
        }

        private bool _isRecommended;

        /// <summary>是否为综合延迟最低的「推荐」节点。探测完成后标记，因此可写并通知。</summary>
        public bool IsRecommended
        {
            get => _isRecommended;
            set
            {
                if (_isRecommended == value)
                {
                    return;
                }

                _isRecommended = value;
                OnPropertyChanged(null);
            }
        }

        private string _usageText = string.Empty;

        /// <summary>
        /// 所属通道的用量标签，形如「1.2 GB / 5 GB」（已用 / 总量）。
        ///
        /// 一个通道 = 供应商的一个账号，所以通道内的节点共用同一份用量。
        /// 节点是先解析出来、之后才由 <see cref="Services.Provider.ProviderSlotStore"/>
        /// 补上这个字段，因此它可写并会通知界面。
        /// 取不到用量时是空串，界面据此不显示标签（本地文件 / 备用节点就是这种）。
        /// </summary>
        public string UsageText
        {
            get => _usageText;
            set
            {
                if (_usageText == value)
                {
                    return;
                }

                _usageText = value;
                OnPropertyChanged(null);
            }
        }

        /// <summary>是否拿到了用量，决定标签显示与否。</summary>
        public bool HasUsage => !string.IsNullOrEmpty(_usageText);

        private string? _regionCode;

        /// <summary>
        /// 推断出的国家/地区代码（ISO 3166-1 alpha-2，小写，例如 hk），用于显示旗帜；
        /// 认不出来是空串（界面回退到品牌光点）。见 <see cref="RegionCatalog"/>。
        ///
        /// 只依据 <see cref="Name"/> / <see cref="Country"/> / <see cref="Group"/>，它们都是
        /// init 后不变的，所以算一次就缓存住——列表里每个节点会被反复取这个属性。
        /// </summary>
        public string RegionCode => _regionCode ??= RegionCatalog.Resolve(Name, Country, Group);

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

        /// <summary>属性变化时触发；传 null 让所有绑定就地重算（LatencyText 等是计算方法）。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
                var key = string.IsNullOrWhiteSpace(node.Group) ? "优选" : node.Group;

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
