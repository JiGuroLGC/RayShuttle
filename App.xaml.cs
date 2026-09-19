using Microsoft.UI.Xaml;
using RayShuttle.Services;

namespace RayShuttle
{
    /// <summary>
    /// 应用入口。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 主窗口。引导流程中的页面需要靠它推进步骤与进入主界面。
        /// </summary>
        public MainWindow? MainWindow { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 最优先做这件事：上次若是异常退出，系统代理可能还指着已经关闭的本地端口，
            // 用户此刻处于断网状态，必须在做任何别的事之前把网络恢复回来。
            VpnConnectionService.CleanupStaleState();

            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
    }
}
