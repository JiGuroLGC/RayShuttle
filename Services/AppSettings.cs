using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>节点源的优先级。</summary>
    public enum NodeSourcePreference
    {
        /// <summary>优先主源。默认值。</summary>
        Primary,

        /// <summary>优先备用源。主源在所在网络下不可达时可切换。</summary>
        Fallback
    }

    internal sealed class AppSettingsState
    {
        public NodeSourcePreference PreferredNodeSource { get; set; } = NodeSourcePreference.Primary;
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
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle",
            "settings.json");

        public static AppSettings Current { get; } = new();

        private AppSettingsState _state = new();

        private AppSettings()
        {
        }

        public NodeSourcePreference PreferredNodeSource => _state.PreferredNodeSource;

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
            }
            catch
            {
                // 文件损坏或格式变更都按默认值处理，不能让设置读不出来就起不来。
                _state = new AppSettingsState();
            }
        }

        public async Task SetPreferredNodeSourceAsync(NodeSourcePreference value)
        {
            if (_state.PreferredNodeSource == value)
            {
                return;
            }

            _state.PreferredNodeSource = value;
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
