namespace RayShuttle.Common
{
    /// <summary>界面通用的数值格式化。</summary>
    public static class FormatHelpers
    {
        /// <summary>把字节/秒格式化成带单位的速率，例如 "1.2 MB/s" / "845 KB/s" / "0 B/s"。</summary>
        public static string FormatRate(long bytesPerSecond)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            if (bytesPerSecond < 0)
            {
                bytesPerSecond = 0;
            }

            double value = bytesPerSecond;

            if (value >= GB)
            {
                return $"{value / GB:0.00} GB/s";
            }

            if (value >= MB)
            {
                return $"{value / MB:0.0} MB/s";
            }

            if (value >= KB)
            {
                return $"{value / KB:0.0} KB/s";
            }

            return $"{value:0} B/s";
        }
    }
}
