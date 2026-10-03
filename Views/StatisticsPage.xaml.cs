using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RayShuttle.Common;
using RayShuttle.Models;
using RayShuttle.Services;

namespace RayShuttle.Views
{
    /// <summary>
    /// 统计页：本次会话实时流量 / 时长、累计连接时长与流量（持久化）、实时速率曲线、延迟波动。
    ///
    /// 曲线只保留**本次运行**的样本（内存，约 60 点）：速率按 1s 采样、延迟按 5s 采样，
    /// 不持久化历史。累计值由 <see cref="UsageStatsRecorder"/> 维护，本页只读。
    /// </summary>
    public sealed partial class StatisticsPage : Page
    {
        /// <summary>曲线保留的样本数：速率 60 点≈1 分钟，延迟 60 点≈5 分钟。</summary>
        private const int MaxSamples = 60;

        private readonly List<double> _downSamples = new();
        private readonly List<double> _upSamples = new();
        private readonly List<double> _latencySamples = new();

        public StatisticsPage()
        {
            InitializeComponent();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EntranceAnimation.Run(
                HeaderBlock,
                SessionCard,
                TotalsCard,
                SpeedCard,
                LatencyCard,
                DailyCard);

            var connection = VpnConnectionService.Current;
            connection.StatsChanged += OnStatsChanged;
            connection.StatusChanged += OnStatusChanged;

            RefreshNow();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var connection = VpnConnectionService.Current;
            connection.StatsChanged -= OnStatsChanged;
            connection.StatusChanged -= OnStatusChanged;
        }

        private void OnStatusChanged(object? sender, EventArgs e) =>
            // StatusChanged 可能在连接 / 断开的异步流程里于后台线程抛出，UI 更新必须切回 UI 线程。
            DispatcherQueue.TryEnqueue(RefreshNow);

        private void OnStatsChanged(object? sender, EventArgs e)
        {
            // StatsChanged 由后台监视定时器抛出，UI 更新必须切回 UI 线程。
            var connection = VpnConnectionService.Current;
            var down = connection.DownloadSpeedBps;
            var up = connection.UploadSpeedBps;
            var latency = connection.LatencyMs;

            DispatcherQueue.TryEnqueue(() =>
            {
                PushSample(_downSamples, down);
                PushSample(_upSamples, up);
                if (latency is { } value)
                {
                    PushSample(_latencySamples, value);
                }

                // 每次刷新都重新赋值一个新列表引用，确保 Sparkline 的 DP 回调被触发重绘。
                DownSparkline.Points = new List<double>(_downSamples);
                UpSparkline.Points = new List<double>(_upSamples);
                LatencySparkline.Points = new List<double>(_latencySamples);

                // 实时速率数值（与曲线同源于 StatsChanged）。
                DownSpeedText.Text = down > 0 ? $"{FormatHelpers.FormatBytes(down)}/s" : "0 B/s";
                UpSpeedText.Text = up > 0 ? $"{FormatHelpers.FormatBytes(up)}/s" : "0 B/s";

                RefreshNow();
            });
        }

        private static void PushSample(List<double> buffer, double value)
        {
            buffer.Add(value);
            while (buffer.Count > MaxSamples)
            {
                buffer.RemoveAt(0);
            }
        }

        /// <summary>把当前连接状态 / 累计值刷到界面。可在 UI 线程直接调用。</summary>
        private void RefreshNow()
        {
            var connection = VpnConnectionService.Current;

            // 本次会话：仅连接期间显示。
            if (connection.Status == VpnStatus.Connected)
            {
                SessionCard.Visibility = Visibility.Visible;
                SessionTrafficText.Text =
                    $"↓ {FormatHelpers.FormatBytes(connection.SessionDownloadBytes)}　↑ {FormatHelpers.FormatBytes(connection.SessionUploadBytes)}";

                var elapsed = connection.ConnectedAt is { } start
                    ? DateTime.Now - start
                    : TimeSpan.Zero;
                SessionDurationText.Text = FormatDuration((long)elapsed.TotalSeconds);
            }
            else
            {
                SessionCard.Visibility = Visibility.Collapsed;

                // 断开后实时速率失去意义，复位以免残留上一次连接的速度。
                DownSpeedText.Text = "--";
                UpSpeedText.Text = "--";
            }

            // 累计：来自持久化记录器（显示时叠加当前会话增量）。
            var recorder = UsageStatsRecorder.Current;
            TotalDurationText.Text = FormatDuration(recorder.TotalConnectedSeconds);
            TotalTrafficText.Text =
                $"↓ {FormatHelpers.FormatBytes(recorder.TotalDownloadBytes)}　↑ {FormatHelpers.FormatBytes(recorder.TotalUploadBytes)}";

            // 延迟波动：当前值 + 区间 min / avg / max。
            if (connection.LatencyMs is { } current)
            {
                LatencyCurrentText.Text = $"{current} ms";
            }
            else
            {
                LatencyCurrentText.Text = "--";
            }

            if (_latencySamples.Count > 0)
            {
                var min = (int)_latencySamples.Min();
                var max = (int)_latencySamples.Max();
                var avg = (int)_latencySamples.Average();
                LatencyRangeText.Text = $"{min} / {avg} / {max} ms";
            }
            else
            {
                LatencyRangeText.Text = "--";
            }

            RefreshDaily();
        }

        /// <summary>把持久化的每日流量趋势刷到柱状图，并汇总近 7 / 30 天总量。</summary>
        private void RefreshDaily()
        {
            var series = UsageStatsRecorder.Current.GetDailySeries(30);
            if (series.Count == 0)
            {
                return;
            }

            // 每次刷新都赋新列表引用，确保 BarChart 的 DP 回调被触发重绘。
            DailyChart.PrimaryValues = series.Select(d => (double)d.DownloadBytes).ToList();
            DailyChart.SecondaryValues = series.Select(d => (double)d.UploadBytes).ToList();

            long PeriodTotal(int days) =>
                series.TakeLast(days).Sum(d => d.DownloadBytes + d.UploadBytes);

            WeekTrafficText.Text = FormatHelpers.FormatBytes(PeriodTotal(7));
            MonthTrafficText.Text = FormatHelpers.FormatBytes(PeriodTotal(30));
        }

        private static string FormatDuration(long seconds)
        {
            if (seconds <= 0)
            {
                return "0 秒";
            }

            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalDays >= 1
                ? $"{(int)span.TotalDays} 天 {span.Hours} 小时"
                : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
        }
    }
}
