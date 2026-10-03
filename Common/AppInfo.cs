using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RayShuttle.Common
{
    /// <summary>
    /// 应用自身的版本与运行环境信息。
    ///
    /// **版本号必须以这里为唯一来源。** 从前设置页把「0.1.0」硬编码在 XAML 里、csproj 又没有
    /// &lt;Version&gt;、程序集版本是默认的 1.0.0.0 —— 三处对不上，诊断报告里写出去的版本就不可信了。
    /// </summary>
    internal static class AppInfo
    {
        /// <summary>语义化版本，来自 csproj 的 &lt;Version&gt;。</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>操作系统描述，例如「Microsoft Windows 11 Pro 10.0.26100」。</summary>
        public static string OsDescription { get; } = RuntimeInformation.OSDescription.Trim();

        /// <summary>运行时描述，例如「X64 / .NET 8.0.0」。</summary>
        public static string Runtime { get; } =
            $"{RuntimeInformation.ProcessArchitecture} / {RuntimeInformation.FrameworkDescription.Trim()}";

        private static string ReadVersion()
        {
            var assembly = typeof(AppInfo).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            // 带 SourceLink 的构建会把提交号用 '+' 拼在版本后面，这里只取版本本身。
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
