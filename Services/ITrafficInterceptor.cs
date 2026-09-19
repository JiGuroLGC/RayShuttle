using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>
    /// 「流量接管」的抽象。
    ///
    /// 当前只有系统代理一种实现：改写 Windows 系统代理设置，覆盖浏览器与大部分桌面应用。
    /// 将来要加 TUN 全局模式时，**只需新增一个实现**（加载 wintun、改路由表、劫持 DNS），
    /// 上层的连接编排（<see cref="VpnConnectionService"/>）完全不用动。
    ///
    /// 接口刻意是异步的：系统代理是同步的，但 TUN 需要异步等待驱动加载与路由生效，
    /// 提前把签名定成异步，可以避免将来改接口牵动所有调用方。
    /// </summary>
    public interface ITrafficInterceptor
    {
        /// <summary>展示用的名称，例如「系统代理」。</summary>
        string Name { get; }

        /// <summary>
        /// 流量是否真的已经接管成功。
        ///
        /// 调用方**必须**在 ApplyAsync 之后检查它：接管失败却仍报「已连接」是最糟糕的失败模式——
        /// 用户看到灯亮了，但流量根本没走代理，而界面上没有任何线索。
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// 把本机流量指向 host:port（即 Xray 的本地入口）。
        /// 实现应当先保存原有设置，以便 <see cref="RestoreAsync"/> 还原。
        /// </summary>
        Task ApplyAsync(string host, int port, CancellationToken cancellationToken);

        /// <summary>还原到接管之前的设置。</summary>
        Task RestoreAsync();
    }
}
