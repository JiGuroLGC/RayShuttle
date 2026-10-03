using System;
using System.Collections.Generic;
using System.Text;

namespace RayShuttle.Models
{
    /// <summary>
    /// 把节点推断成国家/地区代码（ISO 3166-1 alpha-2，小写），用于在列表里显示对应旗帜。
    ///
    /// **节点本身没有国家字段**：只有 JSON 节点文件那条链路会填 <c>Country</c>；
    /// 供应商 / 分享链接链路走的是 clash YAML → <c>ShareLinkParser</c>，只带一个供应商给的
    /// 自由文本名字（形如「🇭🇰 香港 01」「HK-01」「香港01」）。所以这里按优先级依次尝试：
    ///   1. 名字里的**国旗 emoji**（区域指示符对）——最可靠，很多供应商会写；
    ///   2. <c>Country</c> 字段（JSON 链路才有）；
    ///   3. 名字 / 分组里的英文名、中文名、城市名、两字母代码（下面的别名表）。
    /// 都认不出来就返回空串，界面回退到品牌光点。
    ///
    /// 匹配规则：**拉丁别名按「词」匹配**（否则 us 会命中 russia / australia），
    /// 带空格的多词别名与中日韩别名按子串匹配。
    /// 新增地区只改这一张表；对应旗帜图见 <c>Assets/flags/</c>（tools/fetch_flags.py）。
    /// </summary>
    internal static class RegionCatalog
    {
        /// <summary>认不出来时的返回值。</summary>
        public const string Unknown = "";

