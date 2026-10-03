using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using RayShuttle.Services;

namespace RayShuttle
{
    /// <summary>
    /// 应用入口。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>单实例的键。同一个键只允许有一个实例存活。</summary>
        private const string InstanceKey = "RayShuttle.Main";

        /// <summary>
        /// 主窗口。引导流程中的页面需要靠它推进步骤与进入主界面。
        /// </summary>
        public MainWindow? MainWindow { get; private set; }

        public App()
        {
            InitializeComponent();

            // 兜底：任何未处理异常都要留下线索，并尽量还原系统代理——
            // 崩溃若发生在连接期间，不还原会让用户直接断网（正常退出另走 VpnConnectionService.ShutdownSynchronously）。
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 单实例判定必须放在**最前面**：下面非主实例的分支会直接退出本进程，
            // 而 VpnConnectionService.CleanupStaleState 会还原系统代理设置 ——
            // 那绝不能由第二个实例执行，否则会把正在运行的那个实例的连接打断（用户直接断网）。
            var current = AppInstance.GetCurrent();
            var primary = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (!primary.IsCurrent)
            {
                RedirectActivationTo(primary, current.GetActivatedEventArgs());
                return;
            }

            // 主实例：别人再次启动本程序时，把窗口从托盘恢复出来（否则点了没反应）。
            primary.Activated += OnPrimaryActivated;

            // 归档日志（%LOCALAPPDATA%\RayShuttle\log）：把现有日志改名为 *.prev，让本次运行从空白
            // 开始，同时**保住上一次会话（很可能正是崩溃现场）的日志**。这是纯文件操作、不碰网络，
            // 不影响下面「网络恢复最优先」的语义；放在 CleanupStaleState 之前，好让本次代理恢复的
            // 追踪也被记录。
            AppLog.Rotate();

            // 最优先做这件事：上次若是异常退出，系统代理可能还指着已经关闭的本地端口，
            // 用户此刻处于断网状态，必须在做任何别的事之前把网络恢复回来。
            VpnConnectionService.CleanupStaleState();

            // 进程树退出守护：把主进程放进带 KILL_ON_JOB_CLOSE 的 Job——
            // xray / 提权助手 / tun2socks 全部挂在这棵树上，主进程无论怎么死都一起终结。
            Services.Tun.ProcessTreeGuard.Initialize();

            // 直接创建并激活主窗口。开屏品牌页现在是主窗口内的一层遮罩（SplashOverlay），
            // 在 InitializeAsync 中完成预加载并淡出，窗口尺寸/位置始终一致，无任何跳变。
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }

        /// <summary>UI 线程未处理异常。记日志并尽力还原系统代理，然后照常崩溃（不掩盖问题）。</summary>
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
            HandleFatal("UI 未处理异常", e.Exception);

        /// <summary>其它线程上的致命异常（此刻进程即将终止）。</summary>
        private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
            HandleFatal("进程未处理异常",
                e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "(无异常信息)"));

        /// <summary>未被观察的任务异常：非致命，只记录，并标记已观察以免升级为进程终止。</summary>
        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                NodeDiagnostics.LogException("未观察的任务异常", e.Exception);
            }
            catch
            {
            }

            e.SetObserved();
        }

        /// <summary>记录异常 + 尽力还原系统代理。补救动作本身绝不能再抛异常。</summary>
        private static void HandleFatal(string context, Exception exception)
        {
            try
            {
                NodeDiagnostics.LogException(context, exception);
            }
            catch
            {
            }

            try
            {
                // 崩溃若发生在连接期间，系统代理可能仍指向已关闭的本地端口；
                // 这里立即还原，避免用户重启前一直断网（下次启动还会再兜一次）。
                VpnConnectionService.CleanupStaleState();
            }
            catch
            {
            }
        }

        /// <summary>再次启动本程序时，主实例收到事件：把窗口从托盘恢复出来。</summary>
        private void OnPrimaryActivated(object? sender, AppActivationArguments args) =>
            MainWindow?.DispatcherQueue.TryEnqueue(() => MainWindow?.RestoreFromTray());

        /// <summary>
        /// 把这次激活转交给已有实例，然后退出本进程 —— 也就是「禁止多开」。
        ///
        /// 转交必须在**后台线程**上等：RedirectActivationToAsync 内部要走消息循环，
        /// 直接在当前（UI）线程同步等待会死锁。
        /// </summary>
        private static void RedirectActivationTo(AppInstance primary, AppActivationArguments activation)
        {
            using var completed = new SemaphoreSlim(0, 1);
            _ = Task.Run(() =>
            {
                try
                {
                    primary.RedirectActivationToAsync(activation).AsTask().Wait();
                }
                catch
                {
                    // 转交失败也必须退出，否则就真的变成第二个实例了。
                }
                finally
                {
                    completed.Release();
                }
            });

            // 加个上限：万一转交卡住，也不能让「第二个实例」一直挂着不退出。
            completed.Wait(TimeSpan.FromSeconds(5));
            Environment.Exit(0);
        }
    }
}
