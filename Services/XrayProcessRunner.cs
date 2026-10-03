using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>
    /// Xray 子进程的生命周期。
    ///
    /// 保留最近若干行输出用于诊断——内核启动失败时，日志里的那一行才是真正的原因，
    /// 没有它用户只能看到「连接失败」。
    /// </summary>
    public sealed class XrayProcessRunner : IDisposable
    {
        private const int MaxRetainedLogLines = 200;

        private readonly object _logLock = new();
        private readonly Queue<string> _recentOutput = new();

        private Process? _process;
        private bool _disposed;

        public bool IsRunning => _process is { HasExited: false };

        /// <summary>
        /// 内核进程**自己**退出时触发（崩溃、被任务管理器结束等）。
        ///
        /// 我们主动 <see cref="Stop"/> 时不会触发：那会先摘掉事件再 Kill。
        /// 上层据此把「意外掉线」与「用户主动断开」区分开——前者要自动重连，后者不该。
        /// </summary>
        public event EventHandler? ProcessExited;

        /// <summary>最近的内核输出，用于失败时展示。</summary>
        public string LastOutput
        {
            get
            {
                lock (_logLock)
                {
                    return string.Join(Environment.NewLine, _recentOutput);
                }
            }
        }

        public void Start(string executablePath, string configPath, string workingDirectory, string assetDirectory)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("内核已在运行。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                // 路径可能含空格，必须加引号。
                Arguments = $"-c \"{configPath}\"",
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Xray 通过这个变量定位 geoip.dat / geosite.dat。
            startInfo.Environment["XRAY_LOCATION_ASSET"] = assetDirectory;

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutputReceived;
            process.ErrorDataReceived += OnOutputReceived;
            process.Exited += OnProcessExited;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
        }

        /// <summary>
        /// 等待内核就绪。判定依据是**本地入口端口可连接**，而不是固定等待若干秒——
        /// 内核起得快就立刻返回，起得慢也不会误判。
        /// 进程若提前退出（配置错误等），立即返回 false，由调用方展示日志。
        /// </summary>
        public async Task<bool> WaitUntilReadyAsync(
            int probePort,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_process is null || _process.HasExited)
                {
                    return false;
                }

                if (await LocalPort.CanConnectAsync(probePort, cancellationToken))
                {
                    return true;
                }

                await Task.Delay(120, cancellationToken);
            }

            return false;
        }

        public void Stop()
        {
            var process = _process;
            _process = null;

            if (process is null)
            {
                return;
            }

            // **先摘事件再杀**：主动停止不该被上层当成「内核崩了」而去自动重连。
            process.Exited -= OnProcessExited;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // 进程可能已自行退出，或权限不足；都不该让停止流程抛异常。
            }
            finally
            {
                process.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            // 只认当前跟踪的那个进程：旧进程被换掉之后迟到的回调要丢掉。
            if (!ReferenceEquals(sender, _process))
            {
                return;
            }

            ProcessExited?.Invoke(this, EventArgs.Empty);
        }

        private void OnOutputReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            lock (_logLock)
            {
                _recentOutput.Enqueue(e.Data);

                while (_recentOutput.Count > MaxRetainedLogLines)
                {
                    _recentOutput.Dequeue();
                }
            }
        }
    }
}
