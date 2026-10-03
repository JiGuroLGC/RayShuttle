using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace RayShuttle.Controls
{
    /// <summary>
    /// 零依赖的迷你曲线控件：把一组数值画成一条折线（可选面积填充）。
    ///
    /// 项目刻意不引入任何图表库（Win2D / CommunityToolkit 都没引用），所以这里只用
    /// <see cref="Canvas"/> + <see cref="Polyline"/> 自己画。用于统计页的速率 / 延迟走势。
    ///
    /// 用法：设置 <see cref="Points"/>（一组样本，X 等距）、<see cref="Stroke"/> 与可选的
    /// <see cref="Fill"/>（面积填充）。Y 轴以样本最大值为上限、从 0 起算，自动按控件尺寸归一化。
    /// </summary>
    public sealed class Sparkline : Canvas
    {
        /// <summary>曲线数据点（按出现顺序，X 等距排列）。</summary>
        public static readonly DependencyProperty PointsProperty =
            DependencyProperty.Register(
                nameof(Points),
                typeof(IEnumerable<double>),
                typeof(Sparkline),
                new PropertyMetadata(null, OnPointsChanged));

        /// <summary>折线颜色。</summary>
        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(
                nameof(Stroke),
                typeof(Brush),
                typeof(Sparkline),
                new PropertyMetadata(null, OnVisualChanged));

        /// <summary>折线宽度。</summary>
        public static readonly DependencyProperty StrokeWidthProperty =
            DependencyProperty.Register(
                nameof(StrokeWidth),
                typeof(double),
                typeof(Sparkline),
                new PropertyMetadata(2d, OnVisualChanged));

        /// <summary>面积填充颜色（可选；为 null 则不填充）。</summary>
        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register(
                nameof(Fill),
                typeof(Brush),
                typeof(Sparkline),
                new PropertyMetadata(null, OnVisualChanged));

        public IEnumerable<double> Points
        {
            get => (IEnumerable<double>)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        public Brush? Stroke
        {
            get => (Brush?)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double StrokeWidth
        {
            get => (double)GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public Brush? Fill
        {
            get => (Brush?)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public Sparkline()
        {
            SizeChanged += (_, _) => Redraw();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // 默认 Canvas 会按子元素尺寸收缩，导致没有子元素时宽度塌成 0、什么也画不出来。
            // 这里按分配到的区域铺满，确保 ActualWidth / ActualHeight 反映真实可用尺寸。
            base.ArrangeOverride(finalSize);
            return finalSize;
        }

        private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((Sparkline)d).Redraw();

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((Sparkline)d).Redraw();

        private void Redraw()
        {
            Children.Clear();

            var source = Points?.ToList();
            if (source is null || source.Count == 0)
            {
                return;
            }

            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var max = source.Max();
            if (max <= 0)
            {
                max = 1;
            }

            var pad = StrokeWidth;
            var usableHeight = Math.Max(1, height - pad * 2);

            var points = new PointCollection();
            for (var i = 0; i < source.Count; i++)
            {
                var x = source.Count == 1 ? 0 : (i / (double)(source.Count - 1)) * width;
                var y = pad + usableHeight * (1 - source[i] / max);
                points.Add(new Point(x, y));
            }

            // 面积填充画在折线之下。
            if (Fill is not null && points.Count > 1)
            {
                var area = new Polygon { Fill = Fill };
                foreach (var point in points)
                {
                    area.Points.Add(point);
                }

                area.Points.Add(new Point(width, height));
                area.Points.Add(new Point(0, height));
                Children.Add(area);
            }

            var line = new Polyline
            {
                Stroke = Stroke,
                StrokeThickness = StrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = points
            };
            Children.Add(line);
        }
    }
}
