using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace RayShuttle.Services
{
    /// <summary>
    /// 开机自启：往 HKCU 的 Run 键写一条启动命令。
    ///
    /// 写入范式照抄 <see cref="SystemProxyManager"/> —— **写完必须回读校验**。
    /// 被组策略或安全软件静默拦掉时 SetValue 并不报错、值却没落地，
    /// 那时界面若显示「已开启」就是在骗用户。
    ///
    /// 用 HKCU 而不是 HKLM：后者需要管理员权限，而本应用整体是免提权运行的。
    ///
    /// 注意：这里写的是**当前 exe 的完整路径**。它只在「免打包运行 / 固定安装目录」下成立；
    /// 若哪天改为 MSIX 打包分发，应改成写 `shell:AppsFolder\{AUMID}` 那种形式，
    /// 且应用被移动或升级换目录后，旧值会失效（<see cref="IsEnabled"/> 会如实报 false）。
    /// </summary>
    internal static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "RayShuttle";

        /// <summary>自启时附加的参数：静默进托盘，不弹主窗口。</summary>
        public const string TrayArgument = "--tray";

        /// <summary>
        /// 注册表里此刻**实际**是否指向本程序。设置页的开关初值取它，而不是 settings.json——
        /// 用户可能在任务管理器里手动禁用了启动项，两边会不一致。
        /// </summary>
        public static bool IsEnabled =>
            string.Equals(ReadCommand(), BuildCommand(), StringComparison.OrdinalIgnoreCase);

        /// <summary>写入 / 删除并回读校验。任何失败都返回 false，绝不抛异常。</summary>
        public static bool Apply(bool enabled)
        {
            // 拿不到自身路径就无从写起（理论上不会发生）。
            if (ExecutablePath.Length == 0)
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null)
                {
                    return false;
                }

                if (enabled)
                {
                    key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception exception)
            {
                NodeDiagnostics.LogException(enabled ? "写开机自启注册表" : "删开机自启注册表", exception);
                return false;
            }

            // 回读校验：SetValue/DeleteValue 不报错不等于真的生效。
            return IsEnabled == enabled;
        }

        private static string? ReadCommand()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 启动命令行。exe 路径**必须带引号**——安装路径常含空格，不加引号会被拆成两段，
        /// 结果是开机时弹一个「找不到文件」的错误框。
        /// </summary>
        private static string BuildCommand() => $"\"{ExecutablePath}\" {TrayArgument}";

        private static string ExecutablePath =>
            Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? string.Empty;
    }
}
