using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RayShuttle.Common;
using RayShuttle.Models;
using RayShuttle.Services;

namespace RayShuttle.Views
{
    /// <summary>
    /// 节点页：按地区分组展示可选节点，支持关键字筛选与按延迟优选。
    ///
    /// 节点由主窗口在登录后拉取（见 <see cref="NodeRepository"/>）。
    /// 拉取是异步的，本页可能在拉取完成前就已经打开，因此这里订阅
    /// <see cref="NodeRepository.NodesChanged"/>，拿到结果后自动刷新。
    /// </summary>
    public sealed partial class ServersPage : Page
    {
        public ServersPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private bool _isProbing;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            NodeRepository.Current.NodesChanged += OnNodesChanged;
            NodeRepository.Current.LoadStateChanged += OnLoadStateChanged;

            RefreshList();
            EntranceAnimation.Run(HeaderBlock, NodeList);

            // 切到本页时把已用流量刷新一下。不 await：让它自己在后台跑完就地更新标签，
            // 页面该显示什么先显示什么。节流与重入由 NodeRepository 负责。
            _ = RefreshUsageAsync();
        }

        /// <summary>
        /// 进页面时刷新各通道的已用流量。
        ///
        /// 用量是锦上添花：失败（断网、供应商抖动）就沿用界面上已有的数字，
        /// 不该为它弹提示、更不该阻塞页面。
        /// </summary>
        private static async Task RefreshUsageAsync()
        {
            try
            {
                await NodeRepository.Current.RefreshUsageAsync();
            }
            catch (Exception)
            {
                // 这是尽力而为的刷新，失败不打扰用户。
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            NodeRepository.Current.NodesChanged -= OnNodesChanged;
            NodeRepository.Current.LoadStateChanged -= OnLoadStateChanged;
        }

        private void OnNodesChanged(object? sender, EventArgs e) => RefreshList();

        private void OnLoadStateChanged(object? sender, EventArgs e) => RefreshList();

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void OnNodeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NodeList.SelectedItem is ProxyNode node)
            {
                VpnSessionState.CurrentNode = node;
            }
        }

        /// <summary>
        /// 手动刷新延迟：仅当用户主动点击时才探测，避免每次打开页面都实时测一遍。
        /// 探测结果写回共享节点对象并缓存，下次打开直接读缓存。
        /// </summary>
        private async void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            await RunProbeAsync();
        }

        /// <summary>
        /// 智能优选：挑出延迟最低的节点 → 回首页 → 直接用它连上。
        ///
        /// 以前这里只是「在列表里选中」，用户还得自己再点一次连接，多此一举；
        /// 现在一键到位。（延迟取自启动时的探测 / 手动刷新写入的缓存。）
        /// </summary>
        private async void OnBestNodeClicked(object sender, RoutedEventArgs e)
        {
            var nodes = NodeRepository.Current.Nodes;
            if (nodes.Count == 0)
            {
                return;
            }

            var best = nodes
                .Where(node => node.LatencyMs > 0)
                .OrderBy(node => node.LatencyMs)
                .FirstOrDefault()
                ?? nodes.FirstOrDefault();

            if (best is not null)
            {
                await ConnectAndGoHomeAsync(best);
            }
        }

        /// <summary>
        /// 连续点同一个节点两次（即双击）= 直接用它连上，并回到首页看连接过程。
        /// 单击仍然只做选中，避免误触就连上。
        /// </summary>
        private async void OnNodeListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (FindNode(e.OriginalSource as DependencyObject) is { } node)
            {
                await ConnectAndGoHomeAsync(node);
            }
        }

        /// <summary>被点到的可能只是模板里某个小元素，所以要往上找到 ListViewItem 才能拿到节点。</summary>
        private static ProxyNode? FindNode(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is ListViewItem { Content: ProxyNode node })
                {
                    return node;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        /// <summary>选中指定节点并连上，同时跳回首页（连接状态、速率、失败原因都显示在首页）。</summary>
        private async Task ConnectAndGoHomeAsync(ProxyNode node)
        {
            VpnSessionState.CurrentNode = node;

            // 先回首页：连接过程与失败原因都在那儿显示。
            Frame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());

            var connection = VpnConnectionService.Current;
            try
            {
                // 若正连着别的节点，必须先断开：ConnectAsync 在已连接时会直接返回、什么也不做。
                if (connection.Status is VpnStatus.Connected or VpnStatus.Connecting
                    && !ReferenceEquals(connection.TargetNode, node))
                {
                    await connection.DisconnectAsync();
                }

                await connection.ConnectAsync(node);
            }
            catch (Exception)
            {
                // 失败原因由首页统一显示（VpnConnectionService.LastError），这里不再弹提示。
            }
        }

        private void RefreshList()
        {
            // 失败时这里只会是一句「无法获取节点配置」，不含具体原因——
            // 区分「账号不存在」与「邀请码错误」等于告诉外人某个用户名是否已注册。
            var repo = NodeRepository.Current;
            SourceStatusText.Text = _isProbing
                ? "正在刷新延迟…"
                : repo.StatusMessage;

            ApplyFilter();

            // 让选中项与共享状态保持一致；列表里若已没有它，就落到第一个。
            var currentId = VpnSessionState.CurrentNode?.Id;
            var target = repo.Nodes.FirstOrDefault(node => node.Id == currentId)
                ?? repo.Nodes.FirstOrDefault();

            NodeList.SelectedItem = target;
            VpnSessionState.CurrentNode = target;

            // 首次加载且暂无内容、或正在手动刷新延迟时，用加载层盖住列表。
            LoadingOverlay.Visibility = (repo.IsLoading && !repo.HasNodes) || _isProbing
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async Task RunProbeAsync()
        {
            if (_isProbing)
            {
                return;
            }

            var nodes = NodeRepository.Current.Nodes;
            if (nodes.Count == 0)
            {
                return;
            }

            _isProbing = true;
            RefreshList();

            // 延迟胶囊由 ProxyNode 的 INotifyPropertyChanged 就地刷新，无需逐节点重建列表。
            await NodeLatencyProber.ProbeAsync(nodes);

            _isProbing = false;
            DispatcherQueue.TryEnqueue(RefreshList);
        }

        /// <summary>按关键字重建分组数据源。节点实例本身是共享的，因此选中项不会丢失引用。</summary>
        private void ApplyFilter()
        {
            var keyword = SearchBox.Text?.Trim() ?? string.Empty;

            var groups = NodeRepository.Current.Nodes
                .Where(node => Matches(node, keyword))
                .ToList();

            GroupedNodes.Source = ProxyNodes.GroupByRegion(groups);
        }

        private static bool Matches(ProxyNode node, string keyword)
        {
            if (string.IsNullOrEmpty(keyword))
            {
                return true;
            }

            return node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || node.Country.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || node.Group.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || node.Address.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }
    }
}
