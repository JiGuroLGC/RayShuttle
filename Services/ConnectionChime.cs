using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace RayShuttle.Services
{
    /// <summary>
    /// 连接 / 断开的提示音。
    ///
    /// 订阅 <see cref="VpnConnectionService.StatusChanged"/>，只在状态**真正发生切换**时响一声：
    ///   连上（Connecting → Connected）        → Assets/connect.wav（两声上行）
    ///   断开回未连接（Connected → Disconnected） → Assets/disconnect.wav（两声下行）
    /// 构造时的初始状态不算切换，所以启动不会响；连接失败也不响（失败由界面文案提示）。
    ///
    /// 播放用 winmm 的 <c>PlaySound</c>（SND_ASYNC）：一次调用即可，不必管理播放器对象。
    /// 但它依赖调用线程有消息队列，所以统一派发回 UI 线程再放。音效文件缺了也不会出声，
    /// 更不会影响连接流程。
    /// </summary>
    internal sealed class ConnectionChime : IDisposable
    {
        private const uint SndAsync = 0x0001;
        private const uint SndNoDefault = 0x0002;
        private const uint SndFilename = 0x00020000;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

        private readonly VpnConnectionService _connection;
        private readonly DispatcherQueue? _dispatcher;
        private VpnStatus _lastStatus;

        public ConnectionChime(VpnConnectionService connection)
        {
            _connection = connection;
            _lastStatus = connection.Status;
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            connection.StatusChanged += OnStatusChanged;
        }

        private void OnStatusChanged(object? sender, EventArgs e)
        {
            var current = _connection.Status;
            var previous = _lastStatus;
            _lastStatus = current;

            if (current == previous)
            {
                return;
            }

            if (current == VpnStatus.Connected)
            {
                Play("connect.wav");
            }
            else if (current == VpnStatus.Disconnected
                && previous is VpnStatus.Connected or VpnStatus.Disconnecting)
            {
                Play("disconnect.wav");
            }
        }

        /// <summary>状态可能在后台线程变化（通道守则会自动换号重连），统一回 UI 线程播。</summary>
        private void Play(string file)
        {
            // 开关在**播放这一刻**读，用户刚关掉就能立刻生效（不去缓存快照）。
            if (!AppSettings.Current.PlayConnectionSound)
            {
                return;
            }

            if (_dispatcher is null || !_dispatcher.TryEnqueue(() => PlayCore(file)))
            {
                PlayCore(file);
            }
        }

        private static void PlayCore(string file)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
                if (File.Exists(path))
                {
                    PlaySound(path, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault);
                }
            }
            catch (Exception)
            {
                // 没声音也不能影响连接流程。
            }
        }

        public void Dispose() => _connection.StatusChanged -= OnStatusChanged;
    }
}