        private static readonly (string Code, string[] Aliases)[] Table =
        {
            // ---- 东亚 ----
            ("cn", new[] { "china", "cn", "中国", "中國", "北京", "上海", "广州", "深圳", "杭州", "成都", "南京", "武汉" }),
            ("hk", new[] { "hong kong", "hongkong", "hk", "香港", "港" }),
            ("mo", new[] { "macao", "macau", "mo", "澳门", "澳門" }),
            ("tw", new[] { "taiwan", "tw", "台湾", "台灣", "台北", "taipei", "新北", "彰化", "高雄", "台中" }),
            ("jp", new[] { "japan", "jp", "日本", "东京", "東京", "tokyo", "大阪", "osaka", "埼玉", "名古屋", "横滨" }),
            ("kr", new[] { "korea", "kr", "韩国", "韓國", "首尔", "首爾", "seoul", "釜山" }),
            ("mn", new[] { "mongolia", "mn", "蒙古" }),

            // ---- 东南亚 ----
            ("sg", new[] { "singapore", "sg", "新加坡", "狮城" }),
            ("my", new[] { "malaysia", "my", "马来西亚", "馬來西亞", "吉隆坡" }),
            ("th", new[] { "thailand", "th", "泰国", "泰國", "曼谷", "bangkok" }),
            ("vn", new[] { "vietnam", "vn", "越南", "河内", "胡志明" }),
            ("ph", new[] { "philippines", "ph", "菲律宾", "菲律賓", "马尼拉" }),
            ("id", new[] { "indonesia", "id", "印尼", "印度尼西亚", "雅加达" }),
            ("kh", new[] { "cambodia", "kh", "柬埔寨" }),
            ("mm", new[] { "myanmar", "mm", "缅甸", "緬甸" }),
            ("la", new[] { "laos", "la", "老挝" }),
            ("bn", new[] { "brunei", "bn", "文莱" }),

            // ---- 北美 / 南美 ----
            ("us", new[] { "united states", "usa", "us", "美国", "美國", "洛杉矶", "los angeles", "圣何塞", "san jose",
                           "西雅图", "seattle", "纽约", "new york", "达拉斯", "dallas", "芝加哥", "chicago",
                           "凤凰城", "硅谷", "silicon", "拉斯维加斯", "迈阿密", "波特兰", "亚特兰大" }),
            ("ca", new[] { "canada", "ca", "加拿大", "多伦多", "toronto", "温哥华", "vancouver", "蒙特利尔" }),
            ("mx", new[] { "mexico", "mx", "墨西哥" }),
            ("br", new[] { "brazil", "br", "巴西", "圣保罗" }),
            ("ar", new[] { "argentina", "ar", "阿根廷" }),
            ("cl", new[] { "chile", "cl", "智利" }),
            ("pe", new[] { "peru", "pe", "秘鲁" }),
            ("co", new[] { "colombia", "co", "哥伦比亚" }),

            // ---- 欧洲 ----
            ("gb", new[] { "united kingdom", "england", "britain", "uk", "gb", "英国", "英國", "伦敦", "london", "曼彻斯特" }),
            ("ie", new[] { "ireland", "ie", "爱尔兰", "都柏林" }),
            ("fr", new[] { "france", "fr", "法国", "法國", "巴黎", "paris", "马赛" }),
            ("de", new[] { "germany", "de", "德国", "德國", "法兰克福", "frankfurt", "柏林", "berlin" }),
            ("nl", new[] { "netherlands", "holland", "nl", "荷兰", "荷蘭", "阿姆斯特丹", "amsterdam" }),
            ("be", new[] { "belgium", "be", "比利时" }),
            ("lu", new[] { "luxembourg", "lu", "卢森堡" }),
            ("ch", new[] { "switzerland", "ch", "瑞士", "苏黎世", "zurich", "日内瓦" }),
            ("at", new[] { "austria", "at", "奥地利", "维也纳" }),
            ("it", new[] { "italy", "it", "意大利", "米兰", "milan", "罗马" }),
            ("es", new[] { "spain", "es", "西班牙", "马德里", "巴塞罗那" }),
            ("pt", new[] { "portugal", "pt", "葡萄牙", "里斯本" }),
            ("se", new[] { "sweden", "se", "瑞典", "斯德哥尔摩" }),
            ("no", new[] { "norway", "no", "挪威", "奥斯陆" }),
            ("dk", new[] { "denmark", "dk", "丹麦", "哥本哈根" }),
            ("fi", new[] { "finland", "fi", "芬兰", "赫尔辛基" }),
            ("is", new[] { "iceland", "is", "冰岛" }),
            ("pl", new[] { "poland", "pl", "波兰", "华沙" }),
            ("cz", new[] { "czech", "cz", "捷克", "布拉格" }),
            ("sk", new[] { "slovakia", "sk", "斯洛伐克" }),
            ("hu", new[] { "hungary", "hu", "匈牙利", "布达佩斯" }),
            ("ro", new[] { "romania", "ro", "罗马尼亚" }),
            ("bg", new[] { "bulgaria", "bg", "保加利亚" }),
            ("gr", new[] { "greece", "gr", "希腊", "雅典" }),
            ("hr", new[] { "croatia", "hr", "克罗地亚" }),
            ("si", new[] { "slovenia", "si", "斯洛文尼亚" }),
            ("rs", new[] { "serbia", "rs", "塞尔维亚" }),
            ("ua", new[] { "ukraine", "ua", "乌克兰", "烏克蘭", "基辅" }),
            ("ru", new[] { "russia", "ru", "俄罗斯", "俄羅斯", "莫斯科", "moscow", "圣彼得堡" }),
            ("by", new[] { "belarus", "by", "白俄罗斯" }),
            ("lt", new[] { "lithuania", "lt", "立陶宛" }),
            ("lv", new[] { "latvia", "lv", "拉脱维亚" }),
            ("ee", new[] { "estonia", "ee", "爱沙尼亚" }),
            ("md", new[] { "moldova", "md", "摩尔多瓦" }),

            // ---- 中东 / 非洲 ----
            ("tr", new[] { "turkey", "turkiye", "tr", "土耳其", "伊斯坦布尔" }),
            ("il", new[] { "israel", "il", "以色列", "特拉维夫" }),
            ("ae", new[] { "united arab emirates", "emirates", "dubai", "ae", "阿联酋", "迪拜", "杜拜" }),
            ("sa", new[] { "saudi", "sa", "沙特" }),
            ("qa", new[] { "qatar", "qa", "卡塔尔" }),
            ("kw", new[] { "kuwait", "kw", "科威特" }),
            ("bh", new[] { "bahrain", "bh", "巴林" }),
            ("om", new[] { "oman", "om", "阿曼" }),
            ("eg", new[] { "egypt", "eg", "埃及" }),
            ("za", new[] { "south africa", "za", "南非", "约翰内斯堡" }),
            ("ng", new[] { "nigeria", "ng", "尼日利亚" }),
            ("ke", new[] { "kenya", "ke", "肯尼亚" }),

            // ---- 南亚 / 中亚 / 高加索 ----
            ("in", new[] { "india", "in", "印度", "孟买", "mumbai" }),
            ("pk", new[] { "pakistan", "pk", "巴基斯坦" }),
            ("bd", new[] { "bangladesh", "bd", "孟加拉" }),
            ("lk", new[] { "sri lanka", "lk", "斯里兰卡" }),
            ("np", new[] { "nepal", "np", "尼泊尔" }),
            ("kz", new[] { "kazakhstan", "kz", "哈萨克斯坦" }),
            ("ge", new[] { "georgia", "ge", "格鲁吉亚" }),
            ("am", new[] { "armenia", "am", "亚美尼亚" }),
            ("az", new[] { "azerbaijan", "az", "阿塞拜疆" }),

            // ---- 大洋洲 ----
            ("au", new[] { "australia", "au", "澳大利亚", "澳洲", "悉尼", "sydney", "墨尔本" }),
            ("nz", new[] { "new zealand", "nz", "新西兰", "奧克蘭", "奥克兰", "auckland" })
        };

