using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using RayShuttle.Common;
using RayShuttle.Models;
using RayShuttle.Services;

namespace RayShuttle.Views
{
    /// <summary>
    /// 首页：以连接光球为中心，展示连接状态与实时指标。
    ///
    /// 界面只反映**真实的连接状态**，不预先展示任何「选中的节点」：
    /// 未连接时一律显示「未连接」，连上之后才显示实际使用的那个节点。
    /// 这样用户看到的永远是事实，而不是一个可能并未生效的选择。
    /// </summary>
    public sealed partial class HomePage : Page
    {
        private readonly DispatcherTimer _durationTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        /// <summary>
        /// 当前可以在「查看详情」里展示的失败。
        ///
        /// 单独存一份而不是每次去读 <see cref="VpnConnectionService.LastFailure"/>：
        /// 「没有节点配置」这类失败不是连接服务产生的，连接服务那边是 null。
        /// </summary>
        private ConnectionFailure? _pendingFailure;

        public HomePage()
        {
            InitializeComponent();

            _durationTimer.Tick += OnDurationTick;
            VpnConnectionService.Current.StatusChanged += OnConnectionStatusChanged;
            VpnConnectionService.Current.StatsChanged += OnStatsChanged;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static VpnConnectionService Connection => VpnConnectionService.Current;

        private static NodeRepository Nodes => NodeRepository.Current;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            VpnSessionState.CurrentNodeChanged += OnCurrentNodeChanged;
            // 节点是登录后异步拉取的，本页可能先于拉取完成就打开了。
            Nodes.NodesChanged += OnNodesChanged;
            Nodes.LoadStateChanged += OnLoadStateChanged;

            ApplyNode();
            ApplyConnectionState();

            EntranceAnimation.Run(HeaderBlock, OrbBlock, MetricsBlock);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            VpnSessionState.CurrentNodeChanged -= OnCurrentNodeChanged;
            Nodes.NodesChanged -= OnNodesChanged;
            Nodes.LoadStateChanged -= OnLoadStateChanged;
            VpnConnectionService.Current.StatusChanged -= OnConnectionStatusChanged;
            VpnConnectionService.Current.StatsChanged -= OnStatsChanged;
            _durationTimer.Stop();
        }

        private void OnLoadStateChanged(object? sender, EventArgs e) => ApplyConnectionState();

        /// <summary>
        /// 速率 / 延迟由后台轮询线程抛出，必须切回 UI 线程再更新控件，否则会抛跨线程异常。
        /// </summary>
        private void OnStatsChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(ApplyStats);

        private void OnCurrentNodeChanged(object? sender, EventArgs e) => ApplyNode();

        private void OnConnectionStatusChanged(object? sender, EventArgs e) => ApplyConnectionState();

        private void OnNodesChanged(object? sender, EventArgs e)
        {
            ApplyNode();
            ApplyConnectionState();
        }

        /// <summary>
        /// 根据节点加载状态切换状态胶囊：正在同步时显示加载圈与「正在加载节点…」，
        /// 而不是「连接失败」——用户此时还没拿到节点，不应被误认为连接出错。
        /// </summary>
        private void ShowLoadingState()
        {
            StatusDot.Visibility = Visibility.Collapsed;
            StatusRing.Visibility = Visibility.Visible;
            StatusRing.IsActive = true;
            StatusText.Text = "正在加载节点…";
        }

        private void HideLoadingState()
        {
            StatusDot.Visibility = Visibility.Visible;
            StatusRing.Visibility = Visibility.Collapsed;
            StatusRing.IsActive = false;
        }

        private async void OnOrbClicked(object? sender, EventArgs e)
        {
            try
            {
                switch (Connection.Status)
                {
                    case VpnStatus.Disconnected:
                    case VpnStatus.Failed:
                        // 没有可用节点时不发起连接，直接把原因说清楚。
                        if (VpnSessionState.CurrentNode is not { } node)
                        {
                            ShowFailure(BuildNoNodesFailure());
                            return;
                        }

                        await Connection.ConnectAsync(node);
                        break;

                    case VpnStatus.Connected:
                        await Connection.DisconnectAsync();
                        break;

                    // 连接中 / 断开中忽略点击，避免流程交错。
                }
            }
            catch (Exception exception)
            {
                // 异步事件处理器里漏掉的异常会直接终止进程，必须兜住。
                // 这里**不往下走 ApplyConnectionState**：那会把刚弹出来的原因又覆盖掉。
                ShowFailure(new ConnectionFailure(
                    FailureKind.Unexpected,
                    "操作失败",
                    exception.Message,
                    "如果反复出现，请把诊断信息复制给开发者。",
                    exception.ToString()));
                return;
            }

            ApplyConnectionState();
        }

