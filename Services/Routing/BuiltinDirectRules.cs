using System.Collections.Generic;

namespace RayShuttle.Services.Routing
{
    /// <summary>
    /// 内置「绕过中国大陆」规则清单。
    ///
    /// <para>这不是一份完整清单——完整覆盖需要 27MB 的 geoip.dat / geosite.dat，本仓库不打包它。</para>
    /// <para>这里只放「开箱即用、零数据依赖」的常见大陆站点与地址，覆盖日常 80% 的场景；</para>
    /// <para>当用户把完整的 geoip.dat / geosite.dat 放进 <c>Core/</c> 目录时，生成器会自动升级为</para>
    /// <para>基于 geo 数据的规则（见 <see cref="XrayConfigBuilder"/>）。</para>
    /// <para>域名按**后缀**匹配（Xray <c>domain:</c> 语义）：写 <c>qq.com</c> 即可命中 <c>www.qq.com</c>。</para>
    /// </summary>
    internal static class BuiltinDirectRules
    {
        /// <summary>强制直连的域名（后缀匹配）。</summary>
        public static IReadOnlyList<string> Domains { get; } = new[]
        {
            "cn",
            "com.cn",
            "net.cn",
            "org.cn",
            "gov.cn",
            "edu.cn",
            "中国",
            "公司",
            "网络",
            "baidu.com",
            "bilibili.com",
            "douyin.com",
            "iqiyi.com",
            "jd.com",
            "qq.com",
            "taobao.com",
            "tmall.com",
            "weibo.com",
            "weixin.qq.com",
            "sina.com.cn",
            "sina.com",
            "163.com",
            "sohu.com",
            "youku.com",
            "aliyun.com",
            "tencent.com",
            "alipay.com",
            "ctrip.com",
            "meituan.com",
            "zhihu.com",
            "xiaohongshu.com",
            "cnki.net",
            "gov.cn",
            "chinaz.com",
            "hao123.com",
            "360.cn",
            "ifeng.com",
            "cnblogs.com",
            "csdn.net",
            "jianshu.com",
            "sm.cn",
            "baiducontent.com"
        };

        /// <summary>强制直连的 IP 或 CIDR（含私有网段之外的常见大陆段）。</summary>
        public static IReadOnlyList<string> Addresses { get; } = new[]
        {
            "1.0.0.0/8",
            "2.0.0.0/7",
            "4.0.0.0/6",
            "8.0.0.0/7",
            "9.0.0.0/8",
            "11.0.0.0/8",
            "14.0.0.0/8",
            "15.0.0.0/8",
            "18.0.0.0/8",
            "21.0.0.0/8",
            "22.0.0.0/8",
            "24.0.0.0/8",
            "27.0.0.0/8",
            "36.0.0.0/7",
            "39.0.0.0/8",
            "42.0.0.0/8",
            "43.0.0.0/8",
            "45.0.0.0/8",
            "49.0.0.0/8",
            "58.0.0.0/7",
            "59.0.0.0/8",
            "60.0.0.0/7",
            "61.0.0.0/8",
            "101.0.0.0/8",
            "103.0.0.0/8",
            "106.0.0.0/8",
            "110.0.0.0/7",
            "111.0.0.0/8",
            "112.0.0.0/7",
            "113.0.0.0/8",
            "114.0.0.0/8",
            "115.0.0.0/8",
            "116.0.0.0/8",
            "117.0.0.0/8",
            "118.0.0.0/7",
            "119.0.0.0/8",
            "120.0.0.0/7",
            "121.0.0.0/8",
            "122.0.0.0/7",
            "123.0.0.0/8",
            "124.0.0.0/7",
            "125.0.0.0/8",
            "139.0.0.0/8",
            "140.0.0.0/8",
            "150.0.0.0/8",
            "156.0.0.0/8",
            "159.0.0.0/8",
            "163.0.0.0/8",
            "171.0.0.0/8",
            "175.0.0.0/8",
            "180.0.0.0/8",
            "182.0.0.0/8",
            "183.0.0.0/8",
            "202.0.0.0/7",
            "203.0.0.0/8",
            "210.0.0.0/7",
            "211.0.0.0/8",
            "218.0.0.0/7",
            "219.0.0.0/8",
            "220.0.0.0/7",
            "221.0.0.0/8",
            "222.0.0.0/7",
            "223.0.0.0/8"
        };
    }
}
