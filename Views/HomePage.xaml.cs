using System;
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

            ApplyNode();
            ApplyConnectionState();

            EntranceAnimation.Run(HeaderBlock, OrbBlock, MetricsBlock);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            VpnSessionState.CurrentNodeChanged -= OnCurrentNodeChanged;
            Nodes.NodesChanged -= OnNodesChanged;
            VpnConnectionService.Current.StatusChanged -= OnConnectionStatusChanged;
            VpnConnectionService.Current.StatsChanged -= OnStatsChanged;
            _durationTimer.Stop();
        }

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
                            ShowMessage($"连接失败：{Nodes.StatusMessage}", isError: true);
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
                ShowMessage($"操作失败：{exception.Message}", isError: true);
            }

            ApplyConnectionState();
        }

        private void OnDurationTick(object? sender, object e) => UpdateDuration();

        private void OnChangeNodeClicked(object sender, RoutedEventArgs e)
        {
            // 用 Frame 直接跳转，主窗口会在 Navigated 里同步侧栏选中态。
            Frame.Navigate(typeof(ServersPage), null, new EntranceNavigationTransitionInfo());
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

            switch (Connection.Status)
            {
                case VpnStatus.Connecting:
                    Orb.State = OrbState.Connecting;
                    StatusDot.Fill = ThemeResources.GetBrush("StatusWarnBrush");
                    StatusText.Text = "正在建立安全隧道…";
                    HideMessage();
                    break;

                case VpnStatus.Connected:
                    Orb.State = OrbState.Connected;
                    StatusDot.Fill = ThemeResources.GetBrush("StatusOkBrush");
                    StatusText.Text = "已连接 · 流量已加密";

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
                    ShowMessage(Connection.LastError ?? "连接失败，原因未知。", isError: true);
                    ResetMetrics();
                    break;

                default:
                    Orb.State = OrbState.Disconnected;
                    StatusDot.Fill = ThemeResources.GetBrush("TextTertiaryBrush");
                    StatusText.Text = "未连接";
                    ResetMetrics();

                    // 没有节点就说明配置没拿到，此时应当直接给出连接失败的提示。
                    if (Nodes.HasNodes)
                    {
                        HideMessage();
                    }
                    else
                    {
                        ShowMessage($"连接失败：{Nodes.StatusMessage}", isError: true);
                    }

                    break;
            }
        }

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
