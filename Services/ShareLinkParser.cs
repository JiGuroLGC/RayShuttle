using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>
    /// 解析 vmess:// / vless:// / trojan:// / ss:// 分享链接。
    ///
    /// 解析结果先变成 <see cref="NodeDto"/>，再交给 <see cref="NodeFactory.Create"/> 校验——
    /// 与节点文件里的 JSON 走**完全相同**的必填项规则，两条路不会漂移。
    ///
    /// 几个刻意的处理：
    /// - **不依赖 System.Uri**：备注名里常有中文，`new Uri(...)` 对含中文的 fragment
    ///   会转义甚至抛异常。这里手工按 `#`、`?`、`@`、`:` 切分，行为完全可预期。
    /// - Base64 同时兼容标准与 URL-safe、以及缺失的 `=` 补齐。
    /// - 未知协议或未知传输方式一律返回 false，不落到默认值上。
    /// </summary>
    public static class ShareLinkParser
    {
        private static readonly string[] Schemes = { "vmess", "vless", "trojan", "ss", "shadowsocks" };

        /// <summary>快速判断一行文本是不是分享链接，用于让节点文件支持纯链接列表。</summary>
        public static bool IsShareLink(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.TrimStart();
            var separator = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var scheme = trimmed[..separator].ToLowerInvariant();
            return Array.IndexOf(Schemes, scheme) >= 0;
        }

        public static bool TryParse(string? link, out ProxyNode? node)
        {
            node = null;

            if (!IsShareLink(link))
            {
                return false;
            }

            var trimmed = link!.Trim();
            var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
            var scheme = trimmed[..schemeEnd].ToLowerInvariant();
            var payload = trimmed[(schemeEnd + 3)..];

            // 先摘掉 fragment（备注名）。分享链接里的 # 只会作为 fragment 出现，
            // Base64 字母表不含 #，所以这一步对 vmess 同样安全。
            var name = string.Empty;
            var fragmentIndex = payload.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                name = SafeUnescape(payload[(fragmentIndex + 1)..]);
                payload = payload[..fragmentIndex];
            }

            var dto = scheme switch
            {
                "vmess" => ParseVmess(payload, name),
                "vless" => ParseStandardUri(payload, name, "vless"),
                "trojan" => ParseStandardUri(payload, name, "trojan"),
                "ss" or "shadowsocks" => ParseShadowsocks(payload, name),
                _ => null
            };

            if (dto is null)
            {
                return false;
            }

            dto.Id = BuildStableId(scheme, dto);

            node = NodeFactory.Create(dto);
            return node is not null;
        }

        // ------------------------------------------------------------ vmess

        /// <summary>vmess:// 后面是 Base64 编码的 JSON（v2rayN 约定）。</summary>
        private static NodeDto? ParseVmess(string payload, string name)
        {
            if (!TryDecodeBase64(payload, out var json))
            {
                return null;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var host = GetString(root, "host");
                var transportText = GetString(root, "net");
                var path = GetString(root, "path");

                // vmess 链接把 gRPC 的 serviceName 也塞在 path 字段里，需要按传输方式区分，
                // 否则 WS 节点会带上一个毫无意义的 serviceName。
                var isGrpc = NodeFactory.TryParseTransport(transportText, out var parsedTransport)
                    && parsedTransport == NodeTransport.Grpc;

                var dto = new NodeDto
                {
                    Protocol = "vmess",
                    Address = GetString(root, "add"),
                    Port = GetInt(root, "port"),
                    Uuid = GetString(root, "id"),
                    AlterId = GetInt(root, "aid"),
                    // scy 是加密方式；部分客户端把它写成 security，故互为兜底。
                    Security = FirstNonEmpty(GetString(root, "scy"), GetString(root, "security")),
                    Transport = transportText,
                    Path = isGrpc ? string.Empty : path,
                    Host = host,
                    ServiceName = isGrpc ? path : string.Empty,
                    // vmess 的 JSON 约定里没有 mode，但部分客户端会带上，有就读。
                    Mode = GetString(root, "mode"),
                    ServerName = FirstNonEmpty(GetString(root, "sni"), host),
                    Fingerprint = GetString(root, "fp"),
                    Name = string.IsNullOrWhiteSpace(name) ? GetString(root, "ps") : name
                };

                dto.Tls = IsTlsValue(GetString(root, "tls")) || IsTlsValue(GetString(root, "security"));
                return dto;
            }
        }

        // ------------------------------------------------- vless / trojan

        /// <summary>形如 scheme://userinfo@host:port?query#name。</summary>
        private static NodeDto? ParseStandardUri(string payload, string name, string protocol)
        {
            var queryIndex = payload.IndexOf('?');
            var query = queryIndex >= 0 ? payload[(queryIndex + 1)..] : string.Empty;
            var authority = queryIndex >= 0 ? payload[..queryIndex] : payload;

            if (!TrySplitAuthority(authority, out var userInfo, out var host, out var port))
            {
                return null;
            }

            var parameters = ParseQuery(query);
            var hostHeader = Get(parameters, "host");
            var security = Get(parameters, "security");

            var dto = new NodeDto
            {
                Protocol = protocol,
                Address = host,
                Port = port,
                Name = name,
                Transport = Get(parameters, "type"),
                Path = Get(parameters, "path"),
                Host = hostHeader,
                ServiceName = Get(parameters, "serviceName"),
                // XHTTP 的 mode，取值 auto / packet-up / stream-up / stream-one。
                Mode = Get(parameters, "mode"),
                ServerName = FirstNonEmpty(Get(parameters, "sni"), hostHeader),
                Fingerprint = Get(parameters, "fp"),
                Flow = Get(parameters, "flow"),
                Tls = IsTlsValue(security),
                AllowInsecure = Get(parameters, "allowInsecure") is "1" or "true" or "True"
            };

            if (protocol == "vless")
            {
                dto.Uuid = SafeUnescape(userInfo);
            }
            else
            {
                dto.Password = SafeUnescape(userInfo);
            }

            return dto;
        }

        // -------------------------------------------------------- shadowsocks

        /// <summary>
        /// 两种格式都要认：
        ///   SIP002 —— ss://Base64(method:password)@host:port#name
        ///   旧格式 —— ss://Base64(method:password@host:port)#name
        /// </summary>
        private static NodeDto? ParseShadowsocks(string payload, string name)
        {
            // 插件参数（plugin=...）当前不支持，直接丢弃该参数而不是拒绝整条链接。
            var queryIndex = payload.IndexOf('?');
            if (queryIndex >= 0)
            {
                payload = payload[..queryIndex];
            }

            var atIndex = payload.LastIndexOf('@');

            if (atIndex >= 0)
            {
                var userPart = payload[..atIndex];
                var hostPart = payload[(atIndex + 1)..];

                // SIP002 允许明文，也允许 Base64，两种都试一遍。
                var credentials = TryDecodeBase64(userPart, out var decoded) ? decoded : SafeUnescape(userPart);

                if (!TrySplitCredentials(credentials, out var method, out var password))
                {
                    return null;
                }

                if (!TrySplitAuthority(hostPart, out _, out var host, out var port))
                {
                    return null;
                }

                return new NodeDto
                {
                    Protocol = "shadowsocks",
                    Address = host,
                    Port = port,
                    Method = method,
                    Password = password,
                    Name = name
                };
            }

            // 旧格式：整段 Base64，解出来是 method:password@host:port
            if (!TryDecodeBase64(payload, out var legacy))
            {
                return null;
            }

            var legacyAtIndex = legacy.LastIndexOf('@');
            if (legacyAtIndex < 0)
            {
                return null;
            }

            if (!TrySplitCredentials(legacy[..legacyAtIndex], out var legacyMethod, out var legacyPassword))
            {
                return null;
            }

            if (!TrySplitAuthority(legacy[(legacyAtIndex + 1)..], out _, out var legacyHost, out var legacyPort))
            {
                return null;
            }

            return new NodeDto
            {
                Protocol = "shadowsocks",
                Address = legacyHost,
                Port = legacyPort,
                Method = legacyMethod,
                Password = legacyPassword,
                Name = name
            };
        }

        // ------------------------------------------------------------- 工具

        /// <summary>把 userinfo@host:port 拆开，兼容 IPv6 的 [::1]:443 写法。</summary>
        private static bool TrySplitAuthority(
            string authority,
            out string userInfo,
            out string host,
            out int port)
        {
            userInfo = string.Empty;
            host = string.Empty;
            port = 0;

            // SIP002 允许在 host:port 之后带一个 "/"，需要先切掉。
            var slashIndex = authority.IndexOf('/');
            if (slashIndex >= 0)
            {
                authority = authority[..slashIndex];
            }

            var atIndex = authority.LastIndexOf('@');
            var hostPort = authority;

            if (atIndex >= 0)
            {
                userInfo = authority[..atIndex];
                hostPort = authority[(atIndex + 1)..];
            }

            var colonIndex = hostPort.LastIndexOf(':');
            if (colonIndex <= 0)
            {
                return false;
            }

            host = hostPort[..colonIndex];
            var portText = hostPort[(colonIndex + 1)..];

            if (host.StartsWith('[') && host.EndsWith(']'))
            {
                host = host[1..^1];
            }

            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            return int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                && port is > 0 and <= 65535;
        }

        private static bool TrySplitCredentials(string value, out string method, out string password)
        {
            method = string.Empty;
            password = string.Empty;

            var separator = value.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            method = value[..separator].Trim();
            password = value[(separator + 1)..];
            return method.Length > 0;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator < 0)
                {
                    result[pair] = string.Empty;
                    continue;
                }

                result[pair[..separator]] = SafeUnescape(pair[(separator + 1)..]);
            }

            return result;
        }

        private static string Get(Dictionary<string, string> source, string key) =>
            source.TryGetValue(key, out var value) ? value : string.Empty;

        /// <summary>
        /// 同时兼容标准与 URL-safe Base64，并按需补齐 padding——
        /// 不同客户端导出的链接在这两点上并不统一。
        /// </summary>
        private static bool TryDecodeBase64(string value, out string result)
        {
            result = string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');

            switch (normalized.Length % 4)
            {
                case 2:
                    normalized += "==";
                    break;
                case 3:
                    normalized += "=";
                    break;
                case 1:
                    // 长度 1 的余数在 Base64 里不可能出现。
                    return false;
            }

            try
            {
                result = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string SafeUnescape(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return value;
            }
        }

        private static string GetString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>vmess 链接里的端口与 alterId 有时是数字、有时是字符串，两种都要认。</summary>
        private static int GetInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return 0;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    return value.TryGetInt32(out var number) ? number : 0;

                case JsonValueKind.String:
                    return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 0;

                default:
                    return 0;
            }
        }

        private static bool IsTlsValue(string value) =>
            value.Equals("tls", StringComparison.OrdinalIgnoreCase)
            || value.Equals("reality", StringComparison.OrdinalIgnoreCase);

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 由链接内容生成稳定 Id，让「记住用户选中的节点」在多次导入之间保持有效。
        /// 只用连接相关的字段，**不含备注名**——改备注不该让选中项失效。
        /// </summary>
        private static string BuildStableId(string scheme, NodeDto dto)
        {
            var key = string.Join('|', scheme, dto.Address, dto.Port, dto.Uuid, dto.Password, dto.Method);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return "link-" + Convert.ToHexString(hash).ToLowerInvariant()[..8];
        }
    }
}
