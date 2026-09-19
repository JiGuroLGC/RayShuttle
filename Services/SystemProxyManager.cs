using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RayShuttle.Services
{
    /// <summary>系统代理的一份快照。</summary>
    public sealed record SystemProxySnapshot(bool Enabled, string Server, string Override);

    /// <summary>
    /// Windows 系统代理（WinINET）的读写。
    ///
    /// 存储位置是注册表 HKCU\...\Internet Settings，改完之后必须调用
    /// InternetSetOption 通知已在运行的程序，否则要等它们重启才生效。
    ///
    /// **本类只做读写，不负责备份与恢复**——那部分在 <see cref="SystemProxyInterceptor"/>，
    /// 因为「崩溃后还能恢复」需要落盘，属于更高一层的职责。
    /// </summary>
    public static class SystemProxyManager
    {
        private const string InternetSettingsKey =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private const int InternetOptionSettingsChanged = 39;
        private const int InternetOptionRefresh = 37;

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InternetSetOption(
            IntPtr hInternet,
            int dwOption,
            IntPtr lpBuffer,
            int dwBufferLength);

        /// <summary>绕过代理的地址：本机与常见私网段。</summary>
        public static string DefaultBypassList { get; } = BuildDefaultBypassList();

        public static bool IsSupported => OperatingSystem.IsWindows();

        // **诊断用追踪**：把每次代理写入的入参、回读结果与异常落到文件，
        // 用来确认「应用到底有没有真的去写注册表、写了什么、写没写进去」。
        // 这是排查「灯亮了但 Windows 设置里代理仍是关」的唯一可靠手段，
        // 因为这类问题只能靠运行期证据判断，不能靠读代码推断。
        private static readonly string TracePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle",
            "proxy-trace.log");

        internal static void Trace(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TracePath)!);
                File.AppendAllText(TracePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
                // 追踪失败绝不影响接管主流程。
            }
        }

        public static SystemProxySnapshot? TryRead()
        {
            if (!IsSupported)
            {
                return null;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
                if (key is null)
                {
                    return new SystemProxySnapshot(false, string.Empty, string.Empty);
                }

                var enabled = key.GetValue("ProxyEnable") is int value && value != 0;
                var server = key.GetValue("ProxyServer") as string ?? string.Empty;
                var bypass = key.GetValue("ProxyOverride") as string ?? string.Empty;

                return new SystemProxySnapshot(enabled, server, bypass);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>写入并通知系统。返回是否成功。</summary>
        public static bool TryWrite(SystemProxySnapshot snapshot)
        {
            if (!IsSupported)
            {
                return false;
            }

            try
            {
                Trace($"TryWrite 入参: Enabled={snapshot.Enabled} Server='{snapshot.Server}' Override='{snapshot.Override}'");

                using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
                if (key is null)
                {
                    return false;
                }

                key.SetValue("ProxyEnable", snapshot.Enabled ? 1 : 0, RegistryValueKind.DWord);

                if (string.IsNullOrEmpty(snapshot.Server))
                {
                    key.DeleteValue("ProxyServer", throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue("ProxyServer", snapshot.Server, RegistryValueKind.String);
                }

                if (string.IsNullOrEmpty(snapshot.Override))
                {
                    key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue("ProxyOverride", snapshot.Override, RegistryValueKind.String);
                }

                NotifySystem();

                // **写完之后必须回读校验。** 只信任 SetValue 的返回值是不够的：写入被静默丢弃时，
                // 界面会显示「已连接」，但流量根本没走代理——这正是最难排查的失败模式。
                var ok = Matches(TryRead(), snapshot);
                Trace($"TryWrite 结果: {(ok ? "成功(回读一致)" : "失败(回读不一致)")}");
                return ok;
            }
            catch (Exception exception)
            {
                Trace($"TryWrite 异常: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        /// <summary>回读的结果是否与期望一致。空快照（读取失败）视为不一致。</summary>
        private static bool Matches(SystemProxySnapshot? actual, SystemProxySnapshot expected)
        {
            if (actual is null)
            {
                return false;
            }

            return actual.Enabled == expected.Enabled
                && string.Equals(actual.Server, expected.Server, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 描述当前系统代理状态，用于失败时展示给用户与排查。
        /// </summary>
        public static string DescribeCurrent()
        {
            var current = TryRead();
            return current is null
                ? "读取系统代理设置失败"
                : $"ProxyEnable={(current.Enabled ? 1 : 0)}，ProxyServer={current.Server}";
        }

        public static SystemProxySnapshot CreateEnabledSnapshot(string host, int port) =>
            new(true, $"{host}:{port}", DefaultBypassList);

        /// <summary>通知已在运行的程序重新读取代理设置。</summary>
        private static void NotifySystem()
        {
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }

        private static string BuildDefaultBypassList()
        {
            var entries = new List<string>
            {
                "<local>",
                "localhost",
                "127.*",
                "10.*",
                "192.168.*"
            };

            // 172.16.0.0/12 跨 172.16.* ~ 172.31.*，代理绕过列表只支持通配符，只能逐个列出。
            for (var second = 16; second <= 31; second++)
            {
                entries.Add($"172.{second}.*");
            }

            return string.Join(';', entries);
        }
    }
}
