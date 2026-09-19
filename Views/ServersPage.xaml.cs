using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            NodeRepository.Current.NodesChanged += OnNodesChanged;

            RefreshList();
            EntranceAnimation.Run(HeaderBlock, NodeList);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) =>
            NodeRepository.Current.NodesChanged -= OnNodesChanged;

        private void OnNodesChanged(object? sender, EventArgs e) => RefreshList();

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void OnNodeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NodeList.SelectedItem is ProxyNode node)
            {
                VpnSessionState.CurrentNode = node;
            }
        }

        private void OnBestNodeClicked(object sender, RoutedEventArgs e)
        {
            var best = NodeRepository.Current.Nodes
                .Where(node => node.LatencyMs > 0)
                .OrderBy(node => node.LatencyMs)
                .FirstOrDefault()
                ?? NodeRepository.Current.Nodes.FirstOrDefault();

            if (best is null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                // 清空筛选会经由 TextChanged 重建列表，保证目标节点可见。
                SearchBox.Text = string.Empty;
            }

            NodeList.SelectedItem = best;
            NodeList.ScrollIntoView(best);
        }

        private void RefreshList()
        {
            // 失败时这里只会是一句「无法获取节点配置」，不含具体原因——
            // 区分「账号不存在」与「邀请码错误」等于告诉外人某个用户名是否已注册。
            SourceStatusText.Text = NodeRepository.Current.StatusMessage;

            ApplyFilter();

            // 让选中项与共享状态保持一致；列表里若已没有它，就落到第一个。
            var currentId = VpnSessionState.CurrentNode?.Id;
            var target = NodeRepository.Current.Nodes.FirstOrDefault(node => node.Id == currentId)
                ?? NodeRepository.Current.Nodes.FirstOrDefault();

            NodeList.SelectedItem = target;
            VpnSessionState.CurrentNode = target;
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
