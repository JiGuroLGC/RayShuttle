using System;
using System.IO;

namespace RayShuttle.Services
{
    /// <summary>
    /// 定位 Xray 内核与运行目录。
    ///
    /// 内核随应用一起打包，放在安装目录的 Core\ 下——安装目录是只读的，
    /// 所以配置文件与运行数据一律写到 %LOCALAPPDATA%\RayShuttle\core。
    ///
    /// 放置内容（从 Xray 官方 release 解压）：
    ///     Core\xray.exe
    ///     Core\geoip.dat      （可选，当前配置未使用 geo 规则，留作后续扩展）
    ///     Core\geosite.dat    （可选）
    /// </summary>
    public static class XrayCoreLocator
    {
        /// <summary>内核与数据文件所在目录（安装目录下，只读）。</summary>
        public static string CoreDirectory { get; } =
            Path.Combine(AppContext.BaseDirectory, "Core");

        public static string ExecutablePath { get; } =
            Path.Combine(CoreDirectory, "xray.exe");

        /// <summary>Xray 通过这个环境变量找 geoip.dat / geosite.dat。</summary>
        public static string AssetDirectory => CoreDirectory;

        /// <summary>可写的工作目录：放生成的配置与运行日志。</summary>
        public static string WorkingDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle",
            "core");

        public static string GeneratedConfigPath { get; } =
            Path.Combine(WorkingDirectory, "config.json");

        public static bool IsAvailable => File.Exists(ExecutablePath);

        /// <summary>内核缺失时给用户看的说明，包含期望路径与获取方式。</summary>
        public static string DescribeMissing() =>
            $"未找到 Xray 内核。\n\n请从 Xray 官方 release 下载后解压到：\n{CoreDirectory}\n\n" +
            "需要至少包含 xray.exe。";
    }
}
