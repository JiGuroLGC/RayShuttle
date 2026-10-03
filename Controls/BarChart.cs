using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace RayShuttle.Controls
{
    /// <summary>
    /// 零依赖的迷你柱状图：用 <see cref="Canvas"/> 画一组纵向柱子。
    ///
    /// 设计取舍与 <see cref="Sparkline"/> 一致——不引入任何图表库（项目一贯的「零外部原生依赖」
    /// 原则），纯靠 <see cref="Canvas"/> + <see cref="Rectangle"/> 自绘。
    ///
    /// 支持两组数据堆叠（主系列在下、次系列在上），用于同时呈现「下载 / 上传」：
    /// <see cref="PrimaryValues"/> 与 <see cref="SecondaryValues"/> 一一对应，
    /// 某一格缺次系列时只画主系列。柱高按全部格子的峰值归一。
    /// </summary>
    public sealed class BarChart : Canvas
    {
        public static readonly DependencyProperty PrimaryValuesProperty =
            DependencyProperty.Register(
                nameof(PrimaryValues),
                typeof(List<double>),
                typeof(BarChart),
                new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty SecondaryValuesProperty =
            DependencyProperty.Register(
                nameof(SecondaryValues),
                typeof(List<double>),
                typeof(BarChart),
                new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty PrimaryFillProperty =
            DependencyProperty.Register(
                nameof(PrimaryFill),
                typeof(Brush),
                typeof(BarChart),
                new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty SecondaryFillProperty =
            DependencyProperty.Register(
                nameof(SecondaryFill),
                typeof(Brush),
                typeof(BarChart),
                new PropertyMetadata(null, OnChanged));

        /// <summary>柱子之间的间隙占「单格宽度」的比例（0~0.9），其余是柱宽。</summary>
        public static readonly DependencyProperty GapRatioProperty =
            DependencyProperty.Register(
                nameof(GapRatio),
                typeof(double),
                typeof(BarChart),
                new PropertyMetadata(0.32, OnChanged));

        public List<double>? PrimaryValues
        {
            get => (List<double>?)GetValue(PrimaryValuesProperty);
            set => SetValue(PrimaryValuesProperty, value);
        }

        public List<double>? SecondaryValues
        {
            get => (List<double>?)GetValue(SecondaryValuesProperty);
            set => SetValue(SecondaryValuesProperty, value);
        }

        public Brush? PrimaryFill
        {
            get => (Brush?)GetValue(PrimaryFillProperty);
            set => SetValue(PrimaryFillProperty, value);
        }

        public Brush? SecondaryFill
        {
            get => (Brush?)GetValue(SecondaryFillProperty);
            set => SetValue(SecondaryFillProperty, value);
        }

        public double GapRatio
        {
            get => (double)GetValue(GapRatioProperty);
            set => SetValue(GapRatioProperty, value);
        }

        public BarChart()
        {
            SizeChanged += (_, _) => Redraw();
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((BarChart)d).Redraw();

        private void Redraw()
        {
            Children.Clear();

            var primary = PrimaryValues;
            if (primary is null || primary.Count == 0)
            {
                return;
            }

            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var secondary = SecondaryValues;
            var n = primary.Count;

            var max = primary.Max();
            if (secondary is not null)
            {
                for (var i = 0; i < n; i++)
                {
                    if (i < secondary.Count)
                    {
                        max = Math.Max(max, primary[i] + secondary[i]);
                    }
                }
            }

            if (max <= 0)
            {
                return;
            }

            var slot = width / n;
            var gap = slot * Math.Clamp(GapRatio, 0, 0.9);
            var barWidth = Math.Max(1.0, slot - gap);
            var offset = gap / 2.0;
            var usableHeight = height - 2;

            var primaryBrush = PrimaryFill ?? new SolidColorBrush(Colors.Cyan);
            var secondaryBrush = SecondaryFill ?? new SolidColorBrush(Colors.Violet);
            var radius = Math.Min(3.0, barWidth / 2.0);

            for (var i = 0; i < n; i++)
            {
                var x = i * slot + offset;
                var p = primary[i];
                var pHeight = p / max * usableHeight;

                // WinUI 的 Canvas 只支持 SetLeft / SetTop（无 SetBottom），
                // 所以柱子的「从底部往上长」要用 top = 画布高 − 柱高 来表示。
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = Math.Max(0, pHeight),
                    Fill = primaryBrush,
                    RadiusX = radius,
                    RadiusY = radius
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, height - pHeight);
                Children.Add(rect);

                if (secondary is not null && i < secondary.Count)
                {
                    var s = secondary[i];
                    var sHeight = s / max * usableHeight;

                    var rect2 = new Rectangle
                    {
                        Width = barWidth,
                        Height = Math.Max(0, sHeight),
                        Fill = secondaryBrush,
                        RadiusX = radius,
                        RadiusY = radius
                    };
                    Canvas.SetLeft(rect2, x);
                    Canvas.SetTop(rect2, height - pHeight - sHeight);
                    Children.Add(rect2);
                }
            }
        }
    }
}
