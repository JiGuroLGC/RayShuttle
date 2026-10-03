using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using RayShuttle.Common;
using Windows.Foundation;

namespace RayShuttle.Views
{
    /// <summary>
    /// 首次运行的第 0 步：欢迎页。整体元素与开屏遮罩一致（光球 + 光梭 + 标语），
    /// 只把底部的加载圈换成「立即使用」按钮；点击后进入用户协议页。
    ///
    /// 它**不是**开屏遮罩：开屏遮罩负责「进入主界面」的品牌加载（正常启动 / 登录成功后），
    /// 由 <see cref="MainWindow"/> 管理。本页只做首启的引导入口，不参与任何状态判定——
    /// 此时用户尚未同意协议，<c>RefreshOnboardingStepAsync</c> 仍会路由回本页。
    /// </summary>
    public sealed partial class WelcomePage : Page
    {
        private Storyboard? _pulse;

        public WelcomePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EntranceAnimation.Run(ContentBlock);
            StartPulse();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 光环保是 Forever 动画，离开本页要主动停掉，避免它在后台空转。
            _pulse?.Stop();
            _pulse = null;
        }

        /// <summary>光球呼吸光环：放大并淡出，循环往复，与开屏遮罩一致。</summary>
        private void StartPulse()
        {
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

            _pulse = new Storyboard { Children = { scaleX, scaleY, opacity } };
            _pulse.Begin();
        }

        private void OnStartClicked(object sender, RoutedEventArgs e)
        {
            // 直接用本页所在 Frame 跳转即可：协议页同意后会经 RefreshOnboardingStepAsync
            // 继续推进，不需要在这里通知主窗口。
            Frame.Navigate(typeof(PrivacyPolicyPage), null, new SuppressNavigationTransitionInfo());
        }
    }
}
