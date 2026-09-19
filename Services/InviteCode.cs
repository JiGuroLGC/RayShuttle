using System.Text;

namespace RayShuttle.Services
{
    /// <summary>
    /// 邀请码的规范化、校验与展示。
    ///
    /// 格式：32 位数字或大写字母，中间无分隔符，例如 7K2M9QX4B1TZ8N5RV3WD6YH0PC。
    ///
    /// **本类不负责生成邀请码。** 邀请码由发放方（服务端 / tools/node_key.py）生成，
    /// 客户端只做校验——客户端若能自行造码，整套准入就失去意义了。
    ///
    /// 目前只做本地格式校验；接入服务端后把 <see cref="IsWellFormed"/> 换成服务端结果即可，
    /// 登录页的调用方式不变。
    /// </summary>
    public static class InviteCode
    {
        /// <summary>邀请码长度。同时也是派生密钥时作为 password 的字符数。</summary>
        public const int RequiredLength = 32;

        /// <summary>去掉分隔符与空白、统一大写，便于比对、派生与展示。</summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(RequiredLength);
            foreach (var character in raw)
            {
                if (IsAllowed(character))
                {
                    // 统一大写：小写输入与规范形式必须派生出同一把密钥。
                    buffer.Append(char.ToUpperInvariant(character));
                }
            }

            return buffer.ToString();
        }

        public static bool IsWellFormed(string normalized)
        {
            if (normalized.Length != RequiredLength)
            {
                return false;
            }

            foreach (var character in normalized)
            {
                if (!IsAllowed(character))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>展示用掩码：按 4 位分组，只露出末 4 位。</summary>
        public static string Mask(string normalized)
        {
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            var tail = normalized.Length <= 4 ? normalized : normalized[^4..];
            var masked = new string('•', System.Math.Max(0, normalized.Length - tail.Length));

            var grouped = new StringBuilder();
            var combined = masked + tail;
            for (var index = 0; index < combined.Length; index += 4)
            {
                if (index > 0)
                {
                    grouped.Append('-');
                }

                grouped.Append(combined, index, System.Math.Min(4, combined.Length - index));
            }

            return grouped.ToString();
        }

        // 只认 ASCII 数字与大写字母：限定范围可避免中文、全角字符混进来凑出「看起来有效」的码。
        private static bool IsAllowed(char character) =>
            (character >= '0' && character <= '9')
            || (character >= 'A' && character <= 'Z')
            || (character >= 'a' && character <= 'z');
    }
}
