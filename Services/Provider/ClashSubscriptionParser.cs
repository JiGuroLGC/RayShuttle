using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RayShuttle.Services.Provider
{
    /// <summary>
    /// 序列化 vmess 分享链接里那张字典用的源生成上下文。
    ///
    /// **不能用反射式 <c>JsonSerializer.Serialize(payload)</c>**：Release 构建开了
    /// <c>PublishTrimmed</c>，反射元数据会被裁掉，运行时会落到 <c>EmptyJsonTypeInfoResolver</c>
    /// 并抛「JsonTypeInfo metadata for type 'System.String' was not provided」。
    /// 这里用源生成器显式登记字典类型，与项目里其它序列化上下文保持一致。
    /// </summary>
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal sealed partial class ClashJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// 解析供应商的订阅内容（实测是 **clash YAML**，不是 base64 分享链接）。
    ///
    /// 只解析 `proxies:` 这一段，其余（proxy-groups / rules / dns）一概不管：
    /// 路由规则由我们自己的 `XrayConfigBuilder` 生成，供应商那份是给 Clash 用的，
    /// 照搬反而会把我们的规则搞乱。
    ///
    /// 刻意**不引入 YAML 库**：需要的只是「两级缩进的 map 列表」，为此多一个依赖不划算。
    /// 解析逻辑照模拟器 `mini_yaml_load` + `node_from_clash` 来写，行为对齐即可。
    ///
    /// 产出的是**分享链接**，交给既有的 `ShareLinkParser` 解析成 `ProxyNode`——
    /// 这样 `NodeFactory` 的必填项校验、`XrayConfigBuilder` 的配置生成全都复用，
    /// 不需要为「供应商格式」再写一条解析链。
    /// </summary>
    internal static class ClashSubscriptionParser
    {
        /// <summary>
        /// 取出订阅里的所有代理，转成分享链接。转换不了的类型（hysteria2 / tuic / ssr 等）
        /// 返回空串，由调用方计入「跳过的条目」。
        /// </summary>
        public static List<string> ToShareLinks(string yaml)
        {
            var links = new List<string>();

            foreach (var proxy in ReadProxies(yaml))
            {
                var link = ToShareLink(proxy);
                if (link.Length > 0)
                {
                    links.Add(link);
                }
            }

            return links;
        }

        /// <summary>读取 `proxies:` 段。嵌套 map 摊平成 `ws-opts.path` 这种点号键。</summary>
        public static List<Dictionary<string, string>> ReadProxies(string yaml)
        {
            var result = new List<Dictionary<string, string>>();
            var lines = yaml.Replace("\r\n", "\n").Split('\n');

            var inProxies = false;
            Dictionary<string, string>? current = null;

            // 缩进 → (该层键名)。用来把嵌套 map 摊平成点号键（如 ws-opts.headers.Host）。
            var levels = new List<(int Indent, string Key)>();

            foreach (var raw in lines)
            {
                if (raw.TrimEnd().Length == 0)
                {
                    continue;
                }

                var indent = raw.Length - raw.TrimStart().Length;
                var line = raw.Trim();

                // YAML 的块序列项与它的父键**同缩进**（`proxies:` 下面就是 `- xxx`），
                // 所以「缩进回 0 就结束」这条规则必须排除列表项，否则一个节点都读不到。
                var isItemStart = line.StartsWith("- ", StringComparison.Ordinal) || line == "-";

                if (indent == 0 && !isItemStart)
                {
                    if (!inProxies)
                    {
                        if (line == "proxies:" || line == "proxies: ")
                        {
                            inProxies = true;
                        }

                        continue;
                    }

                    // 缩进回到 0 且不是列表项，说明 proxies 段结束了。
                    break;
                }

                if (!inProxies)
                {
                    continue;
                }

                if (isItemStart)
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result.Add(current);
                    levels.Clear();
                    line = line.Length > 1 ? line[2..].TrimStart() : string.Empty;

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    // 列表项的第一个字段与 "- " 同行，它所在的缩进按 2 算。
                    indent = 2;
                }

                if (current is null || line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                while (levels.Count > 0 && levels[^1].Indent >= indent)
                {
                    levels.RemoveAt(levels.Count - 1);
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                // 前缀按**由外到内**拼。这里用 List 当栈而不是 Stack<T>：
                // Stack 的枚举是从顶到底的，拼出来会是 "headers.ws-opts." 这种反序。
                var prefix = new StringBuilder();
                foreach (var level in levels)
                {
                    prefix.Append(level.Key).Append('.');
                }

                var fullKey = prefix.ToString() + key;

                if (value.Length == 0 || value == "~" || value == "null")
                {
                    // 可能是嵌套 map，先压栈；下一行若不是更深缩进，会被上面弹掉。
                    levels.Add((indent, key));
                    current[fullKey] = string.Empty;
                    continue;
                }

                current[fullKey] = TrimQuotes(value);
            }

            return result;
        }

        private static string TrimQuotes(string value)
        {
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                return value[1..^1];
            }

            return value;
        }

        private static string Get(Dictionary<string, string> proxy, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (proxy.TryGetValue(key, out var value) && value.Length > 0)
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string ToShareLink(Dictionary<string, string> proxy)
        {
            var type = Get(proxy, "type").ToLowerInvariant();
            var name = Get(proxy, "name");
            var server = Get(proxy, "server");
            var port = Get(proxy, "port");

            if (server.Length == 0 || port.Length == 0)
            {
                return string.Empty;
            }

            var network = Get(proxy, "network");
            if (network.Length == 0)
            {
                network = "tcp";
            }

            var tls = IsTrue(Get(proxy, "tls"));
            var sni = Get(proxy, "servername", "sni");
            if (sni.Length == 0 && tls)
            {
                sni = server;
            }

            var host = Get(proxy, "ws-opts.headers.Host", "ws-opts.headers.host", "h2-opts.host");
            var path = Get(proxy, "ws-opts.path", "h2-opts.path", "grpc-opts.grpc-service-name");
            var uuid = Get(proxy, "uuid", "password");
            var insecure = IsTrue(Get(proxy, "skip-cert-verify"));
            var alpn = Get(proxy, "alpn");

            return type switch
            {
                "vmess" => BuildVmess(proxy, name, server, port, network, tls, sni, host, path, alpn, insecure),
                "vless" => BuildVless(proxy, name, server, port, network, tls, sni, host, path, uuid, alpn, insecure),
                "trojan" => BuildTrojan(name, server, port, network, sni, host, path, Get(proxy, "password"), alpn, insecure),
                "ss" or "shadowsocks" => BuildShadowsocks(proxy, name, server, port),
                _ => string.Empty
            };
        }

        /// <summary>
        /// vmess:// 后面是 Base64 的 JSON（v2rayN 约定）。字段与模拟器 `to_uri` 一致。
        /// </summary>
        private static string BuildVmess(
            Dictionary<string, string> proxy,
            string name,
            string server,
            string port,
            string network,
            bool tls,
            string sni,
            string host,
            string path,
            string alpn,
            bool insecure)
        {
            var payload = new Dictionary<string, string>
            {
                ["v"] = "2",
                ["ps"] = name,
                ["add"] = server,
                ["port"] = port,
                ["id"] = Get(proxy, "uuid"),
                ["aid"] = Get(proxy, "alterId", "alterid"),
                ["scy"] = Get(proxy, "cipher", "method"),
                ["net"] = network,
                ["type"] = "none",
                ["host"] = host,
                ["path"] = path,
                ["tls"] = tls ? "tls" : string.Empty,
                ["sni"] = sni,
                ["alpn"] = alpn,

                // 标准 vmess 链接不带这个字段，但上游订阅普遍要求跳过证书校验，
                // 丢了它就会连不上（见 ShareLinkParser.ParseVmess 的对应处理）。
                ["allowInsecure"] = insecure ? "1" : string.Empty
            };

            if (payload["scy"].Length == 0)
            {
                payload["scy"] = "auto";
            }

            if (payload["aid"].Length == 0)
            {
                payload["aid"] = "0";
            }

            var json = JsonSerializer.Serialize(payload, ClashJsonContext.Default.DictionaryStringString);
            return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        private static string BuildVless(
            Dictionary<string, string> proxy,
            string name,
            string server,
            string port,
            string network,
            bool tls,
            string sni,
            string host,
            string path,
            string uuid,
            string alpn,
            bool insecure)
        {
            // REALITY 订阅里写作 reality-opts.public-key / short-id（同时 tls: true + servername）。
            // 少了 pbk 会退化成普通 TLS 连不上，必须原样透传（见 XrayConfigBuilder 的 reality 分支）。
            var publicKey = Get(proxy, "reality-opts.public-key", "reality-opts.publicKey", "public-key");
            var shortId = Get(proxy, "reality-opts.short-id", "reality-opts.shortId", "short-id");
            var isReality = publicKey.Length > 0;
            var fingerprint = Get(proxy, "client-fingerprint", "fingerprint");

            var query = new List<string>
            {
                "encryption=none",
                "security=" + (isReality ? "reality" : tls ? "tls" : "none"),
                "type=" + Uri.EscapeDataString(network)
            };

            if (host.Length > 0)
            {
                query.Add("host=" + Uri.EscapeDataString(host));
            }

            if (path.Length > 0)
            {
                query.Add((network == "grpc" ? "serviceName=" : "path=") + Uri.EscapeDataString(path));
            }

            if (sni.Length > 0)
            {
                query.Add("sni=" + Uri.EscapeDataString(sni));
            }

            if (alpn.Length > 0)
            {
                query.Add("alpn=" + Uri.EscapeDataString(alpn));
            }

            if (fingerprint.Length > 0)
            {
                query.Add("fp=" + Uri.EscapeDataString(fingerprint));
            }

            if (isReality)
            {
                query.Add("pbk=" + Uri.EscapeDataString(publicKey));

                if (shortId.Length > 0)
                {
                    query.Add("sid=" + Uri.EscapeDataString(shortId));
                }
            }

            var flow = Get(proxy, "flow");
            if (flow.Length > 0)
            {
                query.Add("flow=" + Uri.EscapeDataString(flow));
            }

            if (insecure)
            {
                query.Add("allowInsecure=1");
            }

            return $"vless://{uuid}@{server}:{port}?{string.Join('&', query)}#{Uri.EscapeDataString(name)}";
        }

        private static string BuildTrojan(
            string name,
            string server,
            string port,
            string network,
            string sni,
            string host,
            string path,
            string password,
            string alpn,
            bool insecure)
        {
            var query = new List<string>
            {
                // Trojan 定义上就是 TLS：必须显式写出，否则链接会被解析成明文 trojan 而连不上。
                "security=tls"
            };

            if (sni.Length > 0)
            {
                query.Add("sni=" + Uri.EscapeDataString(sni));
            }

            if (alpn.Length > 0)
            {
                query.Add("alpn=" + Uri.EscapeDataString(alpn));
            }

            if (network.Length > 0 && network != "tcp")
            {
                query.Add("type=" + Uri.EscapeDataString(network));
            }

            if (host.Length > 0)
            {
                query.Add("host=" + Uri.EscapeDataString(host));
            }

            if (path.Length > 0)
            {
                query.Add("path=" + Uri.EscapeDataString(path));
            }

            if (insecure)
            {
                query.Add("allowInsecure=1");
            }

            var qs = query.Count > 0 ? "?" + string.Join('&', query) : string.Empty;
            return $"trojan://{Uri.EscapeDataString(password)}@{server}:{port}{qs}#{Uri.EscapeDataString(name)}";
        }

        private static string BuildShadowsocks(
            Dictionary<string, string> proxy,
            string name,
            string server,
            string port)
        {
            var method = Get(proxy, "cipher", "method");
            if (method.Length == 0)
            {
                method = "aes-128-gcm";
            }

            var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(method + ":" + Get(proxy, "password")));
            var link = $"ss://{userInfo.TrimEnd('=').Replace('+', '-').Replace('/', '_')}@{server}:{port}#{Uri.EscapeDataString(name)}";
            return link;
        }

        private static bool IsTrue(string value) =>
            value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }
}
