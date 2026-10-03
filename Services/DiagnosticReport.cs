using System;
using System.Text;
using RayShuttle.Common;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>
    /// 拼一份可以直接粘贴给开发者的诊断报告。
    ///
    /// **刻意不含任何凭据**：邀请码、会话令牌、节点 uuid / 密码 / 地址一律不写入。
    /// 节点只用「名称 · 协议 · 传输 · 节点 id · 通道 id」标识——足够定位是哪条通道的哪一跳出了问题，
    /// 又不至于把服务器端点散播出去。
    ///
    /// 之所以放在 Services 而不是弹窗里：这份文本既是剪贴板内容，也可能将来用于导出发文件，
    /// 不该绑死在某个 UI 类上。
    /// </summary>
    internal static class DiagnosticReport
    {
        private const int CoreOutputTailLines = 40;
        private const int LogTailLines = 60;

        public static string Build(ConnectionFailure failure)
        {
            var builder = new StringBuilder();

            AppendHeader(builder);
            AppendFailure(builder, failure);
            AppendState(builder);
            AppendRawDetails(builder);

            return builder.ToString();
        }

        private static void AppendHeader(StringBuilder builder)
        {
            builder.AppendLine("光梭 RayShuttle 诊断报告");
            builder.AppendLine(Separator);
            builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine($"版本：{AppInfo.Version}");
            builder.AppendLine($"系统：{AppInfo.OsDescription}");
            builder.AppendLine($"运行时：{AppInfo.Runtime}");
            builder.AppendLine(Separator);
        }

        private static void AppendFailure(StringBuilder builder, ConnectionFailure failure)
        {
            builder.AppendLine($"错误类型：{failure.KindText}");
            builder.AppendLine($"摘要：{failure.Summary}");

            if (!string.IsNullOrWhiteSpace(failure.Suggestion))
            {
                builder.AppendLine("建议：");
                builder.AppendLine(failure.Suggestion);
            }

            // 结构化细节（内核输出尾部、助手报错、真实代理状态等）。
            // 没有这段的话，像「tun2socks 参数不被识别」这类具体原因只会留在本地日志里，
            // 用户贴报告过来就无从定位。
            if (!string.IsNullOrWhiteSpace(failure.Detail))
            {
                builder.AppendLine("细节：");
                builder.AppendLine(failure.Detail);
            }

            builder.AppendLine(Separator);
        }

        private static void AppendState(StringBuilder builder)
        {
            var connection = VpnConnectionService.Current;
            var node = connection.TargetNode ?? VpnSessionState.CurrentNode;

            builder.AppendLine("应用状态");
            builder.AppendLine($"  连接状态：{connection.Status}");
            builder.AppendLine($"  接管方式：{connection.InterceptorName}");

            if (node is null)
            {
                builder.AppendLine("  节点：无");
            }
            else
            {
                builder.AppendLine($"  节点：{node.Name}（{node.Protocol.ToString().ToUpperInvariant()} · {node.ProtocolText}）");
                builder.AppendLine($"  节点 id：{node.Id}　　通道：{(string.IsNullOrEmpty(node.SlotId) ? "无" : node.SlotId)}");
                builder.AppendLine($"  延迟标注：{node.LatencyText}");

                // 用量标签不含敏感信息，但对判断「通道是不是快没了」很有用。
                if (node.HasUsage)
                {
                    builder.AppendLine($"  通道用量：{node.UsageText}");
                }
            }

            builder.AppendLine(Separator);
        }

        private static void AppendRawDetails(StringBuilder builder)
        {
            var connection = VpnConnectionService.Current;
            var coreOutput = TakeTail(connection.LastCoreOutput, CoreOutputTailLines);
            var logTail = NodeDiagnostics.ReadTail(LogTailLines);

            builder.AppendLine("系统代理现状");
            builder.AppendLine($"  {SystemProxyManager.DescribeCurrent()}");
            builder.AppendLine(Separator);

            builder.AppendLine($"Xray 内核输出（末 {CoreOutputTailLines} 行）");
            builder.AppendLine(string.IsNullOrWhiteSpace(coreOutput) ? "  （空）" : coreOutput);
            builder.AppendLine(Separator);

            builder.AppendLine($"诊断日志（末 {LogTailLines} 行）");
            builder.AppendLine(string.IsNullOrWhiteSpace(logTail) ? "  （空）" : logTail);
            builder.AppendLine(Separator);

            builder.AppendLine($"完整诊断日志位置：{NodeDiagnostics.FileLocation}");
        }

        private static string TakeTail(string? text, int maxLines)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length <= maxLines)
            {
                return string.Join(Environment.NewLine, lines);
            }

            var tail = new string[maxLines];
            Array.Copy(lines, lines.Length - maxLines, tail, 0, maxLines);
            return string.Join(Environment.NewLine, tail);
        }

        private static string Separator => new('-', 48);
    }
}
