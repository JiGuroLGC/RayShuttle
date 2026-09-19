using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace RayShuttle.Common
{
    /// <summary>bool 到 Visibility 的转换，用于「推荐」标记这类可选元素的显隐。</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
