using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RayShuttle.Common
{
    /// <summary>按资源键读取 Themes 中定义的资源。</summary>
    public static class ThemeResources
    {
        /// <summary>读取画笔，键不存在时返回 null。</summary>
        public static Brush? GetBrush(string key) => Get<Brush>(key);

        /// <summary>
        /// 读取任意引用类型资源。除了查顶层资源字典，还会递归查找合并字典，
        /// 这样 Brand.xaml / Styles.xaml 中的资源在任何情况下都能取到。
        /// </summary>
        public static T? Get<T>(string key) where T : class
        {
            var resources = Application.Current?.Resources;
            return resources is null ? null : Lookup<T>(resources, key);
        }

        private static T? Lookup<T>(ResourceDictionary dictionary, string key) where T : class
        {
            if (dictionary.TryGetValue(key, out var value) && value is T typed)
            {
                return typed;
            }

            foreach (var merged in dictionary.MergedDictionaries)
            {
                var found = Lookup<T>(merged, key);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
