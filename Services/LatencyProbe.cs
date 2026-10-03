using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>
    /// TCP 链路延迟探测的公共实现，供两处共用、保证口径一致：
    /// - 节点列表的批量测速（<see cref="NodeLatencyProber"/>）；
    /// - 连接期间的实时延迟（<see cref="ConnectionStatsMonitor"/>）。
    ///
    /// 相对「直接对 host:port 建连计时」，这里刻意剔除两类噪声：
    /// - **DNS 不计入耗时**：先把域名解析成 IP（结果缓存），之后只对 IP 计时。域名节点的冷解析
    ///   实测可达 150ms+，若计入会把延迟虚高一截；
    /// - **取多次建连的最小值**：单次测量受抖动影响大，取 min 更接近真实链路 RTT。
    ///
    /// 注意：它衡量的是「到服务器的一次 TCP 握手 RTT」，与 v2rayN「走代理的真实延迟」是两种
    /// 口径，数值本就不该直接相互比较。
    /// </summary>
    internal static class LatencyProbe
    {
        /// <summary>每次测量的建连次数（取其中最小值）。</summary>
        private const int Attempts = 3;

        /// <summary>单个节点的测量总超时（毫秒），多次建连共享该预算。</summary>
        private const int MeasureTimeoutMilliseconds = 2000;

        /// <summary>域名解析超时（毫秒）。单独限时，且不计入握手耗时。</summary>
        private const int ResolveTimeoutMilliseconds = 3000;

        /// <summary>
        /// 解析结果缓存时长：既避免每次探测都重新解析，又能跟随 DNS 变更 / 故障转移。
        /// </summary>
        private static readonly TimeSpan ResolveCacheLifetime = TimeSpan.FromMinutes(10);

        private static readonly ConcurrentDictionary<string, (string Ip, DateTimeOffset At)> ResolveCache = new();

        /// <summary>
        /// 测一次链路延迟（毫秒）。解析失败、建连失败或超时都返回 0（调用方按「未测得」处理）。
        /// </summary>
        public static async Task<int> MeasureAsync(string host, int port, CancellationToken cancellationToken)
        {
            var ip = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            if (ip is null)
            {
                return 0;
            }

            using var timeout = new CancellationTokenSource(MeasureTimeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var best = 0;
            for (var attempt = 0; attempt < Attempts && !linked.IsCancellationRequested; attempt++)
            {
                var measured = await ConnectOnceAsync(ip, port, linked.Token).ConfigureAwait(false);
                if (measured > 0 && (best == 0 || measured < best))
                {
                    best = measured;
                }
            }

            return best;
        }

        private static async Task<int> ConnectOnceAsync(string ip, int port, CancellationToken cancellationToken)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                using var client = new TcpClient();
                // ip 是字面量地址，此重载不会触发 DNS。
                await client.ConnectAsync(ip, port, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                return (int)stopwatch.ElapsedMilliseconds;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 把 host 解析成 IP；本身已是 IP 则原样返回。结果按 <see cref="ResolveCacheLifetime"/> 缓存。
        /// 解析失败返回 null（并清掉可能过期的缓存）。
        /// </summary>
        private static async Task<string?> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                return literal.ToString();
            }

            var now = DateTimeOffset.UtcNow;
            if (ResolveCache.TryGetValue(host, out var cached) && now - cached.At < ResolveCacheLifetime)
            {
                return cached.Ip;
            }

            try
            {
                using var timeout = new CancellationTokenSource(ResolveTimeoutMilliseconds);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

                var addresses = await Dns.GetHostAddressesAsync(host, linked.Token).ConfigureAwait(false);

                // 优先 IPv4：这些节点基本是 IPv4，也能避免双栈时先试 IPv6 带来的额外等待。
                var address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? (addresses.Length > 0 ? addresses[0] : null);

                if (address is null)
                {
                    ResolveCache.TryRemove(host, out _);
                    return null;
                }

                var ip = address.ToString();
                ResolveCache[host] = (ip, now);
                return ip;
            }
            catch
            {
                ResolveCache.TryRemove(host, out _);
                return null;
            }
        }
    }
}
