using System;
using System.Threading.Tasks;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>
    /// 断线自动重连。
    ///
    /// 订阅 <see cref="VpnConnectionService.UnexpectedDisconnect"/>——只有**非用户发起**的中断
    /// 才会走到这里（用户自己点断开、换号重连、退出应用都不算）。收到之后按指数退避重试，
    /// 用尽仍失败就明确告诉用户，而不是静默地停在一个「看起来还行」的状态上。
    ///
    /// 退避而不是立刻重连：掉线常常是因为网络本身断了，立刻重连只会连续失败、还可能触发
    /// 通道守卫去换号（换号是有成本的，会消耗供应商账号）。
    ///
    /// 每次重试前都检查一次状态：只要用户自己动手了（连上了 / 正在连 / 正在断），就立刻退出，
    /// 绝不和用户的操作抢方向盘。
    /// </summary>
    internal sealed class ConnectionSupervisor : IDisposable
    {
        /// <summary>重试间隔。总耗时约 31 秒，够一次短暂的网络抖动恢复，又不至于拖着不放。</summary>
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16)
        };

        private readonly VpnConnectionService _connection;
        private readonly ConnectionNotifier _notifier;
        private bool _isReconnecting;

        public ConnectionSupervisor(VpnConnectionService connection, ConnectionNotifier notifier)
        {
            _connection = connection;
            _notifier = notifier;
            connection.UnexpectedDisconnect += OnUnexpectedDisconnect;
        }

        private void OnUnexpectedDisconnect(object? sender, EventArgs e)
        {
            var reason = _connection.UnexpectedDisconnectReason ?? "连接意外中断。";

            // 无论开不开自动重连，都要告诉用户「掉了」——这是最需要被知道的时刻。
            _notifier.ReportUnexpectedDisconnect(reason);

            if (!AppSettings.Current.AutoReconnect || _isReconnecting)
            {
                return;
            }

            _ = ReconnectAsync();
        }

        private async Task ReconnectAsync()
        {
            var node = VpnSessionState.CurrentNode;
            if (node is null)
            {
                return;
            }

            _isReconnecting = true;
            try
            {
                for (var attempt = 0; attempt < Backoff.Length; attempt++)
                {
                    await Task.Delay(Backoff[attempt]).ConfigureAwait(false);

                    // 用户在这期间自己动手了：让他来，我们退出。
                    if (_connection.Status is VpnStatus.Connected or VpnStatus.Connecting or VpnStatus.Disconnecting)
                    {
                        return;
                    }

                    try
                    {
                        await _connection.ConnectAsync(node).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        NodeDiagnostics.LogException("自动重连", exception);
                    }

                    if (_connection.Status == VpnStatus.Connected)
                    {
                        _notifier.ReportAutoReconnected(_connection.TargetNode);
                        return;
                    }
                }

                _notifier.ReportAutoReconnectFailed();
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        public void Dispose() => _connection.UnexpectedDisconnect -= OnUnexpectedDisconnect;
    }
}
