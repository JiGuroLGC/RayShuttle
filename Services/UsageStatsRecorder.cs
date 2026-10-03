using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>一天的网络用量聚合（持久化历史的基本单元）。</summary>
    internal sealed class DailyUsageEntry
    {
        /// <summary>日期，格式 <c>yyyy-MM-dd</c>（便于按字典序排序与比较）。</summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>当天累计下载字节。</summary>
        public long DownloadBytes { get; set; }

        /// <summary>当天累计上传字节。</summary>
        public long UploadBytes { get; set; }

        /// <summary>当天累计连接秒数。</summary>
        public long ConnectedSeconds { get; set; }
    }

    /// <summary>累计统计的持久化状态。</summary>
    internal sealed class UsageStatsState
    {
        /// <summary>历史累计连接秒数（不含当前正在进行的会话；显示时再叠加）。</summary>
        public long TotalConnectedSeconds { get; set; }

        /// <summary>历史累计下载字节。</summary>
        public long TotalDownloadBytes { get; set; }

        /// <summary>历史累计上传字节。</summary>
        public long TotalUploadBytes { get; set; }

        /// <summary>按天聚合的历史用量（每天一条）。统计页据此画每日流量趋势。</summary>
        public List<DailyUsageEntry> Daily { get; set; } = new();
    }

    /// <summary>源生成器序列化（与 AppSettings 同理，避免裁剪构建里反射式序列化被剥掉）。</summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(UsageStatsState))]
    [JsonSerializable(typeof(DailyUsageEntry))]
    [JsonSerializable(typeof(List<DailyUsageEntry>))]
    internal sealed partial class UsageStatsJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// 累计连接时长 / 流量记录器。
    ///
    /// 订阅 <see cref="VpnConnectionService"/> 的状态与统计事件，把「已连接时长」「本次会话产生的流量」
    /// 累加到本地文件（<c>%LOCALAPPDATA%\RayShuttle\stats.json</c>）。
    ///
    /// 与统计页解耦：本类在 <see cref="MainWindow"/> 构造时即被实例化并订阅，因此**即使从不打开统计页**
    /// 也会持续累计；统计页只是读取这些累计值并画实时曲线。
    ///
    /// 累计值以「历史持久化部分 + 当前会话增量」方式暴露：每次统计事件只把自上次事件以来的
    /// 时间差与流量差累加进持久化状态，因此断开重连不会重复计数。
    ///
    /// <see cref="_state"/>.Daily 按天持久化每天的流量与时长，统计页据此画出**跨会话的每日流量趋势**；
    /// 实时速率 / 延迟曲线仍只保留本次运行的样本（内存），不落盘。
    /// </summary>
    internal sealed class UsageStatsRecorder
    {
        public static UsageStatsRecorder Current { get; } = new();

        private readonly object _gate = new();
        private UsageStatsState _state = new();
        private bool _dirty;

        private DateTime? _sessionStart;
        private DateTime _lastTick;
        private long _lastSessionDown;
        private long _lastSessionUp;
        private DateTime _lastFlush = DateTime.MinValue;

        // 当天用量切分：记录「当天零点（相对累计值）的基线」，当天的增量 = 累计值 − 基线。
        // 跨午夜时按日期更换基线，确保每天各计各的、不串天、不重复。
        private string? _dayKey;
        private long _dayStartDown;
        private long _dayStartUp;
        private long _dayStartSeconds;

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle",
            "stats.json");

        public UsageStatsRecorder()
        {
            Load();

            var connection = VpnConnectionService.Current;
            connection.StatusChanged += OnStatusChanged;
            connection.StatsChanged += OnStatsChanged;
        }

        /// <summary>累计连接时长（秒）：历史持久化值 + 当前仍在连接的会话时长。</summary>
        public long TotalConnectedSeconds
        {
            get
            {
                lock (_gate)
                {
                    var total = _state.TotalConnectedSeconds;
                    if (_sessionStart is { } start)
                    {
                        total += (long)(DateTime.Now - start).TotalSeconds;
                    }

                    return total;
                }
            }
        }

        /// <summary>累计下载字节：历史 + 当前会话自上次采样以来的增量。</summary>
        public long TotalDownloadBytes
        {
            get
            {
                lock (_gate)
                {
                    var live = VpnConnectionService.Current.SessionDownloadBytes - _lastSessionDown;
                    return Math.Max(_state.TotalDownloadBytes, _state.TotalDownloadBytes + live);
                }
            }
        }

        /// <summary>累计上传字节：历史 + 当前会话自上次采样以来的增量。</summary>
        public long TotalUploadBytes
        {
            get
            {
                lock (_gate)
                {
                    var live = VpnConnectionService.Current.SessionUploadBytes - _lastSessionUp;
                    return Math.Max(_state.TotalUploadBytes, _state.TotalUploadBytes + live);
                }
            }
        }

        private void OnStatusChanged(object? sender, EventArgs e)
        {
            var connection = VpnConnectionService.Current;

            if (connection.Status == VpnStatus.Connected)
            {
                lock (_gate)
                {
                    _sessionStart = DateTime.Now;
                    _lastTick = DateTime.Now;
                    _lastSessionDown = connection.SessionDownloadBytes;
                    _lastSessionUp = connection.SessionUploadBytes;
                }
            }
            else if (connection.Status is VpnStatus.Disconnected or VpnStatus.Failed)
            {
                Flush();
            }
        }

        private void OnStatsChanged(object? sender, EventArgs e)
        {
            var connection = VpnConnectionService.Current;
            if (connection.Status != VpnStatus.Connected || _sessionStart is null)
            {
                return;
            }

            lock (_gate)
            {
                var now = DateTime.Now;

                var deltaSeconds = (long)(now - _lastTick).TotalSeconds;
                if (deltaSeconds > 0)
                {
                    _state.TotalConnectedSeconds += deltaSeconds;
                    _lastTick = now;
                }

                var down = connection.SessionDownloadBytes;
                var up = connection.SessionUploadBytes;
                if (down > _lastSessionDown)
                {
                    _state.TotalDownloadBytes += down - _lastSessionDown;
                    _lastSessionDown = down;
                }

                if (up > _lastSessionUp)
                {
                    _state.TotalUploadBytes += up - _lastSessionUp;
                    _lastSessionUp = up;
                }

                _dirty = true;
                CaptureToday();

                // 节流落盘：至少每 5 秒写一次，平衡 IO 与异常退出的数据丢失。
                if ((now - _lastFlush).TotalSeconds >= 5)
                {
                    Save();
                    _lastFlush = now;
                }
            }
        }

        /// <summary>把当前会话的时间 / 流量结算进持久化状态并落盘（断电式退出前的保险）。</summary>
        public void Flush()
        {
            lock (_gate)
            {
                if (_sessionStart is not null)
                {
                    var now = DateTime.Now;
                    var deltaSeconds = (long)(now - _lastTick).TotalSeconds;
                    if (deltaSeconds > 0)
                    {
                        _state.TotalConnectedSeconds += deltaSeconds;
                    }

                    // 结算最后的流量增量（断开瞬间内核还在，Session*Bytes 仍是最终值）。
                    var connection = VpnConnectionService.Current;
                    if (connection.SessionDownloadBytes > _lastSessionDown)
                    {
                        _state.TotalDownloadBytes += connection.SessionDownloadBytes - _lastSessionDown;
                        _lastSessionDown = connection.SessionDownloadBytes;
                    }

                    if (connection.SessionUploadBytes > _lastSessionUp)
                    {
                        _state.TotalUploadBytes += connection.SessionUploadBytes - _lastSessionUp;
                        _lastSessionUp = connection.SessionUploadBytes;
                    }

                    _sessionStart = null;
                }

                if (_dirty)
                {
                    Save();
                }
            }
        }

        /// <summary>
        /// 把「今天」这一天的用量增量结算进 <see cref="_state"/>.Daily。
        /// 当天增量 = 当前累计值 − 当天基线，因此跨午夜换基线也不会串天或重复计数。
        /// 幂等：同一天重复调用只会覆盖当天的条目。
        /// </summary>
        private void CaptureToday()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            // 日期变了（或首次调用）就重新建立当天基线——从已持久化的当天条目里把已有部分扣掉，
            // 这样本会话只累加「本次运行新增」的那部分，不会把之前已有的当天用量再算一遍。
            if (_dayKey != today)
            {
                _dayKey = today;
                var existing = _state.Daily.FirstOrDefault(d => d.Date == today);
                _dayStartDown = _state.TotalDownloadBytes - (existing?.DownloadBytes ?? 0);
                _dayStartUp = _state.TotalUploadBytes - (existing?.UploadBytes ?? 0);
                _dayStartSeconds = _state.TotalConnectedSeconds - (existing?.ConnectedSeconds ?? 0);
            }

            var entry = _state.Daily.FirstOrDefault(d => d.Date == today);
            if (entry is null)
            {
                entry = new DailyUsageEntry { Date = today };
                _state.Daily.Add(entry);
            }

            entry.DownloadBytes = Math.Max(0, _state.TotalDownloadBytes - _dayStartDown);
            entry.UploadBytes = Math.Max(0, _state.TotalUploadBytes - _dayStartUp);
            entry.ConnectedSeconds = Math.Max(0, _state.TotalConnectedSeconds - _dayStartSeconds);
        }

        /// <summary>
        /// 返回最近 <paramref name="days"/> 天的用量序列（含今天，缺数据的日子补 0）。
        /// 统计页据此画每日流量趋势，并汇总近 7 / 30 天总量。
        /// </summary>
        public List<DailyUsageEntry> GetDailySeries(int days)
        {
            if (days <= 0)
            {
                days = 1;
            }

            lock (_gate)
            {
                // 先刷新今天这一格，确保刚产生的增量立即可见。
                if (VpnConnectionService.Current.Status == VpnStatus.Connected)
                {
                    CaptureToday();
                }

                var today = DateTime.Now.Date;
                var map = _state.Daily.ToDictionary(d => d.Date, d => d);
                var result = new List<DailyUsageEntry>(days);

                for (var i = days - 1; i >= 0; i--)
                {
                    var key = today.AddDays(-i).ToString("yyyy-MM-dd");
                    result.Add(map.TryGetValue(key, out var entry)
                        ? entry
                        : new DailyUsageEntry { Date = key });
                }

                return result;
            }
        }

        private void Save()
        {
            try
            {
                lock (_gate)
                {
                    // 只保留最近约一年的每日数据，避免文件无界膨胀。
                    if (_state.Daily.Count > 400)
                    {
                        var cutoff = DateTime.Now.AddDays(-366).ToString("yyyy-MM-dd");
                        _state.Daily = _state.Daily
                            .Where(d => string.CompareOrdinal(d.Date, cutoff) >= 0)
                            .ToList();
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(_state, UsageStatsJsonContext.Default.UsageStatsState);
                File.WriteAllText(FilePath, json);
                _dirty = false;
                _lastFlush = DateTime.Now;
            }
            catch (Exception exception)
            {
                NodeDiagnostics.LogException("保存累计统计", exception);
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var state = JsonSerializer.Deserialize(json, UsageStatsJsonContext.Default.UsageStatsState);
                    if (state is not null)
                    {
                        _state = state;
                    }
                }
            }
            catch (Exception exception)
            {
                NodeDiagnostics.LogException("读取累计统计", exception);
            }
        }
    }
}
