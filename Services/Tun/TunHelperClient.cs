using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Models;

namespace RayShuttle.Services.Tun
{
    /// <summary>与助手的会话出现问题时抛出，携带可直接进连接失败体系的 <see cref="Failure"/>。</summary>
    public sealed class TunInterceptionException : Exception
    {
        public ConnectionFailure Failure { get; }

        public TunInterceptionException(ConnectionFailure failure)
            : base(failure.Summary) => Failure = failure;

        public TunInterceptionException(ConnectionFailure failure, Exception inner)
            : base(failure.Summary, inner) => Failure = failure;
    }

    /// <summary>
    /// 提权助手（RayShuttle.TunHelper.exe）的客户端：拉起（UAC）、命名管道会话、请求-响应。
    ///
    /// 生命周期刻意做成**跨连接复用**：助手进程活着就一直用，换号重连、断开再连都不再弹 UAC。
    /// 主程序退出后助手靠父进程监视自行清场，不需要这里显式关闭（关闭路径要的是快，不是优雅）。
    /// </summary>
    internal sealed class TunHelperClient
    {
        public const string PipeName = "RayShuttle.TunHelper";

        /// <summary>连不上管道时的重连窗口。覆盖 UAC 确认 + 助手启动的常规耗时。</summary>
        private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(15);

        private static readonly TimeSpan ReuseProbeTimeout = TimeSpan.FromMilliseconds(800);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public static TunHelperClient Current { get; } = new();

        public async Task<TunHelperResponse> SendAsync(
            TunHelperRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

                var requestJson = System.Text.Json.JsonSerializer.Serialize(
                    request, TunHelperJsonContext.Default.TunHelperRequest);
                await _writer!.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                var line = await _reader!.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)
                    ?? throw new TunInterceptionException(Failure(
                        FailureKind.TunAdapter, "TUN 助手管道已断开。", null));

                return System.Text.Json.JsonSerializer.Deserialize(line, TunHelperJsonContext.Default.TunHelperResponse)
                    ?? throw new TunInterceptionException(Failure(
                        FailureKind.TunAdapter, "TUN 助手返回了无法解析的响应。", line));
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 「尽力而为」的发送：**管道不可用就直接放弃，绝不重新拉起助手**。
        ///
        /// 专供断开 / 退出路径使用——管道不在意味着助手已经死了，适配器随之消失、
        /// 路由自动失效，本来就没有需要清理的东西；这时去拉新助手（还会弹 UAC）
        /// 只会把断开或退出流程卡住十几秒。超时/出错会**主动断开管道**（挂起的读
        /// 已不可信），成功与否不影响调用方的后续流程。
        /// </summary>
        public async Task<TunHelperResponse?> TrySendAsync(TunHelperRequest request, TimeSpan timeout)
        {
            if (_pipe is not { IsConnected: true })
            {
                return null;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 双重检查：等锁期间管道可能已断。
                if (_pipe is not { IsConnected: true })
                {
                    return null;
                }

                var requestJson = System.Text.Json.JsonSerializer.Serialize(
                    request, TunHelperJsonContext.Default.TunHelperRequest);
                await _writer!.WriteLineAsync(requestJson.AsMemory()).ConfigureAwait(false);

                using var timeoutCts = new CancellationTokenSource(timeout);
                var line = await _reader!.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);

                if (line is null)
                {
                    // 管道 EOF：助手已退出（其自身清理已完成），连接作废。
                    ClosePipe();
                    return null;
                }

                return System.Text.Json.JsonSerializer.Deserialize(line, TunHelperJsonContext.Default.TunHelperResponse);
            }
            catch (Exception)
            {
                // 超时或管道错误：挂起的读已不可信，丢弃这条连接。
                // 助手侧的清理由父进程监视与退出守护 Job 兜底，这里不等它。
                ClosePipe();
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 确保管道可用：优先复用还在运行的旧助手（不弹 UAC），连不上才拉起新的。
        /// 注意只在持锁期间调用。
        /// </summary>
        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_pipe is { IsConnected: true })
            {
                return;
            }

            ClosePipe();

