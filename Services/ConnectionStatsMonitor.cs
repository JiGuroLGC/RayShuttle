using System;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>
    /// 连接期间的实时指标采集：
    /// - 通过 <see cref="XrayStatsClient"/> 每秒轮询 Xray 的流量统计，算出当前下载 / 上传速率；
    /// - 周期性对目标节点 address:port 做 TCP 连接计时，得到真实的链路延迟。
    ///
    /// 两个循环各自独立计时。回调都发生在后台线程，订阅者需自行切回 UI 线程。
    /// </summary>
    internal sealed class ConnectionStatsMonitor : IDisposable
    {
        private readonly XrayStatsClient _client;
        private readonly Func<(string Host, int Port)?> _nodeSelector;
        private readonly PeriodicTimer _speedTimer = new(TimeSpan.FromSeconds(1));
        private readonly PeriodicTimer _latencyTimer = new(TimeSpan.FromSeconds(5));
        private readonly CancellationTokenSource _cts = new();

        private long _lastDownload;
        private long _lastUpload;
        private DateTime _lastSample;

        public event Action<long, long>? SpeedUpdated;
        public event Action<int?>? LatencyUpdated;

        /// <summary>
        /// 本次连接以来的总流量（下载 + 上传，字节）。
        ///
        /// 内核是随连接启动的，所以它的累计值就是「这条连接用了多少」——
        /// 通道要不要换新账号，本地侧就是拿它跟通道的剩余额度比。
        /// </summary>
        public long TotalBytes { get; private set; }

        /// <summary>本次连接以来的累计下载字节数。首页的「本次会话」用它。</summary>
        public long DownloadBytes { get; private set; }

        /// <summary>本次连接以来的累计上传字节数。</summary>
        public long UploadBytes { get; private set; }

        public ConnectionStatsMonitor(string statsHost, int statsPort, Func<(string Host, int Port)?> nodeSelector)
        {
            _client = new XrayStatsClient(statsHost, statsPort);
            _nodeSelector = nodeSelector;
        }

        public void Start()
        {
            _ = RunSpeedLoopAsync(_cts.Token);
            _ = RunLatencyLoopAsync(_cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _client.Dispose();
        }

        private async Task RunSpeedLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _speedTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        var traffic = await _client.QueryAsync(cancellationToken).ConfigureAwait(false);
                        var now = DateTime.UtcNow;

                        // 第一拍只建基线，避免把「进程启动以来累计的流量」算成一次瞬时速率尖峰。
                        if (_lastSample != default)
                        {
                            var seconds = (now - _lastSample).TotalSeconds;
                            if (seconds > 0)
                            {
                                var downloadBps = Math.Max(0, (long)((traffic.DownloadBytes - _lastDownload) / seconds));
                                var uploadBps = Math.Max(0, (long)((traffic.UploadBytes - _lastUpload) / seconds));
                                SpeedUpdated?.Invoke(downloadBps, uploadBps);
                            }
                        }

                        _lastDownload = traffic.DownloadBytes;
                        _lastUpload = traffic.UploadBytes;
                        _lastSample = now;
                        DownloadBytes = traffic.DownloadBytes;
                        UploadBytes = traffic.UploadBytes;
                        TotalBytes = traffic.DownloadBytes + traffic.UploadBytes;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // 内核尚未就绪或 gRPC 暂不可达：跳过本次采样，保留上次数值。
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RunLatencyLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _latencyTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        var node = _nodeSelector();
                        if (node is null)
                        {
                            continue;
                        }

                        // 6s 覆盖「域名解析(≤3s) + 建连测量(≤2s)」；解析结果有缓存，通常远快于此。
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

                        var latency = await MeasureTcpLatencyAsync(node.Value.Host, node.Value.Port, linked.Token)
                            .ConfigureAwait(false);
                        LatencyUpdated?.Invoke(latency);
                    }
                    catch (OperationCanceledException)
                    {
                        // 单次测量超时：本循环继续，下一轮再试。
                    }
                    catch
                    {
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// 对节点做一次链路延迟测量，作为真实链路延迟的近似。失败返回 null。
        /// 实现见 <see cref="LatencyProbe"/>：解析域名并缓存、只对 IP 计时、取多次最小值——
        /// 与节点列表的批量测速（<see cref="NodeLatencyProber"/>）共用同一套口径。
        /// </summary>
        private static async Task<int?> MeasureTcpLatencyAsync(string host, int port, CancellationToken cancellationToken)
        {
            var latency = await LatencyProbe.MeasureAsync(host, port, cancellationToken).ConfigureAwait(false);
            return latency > 0 ? latency : null;
        }
    }
}
