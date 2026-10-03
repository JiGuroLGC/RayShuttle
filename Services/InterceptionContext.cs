using System;
using System.IO;

namespace RayShuttle.Services
{
    /// <summary>
    /// 一次流量接管所需的信息。
    ///
    /// 原接口只传本地 HTTP 代理端口；TUN 需要的更多——SOCKS 端口（tun2socks 的上游）、
    /// 节点服务器地址（写 /32 路由防回环）。统一收进上下文，避免接口签名随实现膨胀。
    /// </summary>
    /// <param name="Host">本机入口地址（恒为 127.0.0.1）。</param>
    /// <param name="HttpPort">Xray HTTP 入站端口（系统代理用）。</param>
    /// <param name="SocksPort">Xray SOCKS 入站端口（TUN 用）。</param>
    /// <param name="ServerAddress">节点服务器地址（IP 或域名）。</param>
    /// <param name="NodeName">节点名，仅用于诊断报告。</param>
    public sealed record InterceptionContext(
        string Host,
        int HttpPort,
        int SocksPort,
        string ServerAddress,
        string NodeName);

    /// <summary>
    /// 接管通道的健康巡检结果。掉线原因由各实现给出——系统代理与 TUN 的
    /// 「掉了」完全是两回事，文案与排查路径都不同。
    /// </summary>
    public sealed record InterceptionHealth(bool Alive, string? Reason);
}
