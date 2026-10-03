using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RayShuttle.Common;
using RayShuttle.Models;

namespace RayShuttle.Controls
{
    /// <summary>
    /// 可复用的连接光球控件。三种状态由 <see cref="State"/> 驱动，
    /// 点击时抛出 <see cref="Clicked"/>，由页面决定具体行为。
    /// </summary>
    public sealed partial class ConnectOrb : UserControl
    {
        private const double HoverScale = 1.03;

        private Storyboard? _spin;
        private Storyboard? _pulseFast;
        private Storyboard? _pulseSlow;
        private Storyboard? _breathe;
        private bool _isLoaded;

        /// <summary>用户点击光球时触发。</summary>
        public event EventHandler? Clicked;

        public ConnectOrb()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State),
            typeof(OrbState),
            typeof(ConnectOrb),
            new PropertyMetadata(OrbState.Disconnected, OnStatePropertyChanged));

        /// <summary>光球当前状态，决定配色与动画。</summary>
        public OrbState State
        {
            get => (OrbState)GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        private static void OnStatePropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            ((ConnectOrb)sender).ApplyState((OrbState)args.NewValue);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureStoryboards();
            _isLoaded = true;
            ApplyState(State);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            _spin?.Stop();
            _pulseFast?.Stop();
            _pulseSlow?.Stop();
            _breathe?.Stop();
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            Animate(RootScale, nameof(ScaleTransform.ScaleX), HoverScale, 200);
            Animate(RootScale, nameof(ScaleTransform.ScaleY), HoverScale, 200);
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            Animate(RootScale, nameof(ScaleTransform.ScaleX), 1, 260);
            Animate(RootScale, nameof(ScaleTransform.ScaleY), 1, 260);
        }

        private void ApplyState(OrbState state)
        {
            ApplyVisuals(state);

            if (_isLoaded)
            {
                ApplyAnimations(state);
            }
        }

        private void ApplyVisuals(OrbState state)
        {
            switch (state)
            {
                case OrbState.Connecting:
                    Glow.Fill = ThemeResources.GetBrush("OrbActiveGlowBrush");
                    Core.Background = ThemeResources.GetBrush("OrbActiveCoreBrush");
                    Core.BorderBrush = ThemeResources.GetBrush("OrbActiveRingBrush");
                    CoreIcon.Foreground = ThemeResources.GetBrush("BrandCyanBrush");
                    CoreLabel.Foreground = ThemeResources.GetBrush("BrandCyanBrush");
                    CoreLabel.Text = "连接中";
                    break;

                case OrbState.Connected:
                    Glow.Fill = ThemeResources.GetBrush("OrbConnectedGlowBrush");
                    Core.Background = ThemeResources.GetBrush("OrbConnectedCoreBrush");
                    Core.BorderBrush = ThemeResources.GetBrush("OrbActiveRingBrush");
                    CoreIcon.Foreground = ThemeResources.GetBrush("BrandCyanBrush");
                    CoreLabel.Foreground = ThemeResources.GetBrush("BrandCyanBrush");
                    // 中心文字表示「可执行的动作」而非状态——状态已由页面顶部胶囊展示，避免重复。
                    CoreLabel.Text = "断开";
                    break;

                default:
                    Glow.Fill = ThemeResources.GetBrush("OrbIdleGlowBrush");
                    Core.Background = ThemeResources.GetBrush("OrbIdleCoreBrush");
                    Core.BorderBrush = ThemeResources.GetBrush("OrbIdleStrokeBrush");
                    CoreIcon.Foreground = ThemeResources.GetBrush("TextSecondaryBrush");
                    CoreLabel.Foreground = ThemeResources.GetBrush("TextSecondaryBrush");
                    CoreLabel.Text = "连接";
                    break;
            }
        }

        private void ApplyAnimations(OrbState state)
        {
            _spin?.Stop();
            _pulseFast?.Stop();
            _pulseSlow?.Stop();
            _breathe?.Stop();

            // 停止后先回到静止态，再按状态淡入，避免切换时的突兀跳变。
            RingOuter.Opacity = 0;
            RingTicks.Opacity = 0;
            Pulse1.Opacity = 0;
            Pulse2.Opacity = 0;

            if (state == OrbState.Disconnected)
            {
                return;
            }

            Animate(RingOuter, nameof(UIElement.Opacity), 1, 420);
            Animate(RingTicks, nameof(UIElement.Opacity), 0.85, 520);
            _spin?.Begin();
            _breathe?.Begin();

            if (state == OrbState.Connecting)
            {
                _pulseFast?.Begin();
            }
            else
            {
                _pulseSlow?.Begin();
            }
        }

        private void EnsureStoryboards()
        {
            if (_spin is not null)
            {
                return;
            }

            _spin = new Storyboard();
            _spin.Children.Add(RotationAnimation(RingOuterRotation, 360, 16));
            _spin.Children.Add(RotationAnimation(RingTicksRotation, -360, 28));

            _pulseFast = BuildPulse(1.5);
            _pulseSlow = BuildPulse(2.8);

            _breathe = new Storyboard();
            _breathe.Children.Add(Breathe(nameof(ScaleTransform.ScaleX)));
            _breathe.Children.Add(Breathe(nameof(ScaleTransform.ScaleY)));
        }

        private static DoubleAnimation RotationAnimation(DependencyObject target, double to, double seconds)
        {
            var animation = new DoubleAnimation
            {
                From = 0,
                To = to,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, nameof(RotateTransform.Angle));
            return animation;
        }

        private DoubleAnimation Breathe(string property)
        {
            var animation = new DoubleAnimation
            {
                From = 1,
                To = 1.035,
                Duration = new Duration(TimeSpan.FromSeconds(2.2)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, CoreScale);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }

        /// <summary>构建一组脉冲扩散环：两个环错开半个周期，形成连续向外扩散的效果。</summary>
        private Storyboard BuildPulse(double seconds)
        {
            var half = TimeSpan.FromSeconds(seconds / 2);
            var storyboard = new Storyboard();

            storyboard.Children.Add(PulseScale(PulseScale1, nameof(ScaleTransform.ScaleX), seconds, TimeSpan.Zero));
            storyboard.Children.Add(PulseScale(PulseScale1, nameof(ScaleTransform.ScaleY), seconds, TimeSpan.Zero));
            storyboard.Children.Add(PulseFade(Pulse1, seconds, TimeSpan.Zero));

            storyboard.Children.Add(PulseScale(PulseScale2, nameof(ScaleTransform.ScaleX), seconds, half));
            storyboard.Children.Add(PulseScale(PulseScale2, nameof(ScaleTransform.ScaleY), seconds, half));
            storyboard.Children.Add(PulseFade(Pulse2, seconds, half));

            return storyboard;
        }

        private static DoubleAnimation PulseScale(DependencyObject target, string property, double seconds, TimeSpan beginTime)
        {
            var animation = new DoubleAnimation
            {
                From = 0.62,
                To = 1.22,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                BeginTime = beginTime,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }

        private static DoubleAnimation PulseFade(DependencyObject target, double seconds, TimeSpan beginTime)
        {
            var animation = new DoubleAnimation
            {
                From = 0.7,
                To = 0,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                BeginTime = beginTime,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
            return animation;
        }

        /// <summary>对独立属性做一次性过渡动画（透明度、缩放等）。</summary>
        private static void Animate(DependencyObject target, string property, double to, int milliseconds)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
    }
}
