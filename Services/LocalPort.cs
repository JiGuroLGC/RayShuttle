using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>本机端口工具：挑一个空闲端口，以及等待某个端口开始监听。</summary>
    public static class LocalPort
    {
        /// <summary>
        /// 让系统分配一个空闲端口。
        ///
        /// 注意这是「先探测再使用」，探测与真正占用之间存在极小的竞态窗口。
        /// 对本地代理入口来说可以接受——真被抢占时 Xray 会启动失败并写进日志，
        /// 上层会把它当成连接失败报出来，不会静默出问题。
        /// </summary>
        public static int FindFree()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        public static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 轮询直到端口可连接。比「固定等 N 秒」可靠得多：
        /// 内核就绪得快就立刻返回，慢也不会误判。
        /// </summary>
        public static async Task<bool> WaitUntilListeningAsync(
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await CanConnectAsync(port, cancellationToken))
                {
                    return true;
                }

                await Task.Delay(120, cancellationToken);
            }

            return false;
        }
    }
}
