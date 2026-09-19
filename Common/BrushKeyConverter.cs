using System;
using Microsoft.UI.Xaml.Data;

namespace RayShuttle.Common
{
    /// <summary>
    /// 把 Themes/Brand.xaml 中的画笔键名转换成画笔实例。
    /// 用于让模型只暴露「配色语义」（例如 "StatusOkBrush"），而不直接依赖 UI 类型。
    /// </summary>
    public sealed class BrushKeyConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language) =>
            value is string key ? ThemeResources.GetBrush(key) : null;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
