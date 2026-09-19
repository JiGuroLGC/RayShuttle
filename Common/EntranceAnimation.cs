using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace RayShuttle.Common
{
    /// <summary>
    /// 页面入场动画：让一组元素依次淡入并自下而上轻微浮起，形成错落的节奏感。
    /// </summary>
    public static class EntranceAnimation
    {
        private const double DefaultOffset = 18;
        private const int DefaultStepMilliseconds = 55;
        private const int DefaultDurationMilliseconds = 420;

        public static void Run(params FrameworkElement[] elements)
        {
            Run(DefaultOffset, DefaultStepMilliseconds, DefaultDurationMilliseconds, elements);
        }

        public static void Run(double offset, int stepMilliseconds, int durationMilliseconds, params FrameworkElement[] elements)
        {
            if (elements is null)
            {
                return;
            }

            for (var index = 0; index < elements.Length; index++)
            {
                var element = elements[index];
                if (element is null)
                {
                    continue;
                }

                AnimateOne(element, TimeSpan.FromMilliseconds(index * (double)stepMilliseconds), offset, durationMilliseconds);
            }
        }

        private static void AnimateOne(FrameworkElement element, TimeSpan beginTime, double offset, int durationMilliseconds)
        {
            element.Opacity = 0;

            var translate = new TranslateTransform { Y = offset };
            element.RenderTransform = translate;

            var duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds));
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration,
                BeginTime = beginTime,
                EasingFunction = easing
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

            var slide = new DoubleAnimation
            {
                From = offset,
                To = 0,
                Duration = duration,
                BeginTime = beginTime,
                EasingFunction = easing
            };
            Storyboard.SetTarget(slide, translate);
            Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.Y));

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(slide);
            storyboard.Begin();
        }
    }
}
