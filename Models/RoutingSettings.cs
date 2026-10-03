using System.Collections.Generic;

namespace RayShuttle.Models
{
    /// <summary>分流模式。</summary>
    public enum RoutingMode
    {
        /// <summary>全局代理：除局域网与本机地址外，一律走代理。</summary>
        Global,

        /// <summary>智能分流：大陆的站点与地址直连，其余走代理。</summary>
        BypassMainland,

        /// <summary>只按用户自己写的规则分流（不套用内置清单）。</summary>
        Custom
    }

    /// <summary>
    /// 分流配置。随 <c>AppSettings</c> 一起落在 <c>settings.json</c> 里。
    ///
    /// 三份清单都是**用户自己写的**，形态与 Xray 的 routing 规则一致：
    /// - <see cref="DirectDomains"/> / <see cref="DirectAddresses"/>：强制直连；
    /// - <see cref="ProxyDomains"/>：强制走代理（用来把内置清单里误判的域名捞回来）。
    ///
    /// 域名按**后缀**匹配（写 `example.com` 就能命中 `www.example.com`），这是 Xray `domain:` 的语义。
    /// IP 支持单个地址与 CIDR 两种写法。
    /// </summary>
    public sealed class RoutingSettings
    {
        public RoutingMode Mode { get; set; } = RoutingMode.BypassMainland;

        /// <summary>强制直连的域名（后缀匹配）。</summary>
        public List<string> DirectDomains { get; set; } = new();

        /// <summary>强制直连的 IP 或 CIDR。</summary>
        public List<string> DirectAddresses { get; set; } = new();

        /// <summary>强制走代理的域名（优先级高于直连清单）。</summary>
        public List<string> ProxyDomains { get; set; } = new();

        /// <summary>深拷贝。设置页改完再整体写回，避免半改状态被落盘。</summary>
        public RoutingSettings Clone() => new()
        {
            Mode = Mode,
            DirectDomains = new List<string>(DirectDomains),
            DirectAddresses = new List<string>(DirectAddresses),
            ProxyDomains = new List<string>(ProxyDomains)
        };
    }
}
