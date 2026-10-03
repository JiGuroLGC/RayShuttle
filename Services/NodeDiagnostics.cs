using System;
using System.IO;
using System.Text;

namespace RayShuttle.Services
{
    /// <summary>
    /// 节点加载与连接的诊断日志。只在本地落盘一个文本文件，把所有「拿不到节点」的环节都记下来，
    /// 方便定位到底是网络不通、Worker 拒绝（400/401/403/503）、还是供应商订阅拉取失败。
    ///
    /// **不进入任何用户可见文案**：对外文案仍是统一的「无法获取节点配置」
    /// （区分凭据细节等于告诉外人用户名是否已注册）。这里只服务于本地排查，
    /// 以及「复制诊断信息」时随报告带走。
    /// </summary>
    internal static class NodeDiagnostics
    {
        /// <summary>
        /// 日志文件的上限。超过就从头部丢掉一半。
        ///
        /// 从前完全没有上限：只有在每次加载节点时 Reset 一次，而应用可能连续运行几天
        /// （最小化到托盘保持连接），日志会一直长。诊断信息要整体复制进剪贴板，也不能无限大。
        /// </summary>
        private const long MaxFileBytes = 512 * 1024;

        private static readonly string FilePath = AppLog.PathFor("node-diagnostics.log");

        private static readonly object Gate = new();

        /// <summary>日志文件位置，供诊断报告里写明「完整日志在哪里」。</summary>
        public static string FileLocation => FilePath;

        public static void Log(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            Append(line);
        }

        /// <summary>每次启动清一次旧日志，避免不同会话的条目混在一起难读。</summary>
        public static void Reset()
        {
            try
            {
                lock (Gate)
                {
                    if (File.Exists(FilePath))
                    {
                        File.WriteAllText(FilePath, string.Empty, Encoding.UTF8);
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>记录完整异常（类型 + 消息 + 堆栈），用于定位裁剪构建里偶发的反射式 JSON 等深层异常。</summary>
        public static void LogException(string context, Exception exception)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 异常发生在：{context}");

            var current = exception;
            var depth = 0;
            while (current is not null && depth < 4)
            {
                builder.AppendLine($"  {current.GetType().FullName}: {current.Message}");
                if (!string.IsNullOrWhiteSpace(current.StackTrace))
                {
                    builder.AppendLine(current.StackTrace);
                }

                current = current.InnerException;
                depth++;
            }

            Append(builder.ToString());
        }

        /// <summary>
        /// 读取日志末尾若干行，供「复制诊断信息」拼报告。文件不存在或读不到都返回空串——
        /// 诊断报告少一段内容，好过整个复制功能失败。
        /// </summary>
        public static string ReadTail(int maxLines)
        {
            if (maxLines <= 0)
            {
                return string.Empty;
            }

            try
            {
                lock (Gate)
                {
                    if (!File.Exists(FilePath))
                    {
                        return string.Empty;
                    }

                    return JoinTail(File.ReadAllLines(FilePath, Encoding.UTF8), maxLines);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string JoinTail(string[] lines, int maxLines)
        {
            if (lines.Length <= maxLines)
            {
                return string.Join(Environment.NewLine, lines);
            }

            var tail = new string[maxLines];
            Array.Copy(lines, lines.Length - maxLines, tail, 0, maxLines);
            return string.Join(Environment.NewLine, tail);
        }

        private static void Append(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.AppendAllText(FilePath, text, Encoding.UTF8);
                    TrimIfTooLarge();
                }
            }
            catch
            {
                // 诊断日志写不进去绝不能影响主流程。
            }
        }

        /// <summary>超过上限就丢掉最早的一半——诊断时真正有用的是最近的条目。</summary>
        private static void TrimIfTooLarge()
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaxFileBytes)
            {
                return;
            }

            var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
            if (lines.Length <= 1)
            {
                return;
            }

            var keep = Math.Max(1, lines.Length / 2);
            var tail = new string[keep];
            Array.Copy(lines, lines.Length - keep, tail, 0, keep);
            File.WriteAllLines(FilePath, tail, Encoding.UTF8);
        }
    }
}