            // 先试旧助手：上次会话的残留进程可能还活着（父进程监视有 1 秒轮询窗口）。
            // 连上就能直接复用已授予的管理员权限。
            if (TryConnect(ReuseProbeTimeout))
            {
                return;
            }

            if (!TunPaths.HelperAvailable)
            {
                throw new TunInterceptionException(Failure(
                    FailureKind.TunMissing,
                    "未找到 TUN 提权助手。",
                    $"期望路径：{TunPaths.HelperPath}{Environment.NewLine}请重新发布或安装本软件。"));
            }

            var psi = new System.Diagnostics.ProcessStartInfo(TunPaths.HelperPath)
            {
                // --parent：助手轮询父进程存活，主进程死亡时自动清场退出（第二道保险）；
                // --job：让提权的助手加入主进程的退出守护 Job（KILL_ON_JOB_CLOSE）——
                // 这是第一道保险，主进程无论怎么死，整棵进程树都会被系统一起终结。
                Arguments = $"--parent {Environment.ProcessId} --job \"{ProcessTreeGuard.JobName}\"",
                UseShellExecute = true,
                Verb = "runas", // 触发 UAC。
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            try
            {
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                // ERROR_CANCELED：用户在 UAC 上点了「否」。可重试——下次批准即可。
                throw new TunInterceptionException(Failure(
                    FailureKind.TunElevation,
                    "需要管理员权限。",
                    "已取消管理员权限确认，无法建立 TUN 隧道。"),
                    exception);
            }

            if (!await TryConnectLoopAsync(PipeConnectTimeout, cancellationToken).ConfigureAwait(false))
            {
                throw new TunInterceptionException(Failure(
                    FailureKind.TunAdapter,
                    "TUN 助手未在预期时间内就绪。",
                    "助手已提权启动，但管道始终没有出现。可在诊断日志中查看助手侧记录。"));
            }
        }

        private bool TryConnect(TimeSpan timeout)
        {
            try
            {
                // **必须用 Asynchronous 句柄**：同步句柄上 ReadLineAsync 的超时取消不生效，
                // 助手清理变慢（删路由、杀 tun2socks）时退出路径会在 UI 线程上无限挂起——
                // 就是那个「TUN 正在代理时托盘退出卡死」的根因。
                var pipe = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    pipe.Connect((int)timeout.TotalMilliseconds);
                }
                catch (TimeoutException)
                {
                    pipe.Dispose();
                    return false;
                }

                if (!pipe.IsConnected)
                {
                    pipe.Dispose();
                    return false;
                }

                _pipe = pipe;
                _reader = new StreamReader(pipe);
                _writer = new StreamWriter(pipe) { AutoFlush = true };
                return true;
            }
            catch (Exception)
            {
                // 助手还没建好管道等一切瞬时状况：统一按「连不上」处理，交给上层拉新助手。
                return false;
            }
        }

        private async Task<bool> TryConnectLoopAsync(TimeSpan total, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + total;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryConnect(TimeSpan.FromMilliseconds(300)))
                {
                    return true;
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        private void ClosePipe()
        {
            try
            {
                _reader?.Dispose();
                _writer?.Dispose();
                _pipe?.Dispose();
            }
            catch (Exception)
            {
                // 管道清理失败无后续影响，下轮会重建。
            }

            _reader = null;
            _writer = null;
            _pipe = null;
        }

        private static ConnectionFailure Failure(FailureKind kind, string summary, string? detail) =>
            new(kind, KindText(kind), summary, SuggestionFor(kind), detail ?? string.Empty);

        private static string KindText(FailureKind kind) => kind switch
        {
            FailureKind.TunMissing => "缺少 TUN 组件",
            FailureKind.TunElevation => "需要管理员权限",
            _ => "TUN 隧道建立失败"
        };

        private static string SuggestionFor(FailureKind kind) => kind switch
        {
            FailureKind.TunElevation => "在管理员权限确认框上选择「是」后再试；" +
                "也可以在设置中关闭 TUN 全局模式，改用系统代理。",
            _ => "若反复出现，请把诊断信息复制给开发者。"
        };
    }
}
