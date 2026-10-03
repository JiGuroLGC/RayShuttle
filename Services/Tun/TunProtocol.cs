using System;
using System.IO;
using System.Text.Json.Serialization;

namespace RayShuttle.Services.Tun
{
    // ---------------------------------------------------------------- 协议
    //
    // 主程序 ↔ RayShuttle.TunHelper 的 JSON 行协议。
    // **这里的 record 必须与助手侧 Protocol.cs 保持一致**，改任一侧要同步另一侧。

    /// <summary>发给助手的请求。</summary>
    public sealed record TunHelperRequest(string Cmd, TunSetupArgs? Args, int ClientPid);

    /// <summary>setup 命令参数。</summary>
    public sealed record TunSetupArgs(
        string Device,
        string Tun2SocksPath,
        string Proxy,
        string AdapterIp,
        string AdapterMask,
        string ServerAddress);

    /// <summary>助手的响应。</summary>
    public sealed record TunHelperResponse(bool Ok, string? Error, string? Detail);

    /// <summary>主程序侧源生成器（Release 开 PublishTrimmed，反射式序列化会被裁掉）。</summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(TunHelperRequest))]
    [JsonSerializable(typeof(TunHelperResponse))]
    internal sealed partial class TunHelperJsonContext : JsonSerializerContext
    {
    }

    // ---------------------------------------------------------------- 路径

    /// <summary>TUN 相关组件的定位。</summary>
    internal static class TunPaths
    {
        /// <summary>tun2socks（第三方，占位见 Core/TUN-README.txt）：把 TUN 包转成 SOCKS 流。</summary>
        public static string Tun2SocksPath { get; } =
            Path.Combine(XrayCoreLocator.CoreDirectory, "tun2socks.exe");

        /// <summary>提权助手（随应用发布，位于输出目录 TunHelper\ 下）。</summary>
        public static string HelperPath { get; } =
            Path.Combine(AppContext.BaseDirectory, "TunHelper", "RayShuttle.TunHelper.exe");

        public static bool Tun2SocksAvailable => File.Exists(Tun2SocksPath);

        public static bool HelperAvailable => File.Exists(HelperPath);
    }
}
