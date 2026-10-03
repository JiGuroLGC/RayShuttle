using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>
    /// 「流量接管」的抽象。
    ///
    /// 两个实现：
    /// - <see cref="SystemProxyInterceptor"/>：改写 Windows 系统代理，覆盖浏览器与大部分桌面应用；
    /// - <see cref="Tun.TunInterceptor"/>：TUN 虚拟网卡 + 路由表，接管全部流量（含游戏、UWP）。
    ///
    /// 上层的连接编排（<see cref="VpnConnectionService"/>）不感知具体实现：
    /// 按 <c>AppSettings.TunMode</c> 选择实例，Apply / Restore / 健康巡检全部多态分发。
    ///
    /// 接口是异步的：系统代理是同步的，但 TUN 需要等待适配器出现与路由生效。
    /// </summary>
    public interface ITrafficInterceptor
    {
        /// <summary>展示用的名称，例如「系统代理」「全局隧道 (TUN)」。</summary>
        string Name { get; }

        /// <summary>
        /// 流量是否真的已经接管成功。
        ///
        /// 调用方**必须**在 ApplyAsync 之后检查它：接管失败却仍报「已连接」是最糟糕的失败模式——
        /// 用户看到灯亮了，但流量根本没走代理，而界面上没有任何线索。
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// 把本机流量指向 Xray 的本地入口。
        /// 系统代理实现写注册表；TUN 实现建网卡、写路由。
        /// 失败时：系统代理返回 false，TUN 抛 <see cref="Tun.TunInterceptionException"/>
        /// （携带现成的 <see cref="ConnectionFailure"/>，供调用方直接进失败体系）。
        /// 实现应当先保存原有设置，以便 <see cref="RestoreAsync"/> 还原。
        /// </summary>
        Task<bool> ApplyAsync(InterceptionContext context, CancellationToken cancellationToken);

        /// <summary>还原到接管之前的设置（撤代理 / 删路由、停隧道）。</summary>
        Task RestoreAsync();

        /// <summary>
        /// 健康巡检：接管通道此刻是否还活着。
        /// 系统代理检查注册表是否被改回，TUN 检查隧道进程是否存活。
        /// </summary>
        Task<InterceptionHealth> CheckHealthAsync();
    }
}
