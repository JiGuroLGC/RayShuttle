using System;
using System.Linq;
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
        private const int BackdropFadeMilliseconds = 260;

        private readonly WindowAspectLock _aspectLock;
        private readonly IntPtr _windowHandle;

        private SidebarItem[] _sidebarItems = Array.Empty<SidebarItem>();
        private Type[] _pageTypes = Array.Empty<Type>();
        private Storyboard? _indicatorAnimation;
        private bool _isLoaded;
        private bool _isBackdropVisible;
        private bool _isOnboarding;

        public MainWindow()
        {
            InitializeComponent();

            ConfigureTitleBar();

            // 锁死等比缩放。挂接失败也不影响使用，只是退化为普通可缩放窗口。
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _aspectLock = new WindowAspectLock(_windowHandle, WindowAspectRatio, MinClientWidth);
            _aspectLock.Attach();

            Closed += OnClosed;

            _sidebarItems = new[] { HomeItem, ServersItem, SettingsItem };
            _pageTypes = new[] { typeof(HomePage), typeof(ServersPage), typeof(SettingsPage) };

            ContentFrame.Navigated += OnContentFrameNavigated;
            ContentFrame.Navigate(typeof(HomePage), null, new EntranceNavigationTransitionInfo());
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            // 必须在窗口关闭的这一刻同步撤掉系统代理：窗口关闭不会等待异步流程，
            // 漏掉的话用户下次开机就是断网状态。
            VpnConnectionService.Current.ShutdownSynchronously();
            _aspectLock.Detach();
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

            // 布局完成后才能拿到真实的 DPI 缩放与控件位置。
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            ApplyInitialSize(scale);
            ApplyMinimumSize(scale);

            SyncSidebarSelection(animate: false);

            _ = InitializeAsync();
        }

        /// <summary>
        /// 读取本地设置与账户状态，决定首次进入主界面还是走引导流程。
        /// </summary>
        private async Task InitializeAsync()
        {
            // 设置要先于节点拉取就绪：拉取顺序依赖「优先使用哪个源」。
            await AppSettings.Current.LoadAsync();
            await AccountStore.Current.LoadAsync();
            await RefreshOnboardingStepAsync();
        }

        /// <summary>按当前设置重新拉取节点。用户在设置里切换节点源后调用。</summary>
        public Task ReloadNodesAsync() => LoadNodesAsync();

        /// <summary>
        /// 按当前账户状态决定显示哪一步引导；全部完成则进入主界面。
        ///
        /// 这是全流程**唯一的推进入口**：隐私协议页、登录页、设置页的「退出登录」
        /// 都调用它，因此不存在某条路径漏判、把用户放进主界面的可能。
        /// </summary>
        public Task RefreshOnboardingStepAsync()
        {
            var store = AccountStore.Current;

            if (!store.PrivacyAccepted)
            {
                ShowOnboardingStep(typeof(PrivacyPolicyPage));
            }
            else if (!store.IsSignedIn)
            {
                ShowOnboardingStep(typeof(LoginPage));
            }
            else
            {
                ExitOnboarding();
            }

            return Task.CompletedTask;
        }

        private void ShowOnboardingStep(Type pageType)
        {
            _isOnboarding = true;

            // 隐藏主界面而不是只盖一层蒙版：不给任何绕过引导的可能。
            MainShell.Visibility = Visibility.Collapsed;
            OnboardingFrame.Visibility = Visibility.Visible;

            if (OnboardingFrame.CurrentSourcePageType != pageType)
            {
                OnboardingFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            }

            SyncBackdropVisibility(true);
        }

        private void ExitOnboarding()
        {
            _isOnboarding = false;

            OnboardingFrame.Visibility = Visibility.Collapsed;
            OnboardingFrame.BackStack.Clear();
            // 清空内容，保证下次回到登录页时一定会重新导航（否则带不进去提示文案）。
            OnboardingFrame.Content = null;
            MainShell.Visibility = Visibility.Visible;

            SyncBackdropVisibility(ContentFrame.CurrentSourcePageType == typeof(HomePage));

            // 进入主界面后立即拉取该账号的节点配置，不阻塞界面。
            _ = LoadNodesAsync();
        }

        /// <summary>
        /// 拉取并加载当前账号的节点配置。
        ///
        /// 放在这里而不是页面里，是因为**节点配置同时充当了凭据的校验**：
        /// 邀请码格式合法不代表它有效，只有用它解开对应的配置文件才算通过。
        /// 所以解密失败时要把用户退回登录页并说明原因，
        /// 而不是让他在一个空节点列表里自行困惑。
        /// </summary>
        private async Task LoadNodesAsync()
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

        private void Navigate(Type pageType)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            }
        }

        private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
        {
            SyncSidebarSelection(_isLoaded);
            SyncBackdropVisibility(_isOnboarding || e.SourcePageType == typeof(HomePage));
        }

        /// <summary>
        /// 背景图只在首页显示，切页时淡入淡出。
        /// 这里用字段记录期望状态，不去读 Opacity：动画是 HoldEnd，
        /// 读到的值不可靠，会导致切页时漏掉一次淡出。
        /// </summary>
        private void SyncBackdropVisibility(bool visible)
        {
            if (_isBackdropVisible == visible)
            {
                return;
            }

            _isBackdropVisible = visible;

            var animation = new DoubleAnimation
            {
                To = visible ? 1 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(BackdropFadeMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, BackdropLayer);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
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