        /// <summary>
        /// 推断节点所属地区。参数都允许为空；认不出来返回 <see cref="Unknown"/>（空串）。
        /// </summary>
        public static string Resolve(string? name, string? country = null, string? group = null)
        {
            var fromEmoji = FromFlagEmoji(name);
            if (fromEmoji.Length > 0)
            {
                return fromEmoji;
            }

            var fromCountry = Match(country);
            if (fromCountry.Length > 0)
            {
                return fromCountry;
            }

            var fromName = Match(name);
            return fromName.Length > 0 ? fromName : Match(group);
        }

        /// <summary>解码名字里的国旗 emoji（两个区域指示符合成一个 ISO 代码）。</summary>
        private static string FromFlagEmoji(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Unknown;
            }

            for (var i = 0; i + 3 < text!.Length; i++)
            {
                if (!char.IsHighSurrogate(text[i]) || !char.IsLowSurrogate(text[i + 1])
                    || !char.IsHighSurrogate(text[i + 2]) || !char.IsLowSurrogate(text[i + 3]))
                {
                    continue;
                }

                var first = char.ConvertToUtf32(text[i], text[i + 1]);
                var second = char.ConvertToUtf32(text[i + 2], text[i + 3]);

                if (IsRegionalIndicator(first) && IsRegionalIndicator(second))
                {
                    return new string(new[]
                    {
                        (char)('a' + first - 0x1F1E6),
                        (char)('a' + second - 0x1F1E6)
                    });
                }
            }

            return Unknown;
        }

        private static bool IsRegionalIndicator(int codePoint) =>
            codePoint is >= 0x1F1E6 and <= 0x1F1FF;

        private static string Match(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Unknown;
            }

            var lower = text!.ToLowerInvariant();
            var tokens = Tokenize(lower);

            foreach (var (code, aliases) in Table)
            {
                foreach (var alias in aliases)
                {
                    // 带空格的多词别名与中日韩别名走子串；单个拉丁词走分词，避免 us 命中 russia。
                    var matched = IsLatin(alias) && alias.IndexOf(' ') < 0
                        ? tokens.Contains(alias)
                        : lower.Contains(alias, StringComparison.Ordinal);

                    if (matched)
                    {
                        return code;
                    }
                }
            }

            return Unknown;
        }

        /// <summary>按非字母数字切词，供拉丁别名做整词匹配。</summary>
        private static HashSet<string> Tokenize(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var current = new StringBuilder();

            foreach (var ch in text)
            {
                if (char.IsAsciiLetterOrDigit(ch))
                {
                    current.Append(ch);
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }

        private static bool IsLatin(string text)
        {
            foreach (var ch in text)
            {
                if (ch > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
