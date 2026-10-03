using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RayShuttle.Common
{
    /// <summary>
    /// 地区代码 → 旗帜图片（`ms-appx:///Assets/flags/{code}.png`）。
    ///
    /// 认不出来、或该地区没有对应图片时返回 <c>null</c>：调用处把旗帜叠在品牌光点**之上**，
    /// 旗帜画不出来时光点自然露出来，就等于回退，不需要额外的可见性判断。
    ///
    /// 图片按地区码缓存：同一面旗在列表里会被反复请求，没必要每次 new 一个 BitmapImage。
    /// </summary>
    public sealed class RegionFlagConverter : IValueConverter
    {
        private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string code || string.IsNullOrEmpty(code))
            {
                return null;
            }

            if (Cache.TryGetValue(code, out var cached))
            {
                return cached;
            }

            ImageSource? source = null;
            try
            {
                source = new BitmapImage(new Uri($"ms-appx:///Assets/flags/{code}.png"));
            }
            catch (Exception)
            {
                // 地区码拼不出合法 URI：当作没有旗帜。
            }

            Cache[code] = source;
            return source;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}
