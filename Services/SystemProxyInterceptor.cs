using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>
    /// 通过改写 Windows 系统代理来接管流量。
    ///
    /// **最重要的一点是「不能把用户搞断网」**：一旦应用崩溃而系统代理还指着已经关闭的
    /// 本地端口，用户所有走系统代理的程序都会立刻连不上，而原因看起来毫无头绪。
    ///
    /// 为此接管前会把原有设置落盘备份，恢复时读回；应用启动时也会检查是否有残留备份
    /// （说明上次是异常退出）并主动还原。
    /// </summary>
    public sealed class SystemProxyInterceptor : ITrafficInterceptor
    {
        private static readonly string BackupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle",
            "proxy-backup.txt");

        private bool _applied;
        private int _httpPort;

        public string Name => "系统代理";

        /// <inheritdoc />
        public bool IsActive => _applied;

        public Task<bool> ApplyAsync(InterceptionContext context, CancellationToken cancellationToken)
        {
            if (!SystemProxyManager.IsSupported)
            {
                return Task.FromResult(false);
            }

            // 记下端口供健康巡检比对（见 CheckHealthAsync）。
            _httpPort = context.HttpPort;

            // 先备份再改。备份只在「当前没有备份」时写，避免把已接管的设置当成原始设置存下来。
            if (!File.Exists(BackupPath))
            {
                var current = SystemProxyManager.TryRead();
                if (current is not null)
                {
                    WriteBackup(current);
                }
            }

            _applied = SystemProxyManager.TryWrite(SystemProxyManager.CreateEnabledSnapshot(context.Host, context.HttpPort));
            SystemProxyManager.Trace($"ApplyAsync({context.Host}:{context.HttpPort}) -> TryWrite 返回 {_applied}");
            return Task.FromResult(_applied);
        }

        public Task RestoreAsync()
        {
            if (!_applied)
            {
                DeleteBackup();
                return Task.CompletedTask;
            }

            RestoreFromBackup();
            _applied = false;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<InterceptionHealth> CheckHealthAsync()
        {
            if (!_applied)
            {
                return Task.FromResult(new InterceptionHealth(true, null));
            }

            // 读不到当前设置时按「正常」处理：宁可漏报一次，也不要因为一次读取抖动就误判掉线、
            // 反复重连（与 SlotGuard 原巡检语义一致）。
            var current = SystemProxyManager.TryRead();
            if (current is null)
            {
                return Task.FromResult(new InterceptionHealth(true, null));
            }

            var alive = current.Enabled
                && current.Server.Contains($"127.0.0.1:{_httpPort}", StringComparison.Ordinal);

            return Task.FromResult(alive
                ? new InterceptionHealth(true, null)
                : new InterceptionHealth(false, "系统代理设置被其它程序改回去了，流量已不再经过光梭。"));
        }

        /// <summary>
        /// 供 TUN 模式使用的互斥动作：把当前系统代理（若有）备份后禁用，
        /// 避免流量同时走 TUN 与旧系统代理形成双重代理。恢复走 <see cref="RestoreAsync"/>。
        /// </summary>
        public Task DisableWithBackupAsync()
        {
            if (!SystemProxyManager.IsSupported)
            {
                return Task.CompletedTask;
            }

            var current = SystemProxyManager.TryRead();
            if (current is not { Enabled: true })
            {
                return Task.CompletedTask;
            }

            if (!File.Exists(BackupPath))
            {
                WriteBackup(current);
            }

            SystemProxyManager.Trace("DisableWithBackupAsync -> 禁用系统代理（已备份）");
            SystemProxyManager.TryWrite(new SystemProxySnapshot(false, string.Empty, string.Empty));
            _applied = true; // 让 RestoreAsync 按正常路径恢复。
            return Task.CompletedTask;
        }

        /// <summary>
        /// 应用启动时调用。存在残留备份说明上次没有正常还原，此时必须主动恢复，
        /// 否则用户的网络会一直处于「代理指向死端口」的状态。
        /// </summary>
        public static void RestoreStaleOnStartup()
        {
            if (!File.Exists(BackupPath))
            {
                return;
            }

            RestoreFromBackup();
        }

        private static void RestoreFromBackup()
        {
            var restored = TryReadBackup(out var snapshot) && SystemProxyManager.TryWrite(snapshot);

            if (!restored)
            {
                // 备份损坏时不能让用户卡在断网状态。只在确认当前代理指向本机时才清掉它，
                // 避免误删用户自己配置的代理。
                DisableIfPointingToLocalHost();
            }

            DeleteBackup();
        }

        private static void DisableIfPointingToLocalHost()
        {
            var current = SystemProxyManager.TryRead();
            if (current is null || !current.Enabled)
            {
                return;
            }

            if (current.Server.StartsWith("127.0.0.1:", StringComparison.Ordinal))
            {
                SystemProxyManager.TryWrite(new SystemProxySnapshot(false, string.Empty, string.Empty));
            }
        }

        private static void WriteBackup(SystemProxySnapshot snapshot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);

                // 三个短值，用逐行文本比 JSON 更省事，也没有序列化与裁剪的顾虑。
                File.WriteAllLines(BackupPath, new[]
                {
                    snapshot.Enabled ? "1" : "0",
                    snapshot.Server,
                    snapshot.Override
                });
            }
            catch (Exception)
            {
                // 备份失败不阻断接管：能连上比能还原更重要，还原失败的兜底见 RestoreFromBackup。
            }
        }

        private static bool TryReadBackup(out SystemProxySnapshot snapshot)
        {
            snapshot = new SystemProxySnapshot(false, string.Empty, string.Empty);

            try
            {
                if (!File.Exists(BackupPath))
                {
                    return false;
                }

                var lines = File.ReadAllLines(BackupPath);
                if (lines.Length < 3)
                {
                    return false;
                }

                snapshot = new SystemProxySnapshot(
                    lines[0] == "1",
                    lines[1],
                    lines[2]);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void DeleteBackup()
        {
            try
            {
                if (File.Exists(BackupPath))
                {
                    File.Delete(BackupPath);
                }
            }
            catch (Exception)
            {
                // 删不掉最多导致下次启动多还原一次，无害。
            }
        }
    }
}
