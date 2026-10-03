using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    internal sealed class AppSettingsState
    {
        // 默认值定义在 AppSettings 上（同文件的另一个类），这里必须带类名限定。
        public string ApiBaseUrl { get; set; } = AppSettings.DefaultApiBaseUrl;

        /// <summary>
        /// 备用云端 API 地址：主地址超时或不可达时自动回退。
        /// 留空表示不启用备用地址；默认值定义在 AppSettings 上。
        /// </summary>
        public string BackupApiBaseUrl { get; set; } = AppSettings.DefaultBackupApiBaseUrl;

        /// <summary>关闭窗口时是否最小化到系统托盘（而非真正退出），保持后台连接。</summary>
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>开机自启（写 HKCU 的 Run 键，启动后静默进托盘）。</summary>
        public bool LaunchAtStartup { get; set; }

        /// <summary>启动后若已登录且有可用节点，自动连上。</summary>
        public bool AutoConnectOnLaunch { get; set; }

        /// <summary>连接意外掉线时自动重连。</summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>连接成功 / 掉线 / 自动重连结果是否弹托盘通知。</summary>
        public bool NotificationsEnabled { get; set; } = true;

        /// <summary>连接与断开时是否播放提示音。</summary>
        public bool PlayConnectionSound { get; set; } = true;

        /// <summary>
        /// TUN 全局模式（实验性）：接管全部流量而不是只写系统代理。
        /// 连接时会拉起提权助手（UAC），改系统代理之外还要建虚拟网卡与写路由表。
        /// </summary>
        public bool TunMode { get; set; }

        /// <summary>分流配置。</summary>
        public RoutingSettings Routing { get; set; } = new();
    }

    /// <summary>
    /// 用源生成器而不是反射式序列化：Release 构建开了 PublishTrimmed，
    /// 反射式 JsonSerializer 会把属性裁掉，存出一串空对象。
    /// 枚举按字符串存，便于人工查看，也不怕以后调整枚举顺序。
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(AppSettingsState))]
    internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// 应用级设置（与账号无关，退出登录不清除）。
    ///
    /// 存放在 %LOCALAPPDATA%\RayShuttle\settings.json。
    /// 与 AccountStore 分开是刻意的：账号状态会在登出时被清掉，设置不该跟着消失。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// 云端 API 地址。**换成你自己的域名**——`workers.dev` 子域在大陆不可靠，
        /// 自定义域名是必需项。
        /// </summary>
        public const string DefaultApiBaseUrl = "https://rayshuttle.196104.xyz";

        /// <summary>
        /// 默认的备用 API 地址。与主地址指向同一套后端，只是换一个入口域名，
        /// 因此主地址不可达时可以无缝顶上。
        /// </summary>
        public const string DefaultBackupApiBaseUrl = "https://rayshuttlebak.196104.xyz";

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle",
            "settings.json");

        public static AppSettings Current { get; } = new();

        private AppSettingsState _state = new();

        private AppSettings()
        {
        }

        /// <summary>云端 API 地址，末尾不带斜杠。</summary>
        public string ApiBaseUrl =>
            string.IsNullOrWhiteSpace(_state.ApiBaseUrl) ? DefaultApiBaseUrl : _state.ApiBaseUrl;

        /// <summary>
        /// 备用 API 地址。**空串表示不启用备用地址**（用户在设置里清空即可关闭回退）；
        /// 非空时统一去掉末尾斜杠。
        /// </summary>
        public string BackupApiBaseUrl =>
            string.IsNullOrWhiteSpace(_state.BackupApiBaseUrl)
                ? string.Empty
                : _state.BackupApiBaseUrl.Trim().TrimEnd('/');

        /// <summary>关闭窗口时最小化到系统托盘（默认开启）。</summary>
        public bool MinimizeToTray => _state.MinimizeToTray;

        /// <summary>开机自启。</summary>
        public bool LaunchAtStartup => _state.LaunchAtStartup;

        /// <summary>启动后自动连接。</summary>
        public bool AutoConnectOnLaunch => _state.AutoConnectOnLaunch;

        /// <summary>断线自动重连（默认开启）。</summary>
        public bool AutoReconnect => _state.AutoReconnect;

        /// <summary>是否弹托盘通知（默认开启）。</summary>
        public bool NotificationsEnabled => _state.NotificationsEnabled;

        /// <summary>是否播放连接提示音（默认开启）。</summary>
        public bool PlayConnectionSound => _state.PlayConnectionSound;

        /// <summary>TUN 全局模式（实验性，默认关闭）。</summary>
        public bool TunMode => _state.TunMode;

        /// <summary>
        /// 分流配置。**只读使用**——要改就取一份 <see cref="RoutingSettings.Clone"/> 改完再
        /// <see cref="SetRoutingAsync"/> 整体写回，避免半改状态被落盘。
        /// </summary>
        public RoutingSettings Routing => _state.Routing;

        /// <summary>设置变化时触发，供已打开的页面刷新。</summary>
        public event EventHandler? Changed;

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                var json = await File.ReadAllTextAsync(FilePath);
                _state = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettingsState)
                    ?? new AppSettingsState();

                // 老版本的 settings.json 里没有 routing 字段，反序列化回来会是 null。
                _state.Routing ??= new RoutingSettings();
            }
            catch
            {
                // 文件损坏或格式变更都按默认值处理，不能让设置读不出来就起不来。
                _state = new AppSettingsState();
            }
        }

        public async Task SetApiBaseUrlAsync(string value)
        {
            var normalized = value.Trim().TrimEnd('/');
            if (string.Equals(_state.ApiBaseUrl, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _state.ApiBaseUrl = normalized;
            await SaveAndNotifyAsync();
        }

        /// <summary>写入备用 API 地址；传空串即关闭备用地址（下次请求不再回退）。</summary>
        public async Task SetBackupApiBaseUrlAsync(string value)
        {
            var normalized = value.Trim().TrimEnd('/');
            if (string.Equals(_state.BackupApiBaseUrl, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _state.BackupApiBaseUrl = normalized;
            await SaveAndNotifyAsync();
        }

        public Task SetMinimizeToTrayAsync(bool value) =>
            ApplyAsync(_state.MinimizeToTray, value, () => _state.MinimizeToTray = value);

        public Task SetLaunchAtStartupAsync(bool value) =>
            ApplyAsync(_state.LaunchAtStartup, value, () => _state.LaunchAtStartup = value);

        public Task SetAutoConnectOnLaunchAsync(bool value) =>
            ApplyAsync(_state.AutoConnectOnLaunch, value, () => _state.AutoConnectOnLaunch = value);

        public Task SetAutoReconnectAsync(bool value) =>
            ApplyAsync(_state.AutoReconnect, value, () => _state.AutoReconnect = value);

        public Task SetNotificationsEnabledAsync(bool value) =>
            ApplyAsync(_state.NotificationsEnabled, value, () => _state.NotificationsEnabled = value);

        public Task SetPlayConnectionSoundAsync(bool value) =>
            ApplyAsync(_state.PlayConnectionSound, value, () => _state.PlayConnectionSound = value);

        public Task SetTunModeAsync(bool value) =>
            ApplyAsync(_state.TunMode, value, () => _state.TunMode = value);

        /// <summary>
        /// 整体替换分流配置。清单会顺手规范化（去首尾空白、转小写、去重、丢弃空项）——
        /// 让规范化的责任落在一处，而不是散在界面代码里。
        /// </summary>
        public async Task SetRoutingAsync(RoutingSettings routing)
        {
            ArgumentNullException.ThrowIfNull(routing);

            _state.Routing = new RoutingSettings
            {
                Mode = routing.Mode,
                DirectDomains = Normalize(routing.DirectDomains),
                DirectAddresses = Normalize(routing.DirectAddresses),
                ProxyDomains = Normalize(routing.ProxyDomains)
            };

            await SaveAndNotifyAsync();
        }

        private static List<string> Normalize(IEnumerable<string>? values) =>
            values is null
                ? new List<string>()
                : values
                    .Select(value => value?.Trim().ToLowerInvariant() ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

        /// <summary>值真的变了才落盘并广播，避免给 ToggleSwitch 赋初值时触发一次无谓的写盘。</summary>
        private async Task ApplyAsync(bool current, bool value, Action assign)
        {
            if (current == value)
            {
                return;
            }

            assign();
            await SaveAndNotifyAsync();
        }

        private async Task SaveAndNotifyAsync()
        {
            await SaveAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private async Task SaveAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(_state, AppSettingsJsonContext.Default.AppSettingsState);
                await File.WriteAllTextAsync(FilePath, json);
            }
            catch (Exception)
            {
                // 写不进去只影响下次启动的默认值，不该让设置操作报错。
            }
        }
    }
}
