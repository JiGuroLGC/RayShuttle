using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Models;
using RayShuttle.Services.Provider;

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
        /// 连接期间检查通道是否将尽的间隔。取 1 分钟：额度单位是 GB 级，
        /// 早发现几十秒没有意义，而每次核实都要打一次 200KB 的 init。
        /// </summary>
        private static readonly TimeSpan SlotGuardInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// 流量接管失败（系统代理模式）时的建议文案。刻意写明「不需要管理员权限」，
        /// 避免用户以为要靠提权解决。真正的原因通常是别的代理工具在争抢同一份系统代理设置。
        ///
        /// 它属于「建议」而不是「结论」：界面上只显示一句短结论（见 <see cref="ConnectionFailure.Summary"/>），
        /// 这一长段收进诊断弹窗里。
        /// </summary>
        public const string InterceptionSuggestion =
            "常见原因是被其它代理工具（v2rayN / Clash 等）占用或覆盖。\n"
            + "本应用写入 HKCU 代理设置不需要管理员权限，提权并不能解决这个问题。\n"
            + "请退出其它代理工具后重试。\n"
            + "若需接管不读取系统代理的程序（游戏、UWP 等），请在设置中开启 TUN 全局模式。";

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly XrayProcessRunner _runner = new();
        private readonly SystemProxyInterceptor _systemProxy = new();
        private readonly Tun.TunInterceptor _tun = new();

        /// <summary>当前生效的接管实现。每次连接开始时按 TUN 开关重新选择。</summary>
        private ITrafficInterceptor _interceptor;
        private ConnectionStatsMonitor? _monitor;
        private CancellationTokenSource? _slotGuardCts;

        private int _statsPort;
        private bool _disposed;

        /// <summary>正在退出应用。退出时内核被停掉不算「意外掉线」，不该触发自动重连。</summary>
        private bool _shuttingDown;

        public static VpnConnectionService Current { get; } = new();

        private VpnConnectionService()
        {
            // 内核自己挂掉（崩溃 / 被任务管理器结束）也算意外掉线。
            _runner.ProcessExited += OnCoreProcessExited;
            _interceptor = _systemProxy;
        }

        public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;

        /// <summary>最近一次失败的结构化原因，成功连接后清空。完整内容进诊断弹窗。</summary>
        public ConnectionFailure? LastFailure { get; private set; }

        /// <summary>失败时首页显示的那一行短结论。等价于 <see cref="ConnectionFailure.Summary"/>。</summary>
        public string? LastError => LastFailure?.Summary;

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

        /// <summary>
        /// 本次会话累计下载 / 上传字节数。
        ///
        /// 内核是随连接启动的，所以它的累计值就是「这条连接用了多少」——断开后 monitor 被释放，
        /// 这两个值自然回到 0，不需要单独清。
        /// </summary>
        public long SessionDownloadBytes => _monitor?.DownloadBytes ?? 0;

        /// <inheritdoc cref="SessionDownloadBytes"/>
        public long SessionUploadBytes => _monitor?.UploadBytes ?? 0;

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
        /// **非用户发起**的连接中断：内核进程挂了，或系统代理被别的程序改了回去。
        ///
        /// 与 <see cref="StatusChanged"/> 分开是因为这两件事需要不同的反应：
        /// 状态变化照常刷新界面即可，而意外中断还要触发自动重连（见 ConnectionSupervisor）。
        /// 触发时清理已经做完（代理已撤、内核已停），订阅者可以直接重连。
        /// </summary>
        public event EventHandler? UnexpectedDisconnect;

        /// <summary>最近一次意外中断的原因，配合 <see cref="UnexpectedDisconnect"/> 使用。</summary>
        public string? UnexpectedDisconnectReason { get; private set; }

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

                LastFailure = null;
                // 先记下目标节点：界面在「连接中」就应该显示要连去哪儿。
                TargetNode = node;
                SetStatus(VpnStatus.Connecting);

                if (!XrayCoreLocator.IsAvailable)
                {
                    return Fail(new ConnectionFailure(
                        FailureKind.CoreMissing,
                        "缺少内核",
                        "未找到 Xray 内核，无法建立连接。",
                        XrayCoreLocator.DescribeMissing()));
                }

                try
                {
                    // 每次连接都重新挑端口：上次的端口可能已被别的程序占用。
                    SocksPort = LocalPort.FindFree();
                    HttpPort = LocalPort.FindFree();
                    _statsPort = LocalPort.FindFree();

                    // 设置与接管方式都在连接开始时确定：改了 TUN 开关必须断开重连才生效。
                    await AppSettings.Current.LoadAsync();
                    _interceptor = AppSettings.Current.TunMode ? _tun : _systemProxy;

                    await WriteConfigAsync(node, cancellationToken);

                    _runner.Start(
                        XrayCoreLocator.ExecutablePath,
                        XrayCoreLocator.GeneratedConfigPath,
                        XrayCoreLocator.WorkingDirectory,
                        XrayCoreLocator.AssetDirectory);

                    if (!await _runner.WaitUntilReadyAsync(HttpPort, CoreStartupTimeout, cancellationToken))
                    {
                        var failure = BuildStartupFailure();
                        await RollbackAsync();
                        return Fail(failure);
                    }

                    // 接管失败必须当成连接失败处理。否则会出现「光球亮着、但流量根本没走代理」
                    // 这种最难排查的状态——用户以为是节点问题，实际上是流量没被接管。
                    // 系统代理实现失败返回 false；TUN 实现失败抛 TunInterceptionException（见下）。
                    if (!await _interceptor.ApplyAsync(
                            new InterceptionContext("127.0.0.1", HttpPort, SocksPort, node.Address, node.Name),
                            cancellationToken)
                        || !_interceptor.IsActive)
                    {
                        // 带上回读到的真实状态，用户一眼能看出是「没写进去」还是「被别的东西改回去了」。
                        var detail = SystemProxyManager.DescribeCurrent();
                        await RollbackAsync();
                        return Fail(new ConnectionFailure(
                            FailureKind.Interception,
                            "无法接管系统流量",
                            "写入系统代理设置失败，流量没有走代理。",
                            InterceptionSuggestion,
                            $"当前系统代理：{detail}"));
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
                catch (Tun.TunInterceptionException exception)
                {
                    // TUN 的失败带着现成的结构化描述（分类 / 文案 / 建议），直接进失败体系。
                    await RollbackAsync();
                    return Fail(exception.Failure);
                }
                catch (Exception exception)
                {
                    NodeDiagnostics.LogException("ConnectAsync 同步流程", exception);
                    await RollbackAsync();
                    return Fail(new ConnectionFailure(
                        FailureKind.Unexpected,
                        "连接时发生异常",
                        $"连接过程中出现未预期的错误：{exception.Message}",
                        "如果反复出现，请把诊断信息复制给开发者。",
                        exception.ToString()));
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
        ///
        /// 等待放在**线程池**上而不是直接在调用线程（UI）上 GetResult()：
        /// TUN 的清理包含真正异步的管道 IO，其 await 若捕获 UI 上下文，continuation
        /// 会因 UI 线程被本调用阻塞而永久死锁（「托盘退出卡死」的根因）。
        /// Wait 带 5 秒上限：超时后照常退出，剩余清理由退出守护 Job 与
        /// 助手的父进程监视兜底。
        /// </summary>
        public void ShutdownSynchronously()
        {
            // 退出流程里停内核不算「意外掉线」。
            _shuttingDown = true;

            try
            {
                Task.Run(() => _interceptor.RestoreAsync())
                    .Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // 退出路径不允许抛异常：超时 / 取消 / 清理失败都继续走完退出。
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

            _shuttingDown = true;
            _disposed = true;
            _runner.ProcessExited -= OnCoreProcessExited;
            StopMonitoring();
            _runner.Dispose();
            _gate.Dispose();
        }

        private async Task WriteConfigAsync(ProxyNode node, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(XrayCoreLocator.WorkingDirectory);

            // 分流配置随每次连接写入：改了规则必须重连才生效（内核读的是启动那一刻的配置）。
            // TUN 模式给 SOCKS 入站开 sniffing（routeOnly）：从 TLS SNI / HTTP Host 还原域名，
            // 让基于域名的分流规则在「包里只有 IP」的 TUN 链路上继续可用。
            var config = XrayConfigBuilder.Build(
                node, SocksPort, HttpPort, _statsPort,
                AppSettings.Current.Routing,
                enableSniffing: AppSettings.Current.TunMode);
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

            StartSlotGuard();
        }

        // ---------------------------------------------------------- 通道守卫
        //
        // 连接期间定期判断「当前这条通道是不是快没了」。判断只是触发，
        // 真正的判据在服务端——它会拿供应商的真实用量再复核一遍。
        //
        // 换通道不需要先断开：供应商 API 在国内可直连，走的是不走系统代理的 HttpClient。
        // 只有**当前正在用的那条**被换掉时才需要断开重连。

        private void StartSlotGuard()
        {
            StopSlotGuard();

            _slotGuardCts = new CancellationTokenSource();
            _ = RunSlotGuardAsync(_slotGuardCts.Token);
        }

        private void StopSlotGuard()
        {
            var cts = _slotGuardCts;
            _slotGuardCts = null;

            if (cts is null)
            {
                return;
            }

            cts.Cancel();
            cts.Dispose();
        }

        private async Task RunSlotGuardAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(SlotGuardInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (Status != VpnStatus.Connected || TargetNode is not { } node)
                    {
                        continue;
                    }

                    // 顺带巡检：接管通道是否还活着（多态）——系统代理模式查注册表是否被
                    // 别的代理工具改回去；TUN 模式查隧道进程是否存活。从前这种情况完全
                    // 无感知——内核还活着、光球还亮着，但流量早就不走我们了。
                    // 现在把它当成意外掉线处理。
                    var health = await _interceptor.CheckHealthAsync().ConfigureAwait(false);
                    if (!health.Alive)
                    {
                        HandleUnexpectedDisconnect(health.Reason ?? "流量接管通道已中断。");
                        return;
                    }

                    if (string.IsNullOrEmpty(node.SlotId))
                    {
                        continue;
                    }

                    var used = _monitor?.TotalBytes ?? 0;
                    if (!ProviderSlotStore.Current.NeedsVerification(node.SlotId, used))
                    {
                        continue;
                    }

                    // 核实 + 必要时换新账号。服务端说「还没到阈值」时这里返回 false，
                    // 缓存已被真实用量刷新，下一轮不会再误判。
                    var refreshed = await ProviderSlotStore.Current
                        .VerifyAndRefreshAsync(node.SlotId, used, cancellationToken)
                        .ConfigureAwait(false);

                    if (!refreshed || Status != VpnStatus.Connected)
                    {
                        continue;
                    }

                    // 换掉的是当前这条：取新节点再连一次。
                    if (!ReferenceEquals(TargetNode, node))
                    {
                        continue;
                    }

                    var candidates = await ProviderSlotStore.Current
                        .LoadNodesAsync(node.SlotId, cancellationToken)
                        .ConfigureAwait(false);

                    var next = candidates.FirstOrDefault();
                    if (next is null)
                    {
                        continue;
                    }

                    await DisconnectAsync().ConfigureAwait(false);
                    await ConnectAsync(next, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                // 守卫出错不该把连接搞掉：下一轮再试。
                NodeDiagnostics.LogException("SlotGuard 后台循环", exception);
            }
        }

        // ---------------------------------------------------------- 意外掉线

        private void OnCoreProcessExited(object? sender, EventArgs e) =>
            HandleUnexpectedDisconnect("Xray 内核进程意外退出。");

        /// <summary>
        /// 连接掉了、而且不是用户要求的。先做清理再抛事件——订阅者（自动重连）拿到的
        /// 必须是一个已经撤掉代理、停掉内核的干净状态，否则重连时会撞上残留的代理设置。
        ///
        /// 清理放在后台线程：这个方法可能在进程退出的回调线程、也可能在守卫循环里被调用，
        /// 而清理要走 <see cref="_gate"/>，在那些线程上同步等待会有死锁风险。
        /// </summary>
        private void HandleUnexpectedDisconnect(string reason)
        {
            if (_shuttingDown || Status != VpnStatus.Connected)
            {
                return;
            }

            UnexpectedDisconnectReason = reason;
            NodeDiagnostics.Log($"意外掉线：{reason}");

            // 掉线瞬间就必须撤代理：内核已经没了，系统代理还指着死端口的话用户是直接断网的。
            _ = Task.Run(async () =>
            {
                try
                {
                    await DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    NodeDiagnostics.LogException("意外掉线后的清理", exception);
                }

                UnexpectedDisconnect?.Invoke(this, EventArgs.Empty);
            });
        }

        /// <summary>
        /// 系统代理此刻是否仍然指向本机内核。
        ///
        /// **不能只看 <c>_interceptor.IsActive</c>**——那是接管成功时记下的缓存值，
        /// 别的代理工具把注册表改回去之后它照样是 true。
        /// 现在该逻辑已移入 <see cref="SystemProxyInterceptor.CheckHealthAsync"/>，
        /// 通过接口多态分发（TUN 模式检查的是隧道进程存活）。
        /// </summary>
        private void StopMonitoring()
        {
            StopSlotGuard();

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

        private bool Fail(ConnectionFailure failure)
        {
            LastFailure = failure;
            NodeDiagnostics.Log($"连接失败[{failure.KindText}] {failure.Summary}");
            // 失败后不该再显示「正在连接某某节点」。
            TargetNode = null;
            SetStatus(VpnStatus.Failed);
            return false;
        }

        /// <summary>
        /// 内核没能就绪（超时 / 进程提前退出）。只保留日志末尾若干行——
        /// 用户与开发者需要的是最后那句报错，不是整个启动过程。
        /// </summary>
        private ConnectionFailure BuildStartupFailure()
        {
            var output = _runner.LastOutput;

            if (string.IsNullOrWhiteSpace(output))
            {
                return new ConnectionFailure(
                    FailureKind.CoreStartup,
                    "内核启动失败",
                    "Xray 内核未能启动，且没有任何输出。",
                    "请确认 Core\\xray.exe 与生成的节点配置是否正确。",
                    $"配置路径：{XrayCoreLocator.GeneratedConfigPath}");
            }

            var tail = string.Join(
                '\n',
                output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd())
                    .TakeLast(8));

            return new ConnectionFailure(
                FailureKind.CoreStartup,
                "内核启动失败",
                "Xray 内核未能启动：超时或进程提前退出。",
                "多半是节点配置有问题（协议参数、TLS、传输方式不被内核接受）。",
                tail);
        }

        private void SetStatus(VpnStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
