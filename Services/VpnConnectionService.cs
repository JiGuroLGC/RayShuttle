using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    public enum VpnStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting,
        Failed
    }

    /// <summary>
    /// VPN 连接编排：把「生成配置 → 起内核 → 等就绪 → 接管流量」串成一条流程，
    /// 并保证任何一步失败都能干净地回滚。
    ///
    /// 几个刻意的顺序约束：
    /// - **内核确认就绪后才接管流量**。反过来的话，系统代理会有一段时间指向还没监听的端口，
    ///   用户在这段时间里是断网的。
    /// - **断开时先撤代理再停内核**，同样是为了避免「代理指向已关闭端口」的窗口期。
    /// - 连接与断开用信号量串行化，避免快速连点导致两条流程交错。
    /// </summary>
    public sealed class VpnConnectionService : IDisposable
    {
        private static readonly TimeSpan CoreStartupTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 流量接管失败时的提示。刻意写明「不需要管理员权限」，避免用户以为要靠提权解决。
        /// 真正的原因通常是别的代理工具在争抢同一份系统代理设置。
        /// </summary>
        public const string InterceptionFailureMessage =
            "未能接管系统流量：写入系统代理设置失败。常见原因是被其它代理工具（v2rayN / Clash 等）占用或覆盖。本应用写入 HKCU 代理设置不需要管理员权限，提权并不能解决这个问题。请退出其它代理工具后重试。若需接管不读取系统代理的程序（游戏、UWP 等），需要改用 TUN 全局模式，架构上已通过 ITrafficInterceptor 预留了接入点。";

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly XrayProcessRunner _runner = new();
        private readonly ITrafficInterceptor _interceptor = new SystemProxyInterceptor();
        private ConnectionStatsMonitor? _monitor;

        private int _statsPort;
        private bool _disposed;

        public static VpnConnectionService Current { get; } = new();

        private VpnConnectionService()
        {
        }

        public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;

        /// <summary>最近一次失败的可读原因，成功连接后清空。</summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// 当前的目标节点：开始连接时就设置，断开或失败后清空。
        /// 界面据此展示「正在连接哪个节点」，因此**不能等连接成功才赋值**。
        /// 是否真的连上了要看 <see cref="Status"/>。
        /// </summary>
        public ProxyNode? TargetNode { get; private set; }

        public int SocksPort { get; private set; }

        public int HttpPort { get; private set; }

        public DateTime? ConnectedAt { get; private set; }

        /// <summary>当前下载速率（字节/秒），由统计轮询更新；未连接时为 0。</summary>
        public long DownloadSpeedBps { get; private set; }

        /// <summary>当前上传速率（字节/秒），由统计轮询更新；未连接时为 0。</summary>
        public long UploadSpeedBps { get; private set; }

        /// <summary>实测链路延迟（毫秒）；未测得为 null。连接中先回退到节点文件里的标注值。</summary>
        public int? LatencyMs { get; private set; }

        /// <summary>接管方式名称，例如「系统代理」，用于界面展示。</summary>
        public string InterceptorName => _interceptor.Name;

        /// <summary>实时指标（速率 / 延迟）变化时触发，供首页刷新；可能在后台线程抛出。</summary>
        public event EventHandler? StatsChanged;

        /// <summary>
        /// 最近的内核输出。节点不可达、握手失败、服务端拒绝等**只会出现在这里**，
        /// 界面上看不到，因此排查连接失败时这是唯一线索。
        /// </summary>
        public string LastCoreOutput => _runner.LastOutput;

        public event EventHandler? StatusChanged;

        /// <summary>
        /// 应用启动时调用：上次若是异常退出，系统代理可能还指着已经关闭的本地端口，
        /// 此时用户是断网状态，必须先把网络恢复回来。
        /// </summary>
        public static void CleanupStaleState() => SystemProxyInterceptor.RestoreStaleOnStartup();

        public async Task<bool> ConnectAsync(ProxyNode node, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(node);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (Status is VpnStatus.Connected or VpnStatus.Connecting)
                {
                    return true;
                }

                LastError = null;
                // 先记下目标节点：界面在「连接中」就应该显示要连去哪儿。
                TargetNode = node;
                SetStatus(VpnStatus.Connecting);

                if (!XrayCoreLocator.IsAvailable)
                {
                    return Fail(XrayCoreLocator.DescribeMissing());
                }

                try
                {
                    // 每次连接都重新挑端口：上次的端口可能已被别的程序占用。
                    SocksPort = LocalPort.FindFree();
                    HttpPort = LocalPort.FindFree();
                    _statsPort = LocalPort.FindFree();

                    await WriteConfigAsync(node, cancellationToken);

                    _runner.Start(
                        XrayCoreLocator.ExecutablePath,
                        XrayCoreLocator.GeneratedConfigPath,
                        XrayCoreLocator.WorkingDirectory,
                        XrayCoreLocator.AssetDirectory);

                    if (!await _runner.WaitUntilReadyAsync(HttpPort, CoreStartupTimeout, cancellationToken))
                    {
                        await RollbackAsync();
                        return Fail(BuildStartupFailureMessage());
                    }

                    await _interceptor.ApplyAsync("127.0.0.1", HttpPort, cancellationToken);

                    // 接管失败必须当成连接失败处理。否则会出现「光球亮着、但流量根本没走代理」
                    // 这种最难排查的状态——用户以为是节点问题，实际上是系统代理没写进去。
                    if (!_interceptor.IsActive)
                    {
                        // 带上回读到的真实状态，用户一眼能看出是「没写进去」还是「被别的东西改回去了」。
                        var detail = SystemProxyManager.DescribeCurrent();
                        await RollbackAsync();
                        return Fail($"{InterceptionFailureMessage}\n当前系统代理：{detail}");
                    }

                    ConnectedAt = DateTime.Now;
                    StartMonitoring();
                    SetStatus(VpnStatus.Connected);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    await RollbackAsync();
                    SetStatus(VpnStatus.Disconnected);
                    throw;
                }
                catch (Exception exception)
                {
                    await RollbackAsync();
                    return Fail($"连接失败：{exception.Message}");
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (Status is VpnStatus.Disconnected)
                {
                    return;
                }

                SetStatus(VpnStatus.Disconnecting);
                await RollbackAsync();
                SetStatus(VpnStatus.Disconnected);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 退出应用时调用。这里是同步的，因为窗口关闭不会等待异步流程——
        /// 必须在这一刻就把系统代理撤掉，否则用户下次开机就是断网状态。
        /// </summary>
        public void ShutdownSynchronously()
        {
            try
            {
                _interceptor.RestoreAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 退出路径不允许抛异常。
            }

            StopMonitoring();
            _runner.Stop();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopMonitoring();
            _runner.Dispose();
            _gate.Dispose();
        }

        private async Task WriteConfigAsync(ProxyNode node, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(XrayCoreLocator.WorkingDirectory);

            var config = XrayConfigBuilder.Build(node, SocksPort, HttpPort, _statsPort);
            await File.WriteAllTextAsync(XrayCoreLocator.GeneratedConfigPath, config, cancellationToken);
        }

        /// <summary>
        /// 启动实时指标采集：从 Xray StatsService 读速率，并对目标节点做 TCP 延迟探测。
        /// 每次连接都新建一个 monitor，断开时在 <see cref="StopMonitoring"/> 里释放。
        /// </summary>
        private void StartMonitoring()
        {
            StopMonitoring();

            _monitor = new ConnectionStatsMonitor(
                "127.0.0.1",
                _statsPort,
                () => TargetNode is { } node ? (node.Address, node.Port) : null);

            _monitor.SpeedUpdated += OnSpeedUpdated;
            _monitor.LatencyUpdated += OnLatencyUpdated;
            _monitor.Start();
        }

        private void StopMonitoring()
        {
            var monitor = _monitor;
            _monitor = null;

            if (monitor is null)
            {
                return;
            }

            monitor.SpeedUpdated -= OnSpeedUpdated;
            monitor.LatencyUpdated -= OnLatencyUpdated;
            monitor.Dispose();

            DownloadSpeedBps = 0;
            UploadSpeedBps = 0;
            LatencyMs = null;
        }

        private void OnSpeedUpdated(long downloadBps, long uploadBps)
        {
            DownloadSpeedBps = downloadBps;
            UploadSpeedBps = uploadBps;
            StatsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnLatencyUpdated(int? latencyMs)
        {
            LatencyMs = latencyMs;
            StatsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 回滚到「未接管、无内核」的干净状态。
        /// 先撤代理再停内核，顺序不能反。
        /// </summary>
        private async Task RollbackAsync()
        {
            StopMonitoring();

            try
            {
                await _interceptor.RestoreAsync();
            }
            catch (Exception)
            {
                // 撤代理失败也要继续停内核，不能因为一步失败把后续步骤卡住。
            }

            _runner.Stop();

            TargetNode = null;
            ConnectedAt = null;
        }

        private bool Fail(string message)
        {
            LastError = message;
            // 失败后不该再显示「正在连接某某节点」。
            TargetNode = null;
            SetStatus(VpnStatus.Failed);
            return false;
        }

        /// <summary>只保留日志末尾若干行——用户需要的是最后那句报错，不是整个启动过程。</summary>
        private string BuildStartupFailureMessage()
        {
            var output = _runner.LastOutput;

            if (string.IsNullOrWhiteSpace(output))
            {
                return "Xray 内核启动失败，且没有输出日志。请确认 xray.exe 与节点配置是否正确。";
            }

            var tail = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd())
                .TakeLast(8);

            return "Xray 内核启动失败：\n" + string.Join('\n', tail);
        }

        private void SetStatus(VpnStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