        /// <summary>「查看详情」：弹出分级说明与诊断报告。用户选「重试」就再连一次。</summary>
        private async void OnDetailsClicked(object sender, RoutedEventArgs e)
        {
            var failure = _pendingFailure ?? Connection.LastFailure;
            if (failure is null)
            {
                return;
            }

            if (!await ErrorDialog.ShowAsync(XamlRoot, failure))
            {
                return;
            }

            await RetryConnectAsync();
        }

        private async Task RetryConnectAsync()
        {
            if (VpnSessionState.CurrentNode is not { } node)
            {
                return;
            }

            try
            {
                await Connection.ConnectAsync(node);
            }
            catch (Exception)
            {
                // 失败原因由 Connection.LastFailure 承载，ApplyConnectionState 会把它显示出来。
            }

            ApplyConnectionState();
        }

        private void OnDurationTick(object? sender, object e) => UpdateDuration();

        private void OnChangeNodeClicked(object sender, RoutedEventArgs e)
        {
            // 用 Frame 直接跳转，主窗口会在 Navigated 里同步侧栏选中态。
            Frame.Navigate(typeof(ServersPage), null, new SuppressNavigationTransitionInfo());
        }

        /// <summary>
        /// 只显示**实际在用**的节点。未连接（含连接中、已断开）一律显示「未连接」，
        /// 不展示任何「选中的节点」——那会让用户误以为已经连上了某个节点。
        /// </summary>
        private void ApplyNode()
        {
            var node = Connection.TargetNode;

            if (node is null)
            {
                HeaderNodeText.Text = "未连接";
                NodeCityText.Text = "未连接";
                NodeDetailText.Visibility = Visibility.Collapsed;
                LatencyValue.Text = "--";
                return;
            }

            HeaderNodeText.Text = node.Name;
            NodeCityText.Text = node.Name;
            NodeDetailText.Text = node.Subtitle;
            NodeDetailText.Visibility = Visibility.Visible;
        }

        private void ApplyConnectionState()
        {
            ApplyNode();
            ApplyOrbAvailability();

            // 节点仍在线上同步、且当前未连接时，显示加载态而非「连接失败」——
            // 用户只是还没拿到节点，不应被误判为连接出错。
            if (Nodes.IsLoading && Connection.Status == VpnStatus.Disconnected)
            {
                ShowLoadingState();
                return;
            }

            HideLoadingState();

            switch (Connection.Status)
            {
                case VpnStatus.Connecting:
                    Orb.State = OrbState.Connecting;
                    StatusDot.Fill = ThemeResources.GetBrush("StatusWarnBrush");
                    StatusText.Text = "正在建立安全隧道…";
                    HideMessage();
                    HideDetails();
                    break;

                case VpnStatus.Connected:
                    Orb.State = OrbState.Connected;
                    StatusDot.Fill = ThemeResources.GetBrush("StatusOkBrush");
                    StatusText.Text = "已连接 · 流量隐于光";
                    HideDetails();

                    // 把真实的接管方式与本地端口报出来，用户需要据此手动配置其它程序。
                    ShowMessage(
                        $"已接管{Connection.InterceptorName} · HTTP 127.0.0.1:{Connection.HttpPort} · SOCKS5 127.0.0.1:{Connection.SocksPort}",
                        isError: false);

                    _durationTimer.Start();
                    ApplyStats();
                    UpdateDuration();
                    break;

                case VpnStatus.Disconnecting:
                    Orb.State = OrbState.Connecting;
                    StatusDot.Fill = ThemeResources.GetBrush("StatusWarnBrush");
                    StatusText.Text = "正在断开…";
                    break;

                case VpnStatus.Failed:
                    Orb.State = OrbState.Disconnected;
                    StatusDot.Fill = ThemeResources.GetBrush("StatusBadBrush");
                    StatusText.Text = "连接失败";
                    ShowFailure(Connection.LastFailure ?? BuildUnknownFailure());
                    ResetMetrics();
                    break;

                default:
                    Orb.State = OrbState.Disconnected;
                    StatusDot.Fill = ThemeResources.GetBrush("TextTertiaryBrush");
                    StatusText.Text = "未连接";
                    ResetMetrics();

                    // 没有节点就说明配置没拿到，此时应当直接给出失败提示，
                    // 而不是留一个压暗的光球让用户猜为什么点了没反应。
                    if (Nodes.HasNodes)
                    {
                        HideMessage();
                        HideDetails();
                    }
                    else
                    {
                        ShowFailure(BuildNoNodesFailure());
                    }

                    break;
            }
        }

