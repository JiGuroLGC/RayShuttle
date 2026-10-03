using System;
using Microsoft.UI.Dispatching;
using RayShuttle.Interop;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>
    /// 把连接相关的事件变成托盘气泡通知。
    ///
    /// 与 <see cref="ConnectionChime"/> 同构：构造时记下当时的初始状态，只在状态**真正切换**时发。
    /// 因此启动不会立刻弹一条「未连接」。
    ///
    /// 分工刻意划清：
    /// - 「连上了」由这里订阅 <see cref="VpnConnectionService.StatusChanged"/> 得到；
    /// - 「意外掉线 / 自动重连成功 / 重连失败」不是单纯的状态切换，由
    ///   <see cref="ConnectionSupervisor"/> 判定后调这里的 Report* 送进来。
    ///
    /// **用户主动点断开不发通知**——那是他自己点的，再弹一条纯属打扰。
    /// </summary>
    internal sealed class ConnectionNotifier : IDisposable
    {
        private readonly VpnConnectionService _connection;
        private readonly TrayIcon _tray;
        private readonly DispatcherQueue? _dispatcher;
        private VpnStatus _lastStatus;

        public ConnectionNotifier(VpnConnectionService connection, TrayIcon tray)
        {
            _connection = connection;
            _tray = tray;
            _lastStatus = connection.Status;
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            connection.StatusChanged += OnStatusChanged;
        }

        /// <summary>连接意外中断（内核退出 / 系统代理被改回去）。</summary>
        public void ReportUnexpectedDisconnect(string reason) =>
            Show("连接意外中断", reason, TrayNotificationLevel.Warning);

        /// <summary>自动重连成功。</summary>
        public void ReportAutoReconnected(ProxyNode? node) =>
            Show("已自动重连", node is null ? "连接已恢复。" : $"已重新连接到 {node.Name}。");

        /// <summary>自动重连尝试用尽仍未成功。</summary>
        public void ReportAutoReconnectFailed() =>
            Show(
                "自动重连失败",
                "多次尝试仍无法恢复连接，请检查网络后手动重连。",
                TrayNotificationLevel.Warning);

        private void OnStatusChanged(object? sender, EventArgs e)
        {
            var current = _connection.Status;
            var previous = _lastStatus;
            _lastStatus = current;

            if (current == previous || current != VpnStatus.Connected)
            {
                return;
            }

            var node = _connection.TargetNode;
            Show("已连接", node is null ? "流量隐于光" : $"已接入 {node.Name} · 流量隐于光");
        }

        private void Show(string title, string message, TrayNotificationLevel level = TrayNotificationLevel.Info)
        {
            // 开关在**发送这一刻**读，用户刚关掉就能立刻生效。
            if (!AppSettings.Current.NotificationsEnabled)
            {
                return;
            }

            // 状态可能在后台线程变化（通道守卫会自动换号重连），统一回 UI 线程发。
            if (_dispatcher is null || !_dispatcher.TryEnqueue(() => ShowCore(title, message, level)))
            {
                ShowCore(title, message, level);
            }
        }

        private void ShowCore(string title, string message, TrayNotificationLevel level)
        {
            try
            {
                _tray.ShowNotification(title, message, level);
            }
            catch (Exception)
            {
                // 通知发不出去绝不能影响连接流程。
            }
        }

        public void Dispose() => _connection.StatusChanged -= OnStatusChanged;
    }
}
