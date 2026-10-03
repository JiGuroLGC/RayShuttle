using System;
using System.IO;

namespace RayShuttle.Services.Routing
{
    /// <summary>
    /// 探测 <c>Core/</c> 目录下 geoip.dat 与 geosite.dat 是否齐全。
    ///
    /// 这两份数据来自 Loyalsoldier/v2ray-rules-dat（GPL-3.0），本仓库不打包——
    /// 需要完整「绕过大陆」覆盖时，用 <c>tools/fetch_geo.py</c> 下载到 <c>Core/</c> 即可。
    /// 只要缺一份就不启用 geo 规则：否则 Xray 会因找不到 geoip:cn / geosite:cn 直接启动失败。
    /// </summary>
    internal static class GeoAssets
    {
        public static bool AreAvailable
        {
            get
            {
                if (_checked && _available.HasValue)
                {
                    return _available.Value;
                }

                var coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
                var ok = File.Exists(Path.Combine(coreDir, "geoip.dat"))
                    && File.Exists(Path.Combine(coreDir, "geosite.dat"));

                _available = ok;
                _checked = true;
                return ok;
            }
        }

        private static bool _checked;
        private static bool? _available;
    }
}
