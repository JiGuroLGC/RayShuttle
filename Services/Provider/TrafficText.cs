using System;
using System.Globalization;

namespace RayShuttle.Services.Provider
{
    /// <summary>
    /// 解析供应商的流量字段。
    ///
    /// 它给的是**带单位的字符串**（实测 `"5GB"` / `"0B"` / `"232.66MB"`），
    /// 不是数字。解析不出来必须返回 null 表示「无法判断」——**不能默认成 0**：
    /// 默认成 0 会被当成「已耗尽」，于是每次都去刷新，白白把账号烧光。
    /// </summary>
    internal static class TrafficText
    {
        public static long? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var value = text!.Trim();
            var split = value.Length - 1;

            // 单位是末尾的 1~2 个字符（B / KB / MB / GB / TB）。
            while (split > 0 && !char.IsDigit(value[split - 1]) && value[split - 1] != '.')
            {
                split--;
            }

            if (split <= 0 || split >= value.Length)
            {
                return null;
            }

            var numberPart = value[..split].Trim();
            var unitPart = value[split..].Trim().ToUpperInvariant();

            if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            var multiplier = unitPart switch
            {
                "B" => 1d,
                "KB" => 1024d,
                "MB" => 1024d * 1024d,
                "GB" => 1024d * 1024d * 1024d,
                "TB" => 1024d * 1024d * 1024d * 1024d,
                _ => 0d
            };

            return multiplier <= 0 ? null : (long)Math.Round(number * multiplier);
        }

        /// <summary>把字节数格式化成人类可读的形式（仅用于展示）。</summary>
        public static string Format(long? bytes)
        {
            if (bytes is null || bytes.Value <= 0)
            {
                return "--";
            }

            var size = bytes.Value;
            string[] units = { "B", "KB", "MB", "GB", "TB" };

            var index = 0;
            double value = size;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }

            return index == 0
                ? $"{size} B"
                : $"{value.ToString(index <= 1 ? "0" : "0.##", CultureInfo.InvariantCulture)} {units[index]}";
        }
    }
}