        /// <summary>
        /// 显示一条失败：界面上只留**一句短结论**，完整内容（分级、建议、内核输出、诊断日志）
        /// 收进「查看详情」的弹窗里——首页不该被一大段红字占满，用户也没法把它交给开发者。
        /// </summary>
        private void ShowFailure(ConnectionFailure failure)
        {
            _pendingFailure = failure;
            ShowMessage(failure.Summary, isError: true);
            DetailsButton.Visibility = Visibility.Visible;
        }

        private void HideDetails()
        {
            _pendingFailure = null;
            DetailsButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>没拿到任何节点配置。对外文案保持统一，不区分「账号不存在」与「邀请码错误」。</summary>
        private static ConnectionFailure BuildNoNodesFailure() => new(
            FailureKind.NoNodes,
            "无法获取节点配置",
            NodeRepository.FailureMessage,
            "请确认网络可用、账号状态正常；若持续失败，请把诊断信息复制给开发者。");

        /// <summary>状态是 Failed 但没带上结构化原因时的兜底（理论上不该出现）。</summary>
        private static ConnectionFailure BuildUnknownFailure() => new(
            FailureKind.Unexpected,
            "连接失败",
            "连接失败，原因未知。",
            "请把诊断信息复制给开发者。");

        /// <summary>
        /// 没有可用节点时把光球压暗并停止响应，避免用户点了却不知道为什么没反应。
        /// 已连接时始终可点（点击是断开）。
        /// </summary>
        private void ApplyOrbAvailability()
        {
            var interactive = Nodes.HasNodes || Connection.Status == VpnStatus.Connected;

            Orb.IsHitTestVisible = interactive;
            Orb.Opacity = interactive ? 1 : 0.35;
            ChangeNodeButton.IsEnabled = Nodes.HasNodes;
        }

        private void UpdateDuration()
        {
            if (Connection.ConnectedAt is not { } startedAt)
            {
                DurationValue.Text = "--";
                return;
            }

            var elapsed = DateTime.Now - startedAt;
            DurationValue.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        /// <summary>
        /// 把当前连接上的实时指标刷到四张卡片。由 <see cref="OnStatsChanged"/>（后台线程）经
        /// DispatcherQueue 切回 UI 线程后调用，因此这里直接操作控件是安全的。
        /// 延迟优先显示实测值；还没测到时回退到节点文件里的标注值。
        /// </summary>
        private void ApplyStats()
        {
            DownloadValue.Text = FormatHelpers.FormatRate(Connection.DownloadSpeedBps);
            UploadValue.Text = FormatHelpers.FormatRate(Connection.UploadSpeedBps);
            LatencyValue.Text = Connection.LatencyMs.HasValue
                ? $"{Connection.LatencyMs} ms"
                : (Connection.TargetNode?.LatencyText ?? "--");
        }

        private void ResetMetrics()
        {
            // 下载/上传/延迟都来自真实统计探测：断开或未连接时回退占位，而不是编造数字。
            DownloadValue.Text = "--";
            UploadValue.Text = "--";
            DurationValue.Text = "--";
            LatencyValue.Text = "--";
        }

        private void ShowMessage(string message, bool isError)
        {
            MessageText.Text = message;
            MessageText.Foreground = ThemeResources.GetBrush(isError ? "StatusBadBrush" : "TextTertiaryBrush");
            MessageText.Visibility = Visibility.Visible;
        }

        private void HideMessage() => MessageText.Visibility = Visibility.Collapsed;
    }
}
