using System;
using System.IO;

namespace RayShuttle.Services
{
    /// <summary>
    /// 诊断日志的落盘位置。所有日志统一放在 <c>%LOCALAPPDATA%\RayShuttle\log\</c> 下，
    /// 与 account.json / settings.json / stats.json 等**配置**分开——日志是易变、可丢弃的，
    /// 不该和配置混在同一层目录里。
    ///
    /// **启动时由 <see cref="Rotate"/> 归档**：把现有日志改名为 <c>*.prev</c>，让新会话从空白开始，
    /// 同时保留上一次会话的日志——**上一次很可能正是崩溃现场，直接删掉就再也查不到了**。
    /// 每个日志只保留「当前 + 上一份」，总占用有上界。
    /// 注意 <c>proxy-backup.txt</c> 不是日志（是崩溃恢复用的状态），**不在处理范围内**。
    ///
    /// 提权助手是独立进程（RayShuttle.TunHelper），引用不到本类，它用同样的路径规则写
    /// <c>tun-helper.log</c>；改这里的目录名时助手那边要同步（见 TunSession.LogPath）。
    /// </summary>
    internal static class AppLog
    {
        /// <summary>归档后缀：上一次会话的日志以它结尾。</summary>
        private const string PreviousSuffix = ".prev";

        private static readonly string BaseFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle");

        /// <summary>日志目录：<c>%LOCALAPPDATA%\RayShuttle\log</c>。</summary>
        public static string Folder { get; } = Path.Combine(BaseFolder, "log");

        /// <summary>
        /// 迁移前散落在配置目录（<c>%LOCALAPPDATA%\RayShuttle\</c> 根）里的旧日志文件名。
        /// 只认这几个名字，避免误删同目录下的配置文件与 <c>proxy-backup.txt</c>。
        /// </summary>
        private static readonly string[] LegacyLogFileNames =
        {
            "node-diagnostics.log",
            "proxy-trace.log",
            "tun-helper.log"
        };

        /// <summary>拼出日志目录下某个文件的完整路径。</summary>
        public static string PathFor(string fileName) => Path.Combine(Folder, fileName);

        /// <summary>
        /// 归档日志并在新会话中重新开始：把日志目录里每个文件改名为 <c>*.prev</c>
        /// （覆盖更早的 .prev），并删除旧位置遗留的同名日志。启动时调用。
        /// 任何失败都被吞掉：归档日志绝不能拖慢或阻断启动。
        /// </summary>
        public static void Rotate()
        {
            ArchiveExistingLogs();
            RemoveLegacyLogs();
        }

        private static void ArchiveExistingLogs()
        {
            try
            {
                if (!Directory.Exists(Folder))
                {
                    return;
                }

                // 先取文件快照再改名：避免一边枚举一边移动导致的枚举异常。
                foreach (var file in Directory.GetFiles(Folder))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var previous = file + PreviousSuffix;
                    try
                    {
                        // 单个文件归档失败（例如仍被助手进程占用）就跳过，不影响其它文件与启动。
                        File.Move(file, previous, overwrite: true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void RemoveLegacyLogs()
        {
            foreach (var name in LegacyLogFileNames)
            {
                try
                {
                    var path = Path.Combine(BaseFolder, name);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
