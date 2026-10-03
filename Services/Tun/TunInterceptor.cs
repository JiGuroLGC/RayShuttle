using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Models;

namespace RayShuttle.Services.Tun
{
    /// <summary>
    /// 通过 TUN 虚拟网卡接管**全部**流量（含不读系统代理的程序）。
    ///
    /// 链路：TUN 适配器（tun2socks 建自 wintun）→ tun2socks（gVisor 网络栈把包转成流）
    /// → Xray SOCKS 入站 → 节点。本类只做编排：撤系统代理（互斥）→ 让提权助手完成
    /// 「拉 tun2socks、配 IP、加路由」→ 健康巡检走 <see cref="CheckHealthAsync"/>。
    ///
    /// 失败一律抛 <see cref="TunInterceptionException"/>（携带现成的 <see cref="ConnectionFailure"/>），
    /// 由 <see cref="VpnConnectionService"/> 捕获并走统一的失败流程——接管失败必须当成连接失败，
    /// 绝不能出现「光球亮着但流量没走」。
    /// </summary>
    internal sealed class TunInterceptor : ITrafficInterceptor
    {
        /// <summary>适配器名（wintun 适配器名 = netsh 接口名）。</summary>
        public const string DeviceName = "RayShuttleTun";

        /// <summary>适配器 IP。198.18.0.0/15 是 benchmark 保留段，不与真实网络冲突。</summary>
        public const string AdapterIp = "198.18.0.1";
        public const string AdapterMask = "255.254.0.0";

        /// <summary>setup 要等适配器出现 + 配 IP + 加路由，给足余量。</summary>
        private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(40);

        /// <summary>
        /// 退出/断开路径的清理等待上限。正常清理不到 1 秒；超了就走兜底
        /// （父进程监视 + 退出守护 Job + 适配器随进程消失的路由自愈），不再等。
        /// 这个值就是用户点「退出」后 UI 可能冻结的最长时间，刻意取小。
        /// </summary>
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);

        private readonly SystemProxyInterceptor _systemProxy = new();
        private bool _applied;

        public string Name => "全局隧道 (TUN)";

        public bool IsActive => _applied;

        public async Task<bool> ApplyAsync(InterceptionContext context, CancellationToken cancellationToken)
        {
            _applied = false;

            if (!TunPaths.HelperAvailable)
            {
                throw new TunInterceptionException(new ConnectionFailure(
                    FailureKind.TunMissing,
                    "缺少 TUN 组件",
                    "未找到 TUN 提权助手。",
                    "请重新发布或安装本软件。",
                    $"期望路径：{TunPaths.HelperPath}"));
            }

            if (!TunPaths.Tun2SocksAvailable)
            {
                throw new TunInterceptionException(new ConnectionFailure(
                    FailureKind.TunMissing,
                    "缺少 TUN 组件",
                    "未找到 tun2socks 组件，无法把网卡流量转进代理。",
                    $"请从 tun2socks 官方 release 下载对应架构的 tun2socks.exe 放到 Core\\ 目录（见 Core\\TUN-README.txt）。",
                    $"期望路径：{TunPaths.Tun2SocksPath}"));
            }

            // 与系统代理互斥：TUN 接管全部流量，系统代理若还开着只会造成双重代理。
            // 原设置已由 SystemProxyInterceptor 备份，RestoreAsync 时恢复。
            await _systemProxy.DisableWithBackupAsync().ConfigureAwait(false);

            var args = new TunSetupArgs(
                DeviceName,
                TunPaths.Tun2SocksPath,
                $"socks5://127.0.0.1:{context.SocksPort}",
                AdapterIp,
                AdapterMask,
                context.ServerAddress);

            var response = await TunHelperClient.Current.SendAsync(
                new TunHelperRequest("setup", args, Environment.ProcessId),
                SetupTimeout,
                cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                throw new TunInterceptionException(new ConnectionFailure(
                    FailureKind.TunAdapter,
                    "TUN 隧道建立失败",
                    "建立虚拟网卡或写入路由表失败。",
                    "常见原因是安全软件拦截了路由修改或 tun2socks；请检查后重试。",
                    response.Error ?? string.Empty));
            }

            _applied = true;
            return true;
        }

        public async Task RestoreAsync()
        {
            _applied = false;

            // **尽力而为，绝不重新拉起助手**：管道不在 = 助手已死 = 适配器随之消失、
            // 路由自动失效，本来就没有需要清理的东西。断开与退出路径都走这里，
            // 若在这里拉新助手（还会弹 UAC），用户点个「断开」都要卡十几秒。
            // ConfigureAwait(false) 是**退出路径的生命线**：本方法会被
            // ShutdownSynchronously 在 UI 线程上同步等待——任何 await 的 continuation
            // 若捕获 UI 上下文，就会因 UI 线程被阻塞而永久死锁（踩过）。
            try
            {
                await TunHelperClient.Current.TrySendAsync(
                    new TunHelperRequest("cleanup", null, Environment.ProcessId),
                    CleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 助手不在/无响应时路由会随适配器消失自愈，这里不必阻塞断开流程。
            }

            try
            {
                await _systemProxy.RestoreAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 恢复系统代理失败也不阻塞断开（与系统代理模式的回滚策略一致）。
            }
        }

        public async Task<InterceptionHealth> CheckHealthAsync()
        {
            if (!_applied)
            {
                return new InterceptionHealth(true, null);
            }

            try
            {
                var response = await TunHelperClient.Current.SendAsync(
                    new TunHelperRequest("ping", null, Environment.ProcessId),
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None).ConfigureAwait(false);

                if (response.Ok && response.Detail == "tunnel-running")
                {
                    return new InterceptionHealth(true, null);
                }

                return new InterceptionHealth(false, "TUN 隧道已中断（tun2socks 进程退出），流量不再经过光梭。");
            }
            catch (Exception)
            {
                return new InterceptionHealth(false, "TUN 隧道已中断（助手无响应），流量不再经过光梭。");
            }
        }
    }
}
