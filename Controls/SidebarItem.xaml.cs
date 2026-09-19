using System;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RayShuttle.Common;

namespace RayShuttle.Controls
{
    /// <summary>悬浮侧栏里的一个导航项。</summary>
    public sealed partial class SidebarItem : UserControl
    {
        public SidebarItem()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        /// <summary>用户点击该项时触发。</summary>
        public event EventHandler? Clicked;

        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph),
            typeof(string),
            typeof(SidebarItem),
            new PropertyMetadata(string.Empty, OnGlyphChanged));

        /// <summary>图标字形（Segoe Fluent Icons）。</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(SidebarItem),
            new PropertyMetadata(string.Empty, OnLabelChanged));

        /// <summary>显示在图标下方的文字。</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(SidebarItem),
            new PropertyMetadata(false, OnIsSelectedChanged));

        /// <summary>是否为当前页面对应的项。</summary>
        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnGlyphChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            ((SidebarItem)sender).ItemIcon.Glyph = (string)args.NewValue;
        }

        private static void OnLabelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            ((SidebarItem)sender).ItemLabel.Text = (string)args.NewValue;
        }

        private static void OnIsSelectedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            ((SidebarItem)sender).ApplySelection((bool)args.NewValue);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            ApplySelection(IsSelected);
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e) => Clicked?.Invoke(this, EventArgs.Empty);

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e) =>
            Animate(HoverFill, nameof(UIElement.Opacity), 1, 160);

        private void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
            Animate(HoverFill, nameof(UIElement.Opacity), 0, 220);

        private void ApplySelection(bool isSelected)
        {
            var brush = ThemeResources.GetBrush(isSelected ? "BrandCyanBrush" : "TextSecondaryBrush");

            ItemIcon.Foreground = brush;
            ItemLabel.Foreground = brush;
            ItemLabel.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;

            if (isSelected)
            {
                Pop();
            }
        }

        /// <summary>选中时轻微放大再回弹，给一点手感。</summary>
        private void Pop()
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var storyboard = new Storyboard();

            foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
            {
                var animation = new DoubleAnimation
                {
                    From = 1,
                    To = 1.08,
                    Duration = new Duration(TimeSpan.FromSeconds(0.14)),
                    AutoReverse = true,
                    EasingFunction = easing
                };
                Storyboard.SetTarget(animation, RootScale);
                Storyboard.SetTargetProperty(animation, property);
                storyboard.Children.Add(animation);
            }

            storyboard.Begin();
        }

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
