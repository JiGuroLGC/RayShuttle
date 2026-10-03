using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>
    /// 节点延迟探测：并发测量一批节点的链路延迟，写回每个节点的 <see cref="ProxyNode.LatencyMs"/>。
    /// 探测完成后，把延迟最低（且成功测得）的节点标记为推荐。
    ///
    /// 具体测量委托给 <see cref="LatencyProbe"/>（解析域名并缓存、只对 IP 计时、取多次最小值），
    /// 与连接期间的实时延迟（<see cref="ConnectionStatsMonitor"/>）共用同一套口径。这里是批量、
    /// 并发的版本，受并发上限约束，避免一次性对几十个节点同时建连。
    ///
    /// 测延迟发生在后台线程，**写回属性必须回到 UI 线程**：否则会触发 x:Bind 的跨线程更新异常。
    /// 因此这里把测量与写回分开——后台只出结果，最后统一回到 UI 线程一次性写回，
    /// 既避免跨线程问题，也保证「智能优选」在 await 结束后读到的就是最终值。
    /// </summary>
    internal static class NodeLatencyProber
    {
        /// <summary>同时进行的探测数上限。节点通常不多（一个通道一个），略放宽以备大列表。</summary>
        private const int MaxConcurrency = 8;

        /// <summary>
        /// 探测给定节点列表的延迟。每个节点的延迟测得后写回（触发属性通知，胶囊就地刷新），
        /// 并把综合延迟最低（且成功测得）的节点标记为推荐。
        /// </summary>
        /// <param name="nodes">待探测节点；其中任一为 null 会被跳过。</param>
        /// <param name="onNodeUpdated">全部写回后回调一次（允许为 null），参数为被推荐的节点或首个节点。</param>
        /// <param name="cancellationToken">取消整批探测。</param>
        public static async Task ProbeAsync(
            IReadOnlyCollection<ProxyNode> nodes,
            Action<ProxyNode>? onNodeUpdated = null,
            CancellationToken cancellationToken = default)
        {
            if (nodes is null || nodes.Count == 0)
            {
                return;
            }

            // 捕获 UI 线程调度器：本方法由 UI 线程调用，entry 处的当前线程即 UI 线程。
            // 最后的属性写回要回到它，否则 x:Bind 在后台线程更新 UI 会抛跨线程异常。
            var dispatcher = DispatcherQueue.GetForCurrentThread();

            // 后台只测不写：结果暂存到普通字典（非 UI 绑定属性），安全且不涉及线程亲缘。
            var results = new Dictionary<ProxyNode, int>(nodes.Count);
            using var semaphore = new SemaphoreSlim(MaxConcurrency);

            var tasks = new List<Task>(nodes.Count);
            foreach (var node in nodes)
            {
                if (node is null)
                {
                    continue;
                }

                results[node] = 0;
                tasks.Add(MeasureOneAsync(node, semaphore, results, cancellationToken));
            }

            if (tasks.Count == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 整批被取消：已测到的延迟保留，未测到的保持 0。
            }

            // 回到 UI 线程统一写回：清推荐、写延迟、挑最优、标推荐。
            await RunOnDispatcherAsync(dispatcher, () =>
            {
                ProxyNode? best = null;

                foreach (var node in nodes)
                {
                    if (node is null)
                    {
                        continue;
                    }

                    node.IsRecommended = false;

                    if (results.TryGetValue(node, out var measured))
                    {
                        node.LatencyMs = measured;
                    }

                    if (node.LatencyMs > 0 && (best is null || node.LatencyMs < best.LatencyMs))
                    {
                        best = node;
                    }
                }

                if (best is not null)
                {
                    best.IsRecommended = true;
                }

                onNodeUpdated?.Invoke(best ?? nodes.FirstOrDefault()!);
            });
        }

        private static async Task MeasureOneAsync(
            ProxyNode node,
            SemaphoreSlim semaphore,
            Dictionary<ProxyNode, int> results,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                results[node] = await LatencyProbe.MeasureAsync(node.Address, node.Port, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>把动作派发回 UI 线程并等待其完成；无调度器时同步执行。</summary>
        private static Task RunOnDispatcherAsync(DispatcherQueue? dispatcher, Action action)
        {
            if (dispatcher is null)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource();
            if (!dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                        tcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }))
            {
                // 派发失败（窗口已销毁等）：同步兜底执行，保证不卡住调用方。
                try
                {
                    action();
                }
                catch
                {
                }

                tcs.TrySetResult();
            }

            return tcs.Task;
        }
    }
}
