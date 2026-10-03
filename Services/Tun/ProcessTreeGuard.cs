using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RayShuttle.Services.Tun
{
    /// <summary>
    /// 进程树退出守护（Windows Job Object）。
    ///
    /// 主进程启动时创建一个**命名** Job（KILL_ON_JOB_CLOSE）并把自己放进去：
    /// 此后 xray.exe（主进程的子进程）天然继承；提权助手通过启动参数里的 job 名
    /// 自己加入（提权方向才有权限），tun2socks 再继承助手。整棵树绑在一个 Job 上。
    ///
    /// **主进程无论以何种方式死亡**——正常退出、托盘退出、关窗、被任务管理器杀——
    /// 内核持有的 Job 句柄随进程关闭；Job 的最后一个句柄关闭时，系统会终结树内
    /// 全部进程。这样「只杀主进程」也绝不会有 xray / helper / tun2socks 残留吃资源。
    ///
    /// 失败全是静默的：Job 只是兜底，原有的父进程监视（helper）与 XrayProcessRunner
    /// 的主动 Stop 仍然是第一道清理。
    /// </summary>
    internal static class ProcessTreeGuard
    {
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const uint JOB_OBJECT_ASSIGN_PROCESS = 0x0004;
        private const int SddlRevision = 1;

        /// <summary>受保护 DACL，授予 Everyone 完全控制——让提权助手能打开并加入。</summary>
        private const string SddlEveryoneAll = "D:P(A;OICI;GA;;;WD)";

        private static IntPtr? _jobHandle;

        /// <summary>本进程的守护 Job 名（每进程唯一）。</summary>
        public static string JobName { get; } = $"RayShuttle.ExitGuard.{Environment.ProcessId}";

        /// <summary>应用启动最早阶段调用一次。永不释放句柄——由进程死亡触发全树终结。</summary>
        public static void Initialize()
        {
            try
            {
                // **必须显式授予宽松 DACL**：默认安全描述符下，提权助手打开本进程创建的
                // Job 会得到拒绝访问（Win32 5，踩过）。 Everyone 完全控制 + 受保护 DACL。
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        SddlEveryoneAll, SddlRevision, out var securityDescriptor, IntPtr.Zero))
                {
                    NodeDiagnostics.Log($"进程树守护：构造安全描述符失败（Win32 {Marshal.GetLastWin32Error()}）。");
                    return;
                }

                try
                {
                    var attributes = new SECURITY_ATTRIBUTES
                    {
                        nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                        lpSecurityDescriptor = securityDescriptor,
                        bInheritHandle = false
                    };

                    var handle = CreateJobObjectW(ref attributes, JobName);
                    if (handle == IntPtr.Zero)
                    {
                        NodeDiagnostics.Log("进程树守护：创建 Job 失败。");
                        return;
                    }

                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    if (!SetInformationJobObject(
                            handle,
                            JobObjectExtendedLimitInformationClass,
                            ref info,
                            Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    {
                        NodeDiagnostics.Log($"进程树守护：设置 Job 限制失败（Win32 {Marshal.GetLastWin32Error()}）。");
                        _ = CloseHandle(handle);
                        return;
                    }

                    using var current = Process.GetCurrentProcess();
                    if (!AssignProcessToJobObject(handle, current.Handle))
                    {
                        NodeDiagnostics.Log($"进程树守护：主进程加入 Job 失败（Win32 {Marshal.GetLastWin32Error()}）。");
                        _ = CloseHandle(handle);
                        return;
                    }

                    // 刻意不 Close：句柄与进程同生共死。
                    _jobHandle = handle;
                }
                finally
                {
                    _ = LocalFree(securityDescriptor);
                }
            }
            catch (Exception exception)
            {
                NodeDiagnostics.LogException("进程树守护初始化", exception);
            }
        }

        /// <summary>初始化是否成功（供诊断报告参考）。</summary>
        public static bool IsArmed => _jobHandle is not null;

        // ---------------------------------------------------------- 供助手使用的打开/加入

        internal static IntPtr TryOpenJob(string name)
        {
            return name.Length == 0
                ? IntPtr.Zero
                : OpenJobObjectW(JOB_OBJECT_ASSIGN_PROCESS, false, name);
        }

        internal static bool AssignProcess(IntPtr job, IntPtr process) =>
            AssignProcessToJobObject(job, process);

        internal static void CloseJobHandle(IntPtr job) => _ = CloseHandle(job);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(ref SECURITY_ATTRIBUTES attributes, string? name);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string sddl, uint revision, out IntPtr securityDescriptor, IntPtr descriptorSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenJobObjectW(uint desiredAccess, bool inheritHandles, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
