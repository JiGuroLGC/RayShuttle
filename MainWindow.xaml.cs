using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using RayShuttle.Common;
using RayShuttle.Controls;
using RayShuttle.Interop;
using RayShuttle.Models;
using RayShuttle.Services;
using RayShuttle.Views;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace RayShuttle
{
    /// <summary>
    /// 应用外壳：窗口外观（自定义标题栏、Mica 背景、最小尺寸约束）、
    /// 悬浮侧栏与页面导航。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        /// <summary>
        /// 窗口锁定的客户区宽高比。背景图的宽度是按客户区高度推算的，
        /// 比例一旦固定，图片宽度就等价于窗口宽度的固定占比，布局可以用星号比例静态表达。
        /// </summary>
        private const double WindowAspectRatio = 16.0 / 9.0;

        /// <summary>最小客户区宽度（有效像素）。高度由等比锁定推出，不单独设。</summary>
        private const double MinClientWidth = 1160;

        /// <summary>首次启动的目标客户区宽度（有效像素），实际会按屏幕可用区域收敛。</summary>
        private const double InitialClientWidth = 1360;

        private const int IndicatorAnimationMilliseconds = 320;
        private const int SplashFadeMilliseconds = 320;

        private readonly WindowAspectLock _aspectLock;
        private readonly IntPtr _windowHandle;
        private readonly TrayIcon _tray;

        /// <summary>当前打开的托盘菜单（没有则为 null）。用完即弃，每次右键新建一个。</summary>
        private TrayMenuWindow? _trayMenu;

        /// <summary>连接 / 断开的提示音。</summary>
        private readonly ConnectionChime _chime;

        /// <summary>连接状态的托盘气泡通知。</summary>
        private readonly ConnectionNotifier _notifier;

        /// <summary>断线自动重连。</summary>
        private readonly ConnectionSupervisor _supervisor;

        /// <summary>累计连接时长 / 流量的记录器（持久化到本地，统计页读取它）。</summary>
        private readonly UsageStatsRecorder _statsRecorder;

        [DllImport("user32")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        /// <summary>
        /// 是否由「开机自启」拉起来的（命令行带 <c>--tray</c>）。
        ///
        /// 这种情况下窗口只创建、不显示：开机时弹一个窗口出来很打扰，
        /// 用户要的是「安静地在后台待命，需要时点托盘图标」。
        /// </summary>
        private static bool StartedMinimized => Environment
            .GetCommandLineArgs()
            .Any(argument => string.Equals(
                argument,
                StartupRegistration.TrayArgument,
                StringComparison.OrdinalIgnoreCase));

        private SidebarItem[] _sidebarItems = Array.Empty<SidebarItem>();
        private Type[] _pageTypes = Array.Empty<Type>();
        private Storyboard? _indicatorAnimation;
        private bool _isLoaded;
        private bool _isBackdropVisible;
        private bool _isOnboarding;
        private bool _isExiting;

        /// <summary>是否已经至少揭示过一次主界面（决定开屏遮罩是否还需要播放）。</summary>
        private bool _mainShellRevealed;

        /// <summary>
        /// 引导步骤判定完成前，忽略页面入场信号：构造期 HomePage 就会被 Navigate 进来并触发一次入场，
        /// 此时还不知道该显示引导页还是主界面。判定完成后（<see cref="InitializeAsync"/> 末尾）才启用，
        /// 之后的每次切页入场都由 <see cref="OnPageEntranceStarted"/> 同步背景图。
        /// </summary>
        private bool _pageEntranceReady;

        public MainWindow()
        {
            InitializeComponent();

            ConfigureTitleBar();

            // 锁死等比缩放。挂接失败也不影响使用，只是退化为普通可缩放窗口。
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _aspectLock = new WindowAspectLock(_windowHandle, WindowAspectRatio, MinClientWidth);
            _aspectLock.Attach();

            // 在窗口首次绘制之前就按屏幕可用区把尺寸定好：否则窗口会先以默认尺寸闪现，
            // 再被 OnRootLoaded 调整到目标尺寸，表现为开屏→主页之间的「跳变」。
            // 这里用 Win32 DPI（不依赖 XamlRoot，构造期即可用）算缩放。
            var scale = GetDpiScale();
            ApplyInitialSize(scale);
            ApplyMinimumSize(scale);

            Closed += OnClosed;
            AppWindow.Closing += OnAppWindowClosing;

            // 系统托盘：单击恢复窗口；右键弹自定义菜单（不再是系统那种老式弹出菜单）。
            _tray = new TrayIcon();
            _tray.Add("光梭 RayShuttle");
            _tray.ShowRequested += (_, _) => DispatcherQueue.TryEnqueue(RestoreFromTray);
            _tray.ContextMenuRequested += (_, _) => DispatcherQueue.TryEnqueue(ShowTrayMenu);

            // 连接 / 断开的提示音。在这里建实例是为了绑定 UI 线程的调度器（见 ConnectionChime）。
            _chime = new ConnectionChime(VpnConnectionService.Current);

            // 连接状态的托盘气泡通知（同样要在 UI 线程建，见 ConnectionNotifier）。
            _notifier = new ConnectionNotifier(VpnConnectionService.Current, _tray);

            // 断线自动重连。它依赖 _notifier 发结果通知，所以必须在其之后创建。
            _supervisor = new ConnectionSupervisor(VpnConnectionService.Current, _notifier);

            // 累计统计记录器：在 UI 线程建好、立即订阅连接事件，保证从不打开统计页也持续累计。
            _statsRecorder = UsageStatsRecorder.Current;

            _sidebarItems = new[] { HomeItem, ServersItem, SettingsItem, StatisticsItem };
            _pageTypes = new[] { typeof(HomePage), typeof(ServersPage), typeof(SettingsPage), typeof(StatisticsPage) };

            ContentFrame.Navigated += OnContentFrameNavigated;
            // 页面入场时同步背景图：让地球与页面元素在同一时刻、同参数一起出现（见 OnPageEntranceStarted）。
            EntranceAnimation.Started += OnPageEntranceStarted;
            ContentFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            // 必须在窗口关闭的这一刻同步撤掉系统代理：窗口关闭不会等待异步流程，
            // 漏掉的话用户下次开机就是断网状态。
            VpnConnectionService.Current.ShutdownSynchronously();
            _statsRecorder.Flush();
            _aspectLock.Detach();
            _chime.Dispose();
            _supervisor.Dispose();
            _notifier.Dispose();
            _tray.Dispose();

            // 主窗口就是整个应用：关掉它就该结束进程。
            // 不显式退出的话，托盘菜单那类附属窗口会把进程拖着不放
            // （表现就是「主窗口关了，菜单还杵在那、任务管理器里进程还在」）。
            Application.Current.Exit();
        }

        /// <summary>
        /// 关闭窗口时：若开启了「最小化到托盘」且并非真正退出，则取消关闭、把窗口藏到托盘，
        /// 保持后台连接不中断。真正退出（托盘菜单「退出」）会置 <see cref="_isExiting"/> 绕过此处。
        /// </summary>
        private void OnAppWindowClosing(object sender, AppWindowClosingEventArgs args)
        {
            if (_isExiting)
            {
                return;
            }

            if (AppSettings.Current.MinimizeToTray)
            {
                args.Cancel = true;
                TrayIcon.HideWindow(_windowHandle);
            }
        }

        /// <summary>
        /// 把窗口从托盘恢复出来。托盘单击、托盘菜单的「打开光梭」「设置」，
        /// 以及**再次启动本程序**（单实例转交过来）都走这里。
        /// </summary>
        public void RestoreFromTray()
        {
            TrayIcon.RestoreWindow(_windowHandle);
            Activate();
        }

        /// <summary>由托盘菜单「退出」调用：先收起菜单，再标记真正退出、关闭窗口触发清理。</summary>
        private void RealExit()
        {
            _trayMenu?.Dismiss();
            _trayMenu = null;
            _isExiting = true;
            Close();
        }

        /// <summary>
        /// 弹出托盘右键菜单。每次新建一个窗口（菜单很短命，用完即弃）；
        /// 若上一个还开着先关掉，避免叠两层。
        /// </summary>
        private void ShowTrayMenu()
        {
            _trayMenu?.Dismiss();
            _trayMenu = null;

            // 优先贴到托盘图标的右上角；系统给不出图标位置（比如图标被折叠进溢出区）时退回光标处。
            int left, top, right, bottom;
            if (!_tray.TryGetIconRect(out left, out top, out right, out bottom))
            {
                if (!TrayIcon.TryGetCursorPosition(out var cursorX, out var cursorY))
                {
                    return;
                }

                left = cursorX;
                right = cursorX;
                bottom = cursorY;
                top = cursorY - 16;
            }

            var menu = new TrayMenuWindow();
            menu.ConnectRequested += OnTrayConnectRequested;
            menu.OpenRequested += (_, _) => RestoreFromTray();
            menu.SettingsRequested += OnTraySettingsRequested;
            menu.ExitRequested += (_, _) => RealExit();
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_trayMenu, menu))
                {
                    _trayMenu = null;
                }
            };

            _trayMenu = menu;
            menu.ShowAtIcon(left, top, right, bottom);
        }

        /// <summary>托盘菜单的「连接 / 断开」：没连就按当前选中节点连，已连就断开。</summary>
        private async void OnTrayConnectRequested(object? sender, EventArgs e)
        {
            var connection = VpnConnectionService.Current;
            _trayMenu?.Dismiss();

            try
            {
                if (connection.Status is VpnStatus.Connected or VpnStatus.Connecting)
                {
                    await connection.DisconnectAsync();
                    return;
                }

                if (VpnSessionState.CurrentNode is not { } node)
                {
                    // 还没有可用节点：把窗口拉出来，让用户去节点页挑一个。
                    RestoreFromTray();
                    return;
                }

                await connection.ConnectAsync(node);
            }
            catch (Exception)
            {
                // 异步事件处理器里漏掉的异常会直接终止进程，必须兜住。
                RestoreFromTray();
            }
        }

        /// <summary>托盘菜单的「设置」：恢复窗口并跳到设置页。</summary>
        private void OnTraySettingsRequested(object? sender, EventArgs e)
        {
            _trayMenu?.Dismiss();
            RestoreFromTray();
            Navigate(typeof(SettingsPage));
        }

        private void ConfigureTitleBar()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 标题栏按钮融进深色背景。
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0xF2, 0xF6, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x6C, 0x7A, 0x99);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        private void OnRootLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;

            // 尺寸已在构造期定好（消除跳变），这里只在布局完成后补一次最小尺寸收敛，
            // 并定位侧栏选中指示块。
            var scale = GetDpiScale();
            ApplyMinimumSize(scale);

            SyncSidebarSelection(animate: false);

            // 开机自启：**在首帧布局完成之后**才藏窗口。更早藏会导致内容树一直不进入
            // Loaded 状态，初始化（拉节点、建托盘图标）就都不会跑了。
            // 这一小段时间窗口是盖在开屏遮罩底下的，看到的是一块深色底，不会有内容闪出。
            if (StartedMinimized)
            {
                TrayIcon.HideWindow(_windowHandle);
            }

            // 开屏遮罩不在这里播放：是否播放由 InitializeAsync → RefreshOnboardingStepAsync 决定
            // （首启走欢迎页，不走开屏；正常启动 / 登录成功后进主界面时才由 RunSplashAsync 播放）。
            _ = InitializeAsync();
        }

        /// <summary>开屏遮罩的入场：整体淡入 + 光球脉冲，与首页「光」的意象一致。</summary>
        private void PlaySplashIntro()
        {
            SplashOverlay.Opacity = 0;

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(620)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, SplashOverlay);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            new Storyboard { Children = { fade } }.Begin();

            // 脉冲光环：放大并淡出，循环往复，营造「呼吸」的科技感。
            PulseRing.RenderTransformOrigin = new Point(0.5, 0.5);
            PulseRing.RenderTransform = new ScaleTransform();

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var scaleX = new DoubleAnimation
            {
                From = 1,
                To = 1.18,
                Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = easing
            };
            Storyboard.SetTarget(scaleX, PulseRing.RenderTransform);
            Storyboard.SetTargetProperty(scaleX, nameof(ScaleTransform.ScaleX));

            var scaleY = new DoubleAnimation
            {
                From = 1,
                To = 1.18,
                Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = easing
            };
            Storyboard.SetTarget(scaleY, PulseRing.RenderTransform);
            Storyboard.SetTargetProperty(scaleY, nameof(ScaleTransform.ScaleY));

            var opacity = new DoubleAnimation
            {
                From = 0.55,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacity, PulseRing);
            Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));

            var pulse = new Storyboard { Children = { scaleX, scaleY, opacity } };
            pulse.Begin();
        }

        /// <summary>开屏遮罩淡出并收起（不再拦截输入），揭示下方已就绪的主界面。</summary>
        private Task FadeOutSplashAsync()
        {
            var completed = new TaskCompletionSource();

            var animation = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(SplashFadeMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, SplashOverlay);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, _) =>
            {
                SplashOverlay.Visibility = Visibility.Collapsed;
                completed.TrySetResult();
            };
            storyboard.Begin();

            return completed.Task;
        }

        private double GetDpiScale() => GetDpiForWindow(_windowHandle) / 96.0;

        /// <summary>
        /// 读取本地设置与账户状态，决定首启走欢迎页，还是直接进入主界面/引导页。
        ///
        /// 开屏遮罩不再在这里播放：首启由欢迎页（WelcomePage）先登场；只有当流程走到
        /// 「进入主界面」时才由 <see cref="EnterMainShellAsync"/> 播放开屏，节点加载就在那一刻
        /// 的遮罩之下完成（链路为主地址 → 备用地址 → 本地文件，全部失败也只是进主页）。
        /// </summary>
        private async Task InitializeAsync()
        {
            // 设置要先于节点拉取就绪（云端地址等）。
            await AppSettings.Current.LoadAsync();
            await AccountStore.Current.LoadAsync();

            await RefreshOnboardingStepAsync();

            // 引导步骤已判定，之后的每次切页入场都用 OnPageEntranceStarted 同步背景图。
            _pageEntranceReady = true;
        }

        /// <summary>
        /// 按当前账户状态决定显示哪一步引导；全部完成则进入主界面。
        ///
        /// 这是全流程**唯一的推进入口**：欢迎页后的隐私协议页、登录页、设置页的「退出登录」、
        /// 以及「撤回用户协议同意」都调用它，因此不存在某条路径漏判、把用户放进主界面的可能。
        ///
        /// 步骤顺序：欢迎页（首启）→ 隐私协议 → 登录 → 进入主界面（此处才播放开屏遮罩）。
        /// </summary>
        public async Task RefreshOnboardingStepAsync()
        {
            var store = AccountStore.Current;

            if (!store.PrivacyAccepted)
            {
                ShowOnboardingStep(typeof(WelcomePage));
            }
            else if (!store.IsSignedIn)
            {
                ShowOnboardingStep(typeof(LoginPage));
            }
            else
            {
                await EnterMainShellAsync();
            }
        }

        /// <summary>API 地址变更等场景下强制重新拉取节点。</summary>
        public Task ReloadNodesAsync() => LoadNodesAsync(force: true);

        private void ShowOnboardingStep(Type pageType)
        {
            _isOnboarding = true;

            // 隐藏主界面而不是只盖一层蒙版：不给任何绕过引导的可能。
            MainShell.Visibility = Visibility.Collapsed;
            OnboardingFrame.Visibility = Visibility.Visible;

            if (OnboardingFrame.CurrentSourcePageType != pageType)
            {
                OnboardingFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
            }

            // 欢迎页的外观与开屏遮罩一致（不带右侧背景图），其余引导页保留背景图。
            SyncBackdropVisibility(pageType != typeof(WelcomePage));
        }

        /// <summary>
        /// 进入主界面。需要时先播放开屏遮罩，主界面在遮罩**之下**揭示。
        ///
        /// **顺序不能反**：先揭示主界面再让遮罩淡入的话，遮罩在 620ms 淡入期间是透明的，
        /// 用户会先看到主界面闪一下、遮罩才盖上来。所以揭示必须放到遮罩已经盖好之后。
        ///
        /// 开屏遮罩的职责是「进入主界面时的品牌加载」：正常启动（已登录）与登录成功后各一次。
        /// 用 <see cref="_mainShellRevealed"/> 防止已在主界面时重复播放；
        /// 用 <c>wasOnboarding</c> 让「撤回同意 → 重新登录」再次进入时也能播放。
        /// </summary>
        private async Task EnterMainShellAsync()
        {
            var wasOnboarding = _isOnboarding;

            if (wasOnboarding || !_mainShellRevealed)
            {
                _mainShellRevealed = true;
                await RunSplashAsync();
            }
            else
            {
                RevealMainShell();
            }
        }

        private void RevealMainShell()
        {
            _isOnboarding = false;

            OnboardingFrame.Visibility = Visibility.Collapsed;
            OnboardingFrame.BackStack.Clear();
            // 清空内容，保证下次回到登录页时一定会重新导航（否则带不进去提示文案）。
            OnboardingFrame.Content = null;
            MainShell.Visibility = Visibility.Visible;

            SyncBackdropVisibility(ContentFrame.CurrentSourcePageType == typeof(HomePage));
        }

        /// <summary>
        /// 播放开屏遮罩：从背景淡入 → 加载节点 → 在遮罩之下揭示主界面 → 淡出。
        ///
        /// 只有「进入主界面」时才会走到这里，因此此时必定已登录，节点加载也一定有意义。
        /// </summary>
        private async Task RunSplashAsync()
        {
            // 遮罩是在淡入的（起始不透明度为 0），所以淡入期间下面不能露出任何内容，
            // 否则会「先闪一下登录页 / 主界面，遮罩才盖上来」。主界面此刻本就隐藏；
            // 把引导层也先收起，让遮罩从纯背景里淡入。
            // 顶栏（AppTitleBar）是唯一例外：它在 XAML 里刻意声明在 SplashOverlay 之后
            // 浮于遮罩之上、全程可见——若被遮罩盖住，反而会「先露出、再被覆盖、
            // 揭示时又重现」跳闪两次（见 MainWindow.xaml.cs 顶栏注释）。
            OnboardingFrame.Visibility = Visibility.Collapsed;

            SplashOverlay.Visibility = Visibility.Visible;
            PlaySplashIntro();

            // 权威加载：开屏遮罩之下完成，揭示时节点已在，无需二次加载。
            // **不设固定时长**：加载链路本身已是「主地址 → 备用地址 → 本地文件」，
            // 每一步都有超时上界，这里等它跑完即可；即便全部失败（只拿到空列表），
            // 也只是直接进入主界面，绝不把用户卡在遮罩上。
            await LoadNodesAsync();

            // 主界面在遮罩之下揭示，用户看不到这次切换；随后遮罩淡出把它露出来。
            RevealMainShell();

            // 启动自动连接。**不 await**：连接可能要十几秒（等内核就绪 + 接管），
            // 把开屏遮罩一直挂着等它，用户只会觉得「这软件启动好慢」。
            if (AppSettings.Current.AutoConnectOnLaunch)
            {
                _ = AutoConnectAsync();
            }

            await FadeOutSplashAsync();
        }

        /// <summary>
        /// 启动时自动连接当前选中的节点。
        ///
        /// 只在「已登录且有节点」时才会被调用到（<see cref="RunSplashAsync"/> 里节点已经就位）。
        /// 失败不额外提示——错误链路（首页弹窗 + 托盘通知）本来就会覆盖到。
        /// </summary>
        private static async Task AutoConnectAsync()
        {
            var connection = VpnConnectionService.Current;

            if (connection.Status is VpnStatus.Connected or VpnStatus.Connecting)
            {
                return;
            }

            if (VpnSessionState.CurrentNode is not { } node)
            {
                return;
            }

            try
            {
                await connection.ConnectAsync(node);
            }
            catch (Exception exception)
            {
                // 自动连接失败不该影响主流程。
                NodeDiagnostics.LogException("启动自动连接", exception);
            }
        }

        /// <summary>
        /// 拉取并加载当前账号的节点配置。
        ///
        /// 放在这里而不是页面里，是因为**节点配置同时充当了凭据的校验**：
        /// 邀请码格式合法不代表它有效，只有用它解开对应的配置文件才算通过。
        /// 所以解密失败时要把用户退回登录页并说明原因，
        /// 而不是让他在一个空节点列表里自行困惑。
        /// </summary>
        private readonly SemaphoreSlim _nodeLoadGate = new(1, 1);

        private async Task LoadNodesAsync(bool force = false)
        {
            // 防重入：引导流程与开屏重试可能先后进入，避免两次加载交错（互相覆盖节点列表 / 探针）。
            await _nodeLoadGate.WaitAsync();
            try
            {
            // 已登录且已有节点时（开屏已加载或上次成功），不重复拉取；force=true 仅用于设置页切换节点源优先级后的强制刷新。
            if (!force && NodeRepository.Current.HasNodes)
            {
                EnsureSelectedNodeIsValid();
            }
            else
            {
                var store = AccountStore.Current;

                byte[]? nodeKey;
                try
                {
                    nodeKey = await store.DeriveNodeKeyAsync();
                }
                catch (Exception)
                {
                    // 本地凭据解不开（DPAPI 失败）时按「没有密钥」处理，
                    // 走与未登录相同的分支，界面会显示统一的失败文案，不会崩掉。
                    nodeKey = null;
                }

                // **失败时不再登出用户。** 曾在这里把用户踢回登录页，
                // 结果就是十几秒后被莫名其妙地登出；而拿不到节点时保持登录态并无额外风险。
                await NodeRepository.Current.LoadAsync(nodeKey, store.UserName);

                EnsureSelectedNodeIsValid();
            }

            // 节点到手（无论来自开屏预加载还是本次拉取）后触发一次延迟探测并缓存，
            // 节点页直接读缓存，无需每次打开都测。必须在 UI 线程发起：探测写回走 UI 调度器。
            if (NodeRepository.Current.HasNodes)
            {
                _ = NodeLatencyProber.ProbeAsync(NodeRepository.Current.Nodes);
            }
            }
            finally
            {
                _nodeLoadGate.Release();
            }
        }

        /// <summary>节点列表换了之后，把选中项落到一个确实存在的节点上；没有节点就置空。</summary>
        private static void EnsureSelectedNodeIsValid()
        {
            var nodes = NodeRepository.Current.Nodes;
            var current = VpnSessionState.CurrentNode;

            if (current is not null && nodes.Any(node => node.Id == current.Id))
            {
                return;
            }

            VpnSessionState.CurrentNode = nodes.Count > 0 ? nodes[0] : null;
        }

        /// <summary>
        /// 按屏幕可用区域收敛首次启动尺寸。
        /// AppWindow 使用物理像素，因此需要乘上 DPI 缩放。
        ///
        /// 客户区尺寸只能从最紧的那个约束反推，再按比例推出另一边——
        /// 分别夹取宽高会破坏锁定好的比例，窗口一起来就是变形的。
        /// </summary>
        private void ApplyInitialSize(double scale)
        {
            var workArea = DisplayArea
                .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;

            WindowAspectLock.MeasureNonClientSize(_windowHandle, out var nonClientWidth, out var nonClientHeight);

            var maxClientWidth = Math.Max(1, workArea.Width - nonClientWidth);
            var maxClientHeight = Math.Max(1, workArea.Height - nonClientHeight);

            var clientWidth = (int)(InitialClientWidth * scale);
            clientWidth = Math.Min(clientWidth, maxClientWidth);
            clientWidth = Math.Min(clientWidth, (int)(maxClientHeight * WindowAspectRatio));

            var clientHeight = (int)Math.Round(clientWidth / WindowAspectRatio);
            var width = clientWidth + nonClientWidth;
            var height = clientHeight + nonClientHeight;

            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                workArea.X + ((workArea.Width - width) / 2),
                workArea.Y + ((workArea.Height - height) / 2)));
        }

        /// <summary>
        /// 下限交给两处：
        ///   1. 等比锁定里那道 —— 真正生效的那道，因为被反推出来的那一维系统并不检查；
        ///   2. 系统一份 —— 让拖动方向那一维也尽早被拦住，不至于先抖一下再被拉回。
        /// 另外还会按屏幕可用区域收窄：屏幕比设计下限还小时，硬守下限会把窗口顶出屏幕。
        /// </summary>
        private void ApplyMinimumSize(double scale)
        {
            var workArea = DisplayArea
                .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;

            WindowAspectLock.MeasureNonClientSize(_windowHandle, out var nonClientWidth, out var nonClientHeight);

            var availableByWidth = (workArea.Width - nonClientWidth) / scale;
            var availableByHeight = ((workArea.Height - nonClientHeight) / scale) * WindowAspectRatio;

            // 两个方向都要能放下，取更紧的那个——只看宽度的话高度仍可能超出屏幕。
            var clientWidth = Math.Min(MinClientWidth, Math.Min(availableByWidth, availableByHeight));
            var clientHeight = clientWidth / WindowAspectRatio;

            _aspectLock.MinimumClientWidth = clientWidth;

            if (AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            presenter.PreferredMinimumWidth = (int)Math.Round((clientWidth + nonClientWidth) * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round((clientHeight + nonClientHeight) * scale);
        }

        private void OnHomeItemClicked(object? sender, EventArgs e) => Navigate(typeof(HomePage));

        private void OnServersItemClicked(object? sender, EventArgs e) => Navigate(typeof(ServersPage));

        private void OnSettingsItemClicked(object? sender, EventArgs e) => Navigate(typeof(SettingsPage));

        private void OnStatisticsItemClicked(object? sender, EventArgs e) => Navigate(typeof(StatisticsPage));

        private void Navigate(Type pageType)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
            }
        }

        // 背景图不在这里同步：切页时由页面自身的入场动画（EntranceAnimation.Started →
        // OnPageEntranceStarted）驱动，才能与元素同时、同参数出现。
        private void OnContentFrameNavigated(object sender, NavigationEventArgs e) =>
            SyncSidebarSelection(_isLoaded);

        /// <summary>
        /// 页面入场开始时触发：把背景图**硬切**到该页应有的状态。
        ///
        /// 背景图本身不做任何动画（见 <see cref="SyncBackdropVisibility"/>），这里只负责让它的
        /// **出现时刻**与页面元素入场对齐——既不提前（否则地球先于元素出现），也不淡入。
        ///
        /// 显示与否按当前**正在显示的是哪一页**来定，因此构造期 HomePage 的那次入场
        /// （见 <see cref="_pageEntranceReady"/>）与各处跳转都能得到正确结果。
        /// </summary>
        private void OnPageEntranceStarted(object? sender, EventArgs e)
        {
            if (!_pageEntranceReady)
            {
                return;
            }

            SyncBackdropVisibility(ShouldShowBackdrop());
        }

        /// <summary>当前页是否需要显示背景图：引导阶段除欢迎页外要，主界面仅首页要。</summary>
        private bool ShouldShowBackdrop() => _isOnboarding
            ? OnboardingFrame.CurrentSourcePageType != typeof(WelcomePage)
            : ContentFrame.CurrentSourcePageType == typeof(HomePage);

        /// <summary>
        /// 背景图只在首页（及引导页除欢迎页外）显示。
        ///
        /// **直接切换不透明度，绝不做淡入淡出。** 背景图是覆盖全窗的全局图层，只要它的不透明度
        /// 落在 0 与 1 之间（处于混合过程中），它与底色的交界处就会出现色差/割裂；而它和背景
        /// 又不可能在同一帧里「同时淡入」，所以任何淡入动画都会暴露这道缝。
        ///
        /// 但它**出现的时刻**必须和页面元素对齐——这不在导航事件里做，而是由页面入场动画广播的
        /// <see cref="EntranceAnimation.Started"/> 驱动（见 <see cref="OnPageEntranceStarted"/>），
        /// 否则地球会早于页面元素出现。用字段记录目标状态只是为了少赋值（赋值本身幂等）。
        /// </summary>
        private void SyncBackdropVisibility(bool visible)
        {
            if (_isBackdropVisible == visible)
            {
                return;
            }

            _isBackdropVisible = visible;
            BackdropLayer.Opacity = visible ? 1 : 0;
        }

        /// <summary>
        /// 侧栏尺寸变化意味着导航项刚刚完成测量（首次布局），此时指示块才能定位。
        /// </summary>
        private void OnSidebarSizeChanged(object sender, SizeChangedEventArgs e) =>
            SyncSidebarSelection(animate: false);

        /// <summary>
        /// 让侧栏选中态跟上当前页面。
        /// </summary>
        private void SyncSidebarSelection(bool animate)
        {
            var index = Array.IndexOf(_pageTypes, ContentFrame.CurrentSourcePageType);

            for (var i = 0; i < _sidebarItems.Length; i++)
            {
                _sidebarItems[i].IsSelected = i == index;
            }

            if (index < 0 || !TryGetItemOffset(index, out var offset))
            {
                return;
            }

            if (animate)
            {
                AnimateIndicator(offset);
            }
            else
            {
                // 动画会以 HoldEnd 形式持续占据该属性，直接赋值前必须先停掉它。
                _indicatorAnimation?.Stop();
                _indicatorAnimation = null;
                IndicatorOffset.Y = offset;
            }
        }

        /// <summary>
        /// 计算第 index 个导航项在侧栏内的纵向位置。所有导航项等高，因此直接按
        /// 「项高 + 间距」推算。布局尚未测量完成时返回 false，等 SizeChanged 再同步。
        /// 注意这里不能用 TransformToVisual：元素还没进入可视化树时它会抛 COMException。
        /// </summary>
        private bool TryGetItemOffset(int index, out double offset)
        {
            offset = 0;

            var itemHeight = _sidebarItems[0].ActualHeight;
            if (itemHeight <= 0)
            {
                return false;
            }

            offset = index * (itemHeight + SidebarItemsPanel.Spacing);
            return true;
        }

        private void AnimateIndicator(double offset)
        {
            var animation = new DoubleAnimation
            {
                To = offset,
                Duration = new Duration(TimeSpan.FromMilliseconds(IndicatorAnimationMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, IndicatorOffset);
            Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.Y));

            // 不调用 Stop：直接重新 Begin 会从当前值续上，避免指示块先跳回起点。
            _indicatorAnimation = new Storyboard();
            _indicatorAnimation.Children.Add(animation);
            _indicatorAnimation.Begin();
        }
    }
}
