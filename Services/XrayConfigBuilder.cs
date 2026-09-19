using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>
    /// 把 <see cref="ProxyNode"/> 翻译成 Xray 的 JSON 配置。
    ///
    /// 设计取舍：
    /// - **不使用 geoip:/geosite: 规则**。那会强制要求 geoip.dat / geosite.dat 两个数据文件，
    ///   缺一个 Xray 就起不来。私有网段直接用 CIDR 写死，零额外依赖。
    /// - 出站不设 domainStrategy，保持 Xray 默认的 AsIs：域名原样交给服务端解析，
    ///   本地不发生 DNS 泄漏。这也是绝大多数代理客户端的行为。
    /// - inbound 只监听 127.0.0.1，不对外暴露。
    /// </summary>
    public static class XrayConfigBuilder
    {
        // 注意：JsonNode.ToJsonString 内部会把传入的 JsonSerializerOptions 标记为只读，
        // 而只读的 options 必须带 TypeInfoResolver（即使 JsonNode 序列化本身用不到它），
        // 否则在高版本 .NET 8 上抛 "must specify a TypeInfoResolver setting before
        // being marked as read-only"。这里给一个显式 resolver 满足该约束。
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };

        private static readonly string[] PrivateNetworks =
        {
            "127.0.0.0/8",
            "10.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16",
            "169.254.0.0/16",
            "::1/128",
            "fc00::/7",
            "fe80::/10"
        };

        /// <summary>生成完整配置。socksPort / httpPort 是本机入口端口，statsPort 是 StatsService 监听端口。</summary>
        public static string Build(ProxyNode node, int socksPort, int httpPort, int statsPort)
        {
            ArgumentNullException.ThrowIfNull(node);

            var root = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    // warning 级别足够定位问题，又不会把每个连接都刷进日志。
                    ["loglevel"] = "warning"
                },
                // 开启流量计数：每个打了 tag 的 inbound / outbound 会自动产生
                // >>>traffic>>>downlink / >>>traffic>>>uplink 计数，供首页实时速率读取。
                // 注意：光有 stats:{} 还不够——必须在 policy.system 里显式打开这几个开关，
                // Xray 才会真正记录计数，否则 StatsService 永远查不到任何计数器（首页速率恒为 0）。
                ["stats"] = new JsonObject(),
                ["policy"] = new JsonObject
                {
                    ["system"] = new JsonObject
                    {
                        ["statsInboundUplink"] = true,
                        ["statsInboundDownlink"] = true,
                        ["statsOutboundUplink"] = true,
                        ["statsOutboundDownlink"] = true
                    }
                },
                // 暴露 StatsService（gRPC），监听本机回环，仅用于本地读取统计。
                ["api"] = new JsonObject
                {
                    ["tag"] = "api",
                    ["listen"] = $"127.0.0.1:{statsPort}",
                    ["services"] = new JsonArray { "StatsService" }
                },
                ["inbounds"] = new JsonArray
                {
                    BuildSocksInbound(socksPort),
                    BuildHttpInbound(httpPort)
                },
                ["outbounds"] = new JsonArray
                {
                    BuildProxyOutbound(node),
                    new JsonObject
                    {
                        ["tag"] = "direct",
                        ["protocol"] = "freedom"
                    },
                    new JsonObject
                    {
                        ["tag"] = "block",
                        ["protocol"] = "blackhole"
                    }
                },
                ["routing"] = new JsonObject
                {
                    ["domainStrategy"] = "AsIs",
                    ["rules"] = new JsonArray
                    {
                        // 局域网与本机地址直连，不走代理。
                        new JsonObject
                        {
                            ["type"] = "field",
                            ["outboundTag"] = "direct",
                            ["ip"] = new JsonArray(Array.ConvertAll(PrivateNetworks, value => (JsonNode)value))
                        }
                    }
                }
            };

            return root.ToJsonString(WriteOptions);
        }

        private static JsonObject BuildSocksInbound(int port) => new()
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject
            {
                // 允许 UDP：某些应用（含部分游戏的语音）依赖它。
                ["udp"] = true,
                ["auth"] = "noauth"
            }
        };

        private static JsonObject BuildHttpInbound(int port) => new()
        {
            ["tag"] = "http-in",
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = "http"
        };

        private static JsonObject BuildProxyOutbound(ProxyNode node)
        {
            var outbound = new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = ProtocolName(node.Protocol),
                ["settings"] = BuildProtocolSettings(node),
                ["streamSettings"] = BuildStreamSettings(node)
            };

            return outbound;
        }

        private static string ProtocolName(NodeProtocol protocol) => protocol switch
        {
            NodeProtocol.Vmess => "vmess",
            NodeProtocol.Vless => "vless",
            NodeProtocol.Trojan => "trojan",
            NodeProtocol.Shadowsocks => "shadowsocks",
            _ => throw new NotSupportedException($"不支持的协议：{protocol}")
        };

        private static JsonObject BuildProtocolSettings(ProxyNode node) => node.Protocol switch
        {
            NodeProtocol.Vmess => new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = node.Address,
                        ["port"] = node.Port,
                        ["users"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = node.Uuid,
                                ["alterId"] = node.AlterId,
                                ["security"] = node.Security
                            }
                        }
                    }
                }
            },

            NodeProtocol.Vless => new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = node.Address,
                        ["port"] = node.Port,
                        ["users"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = node.Uuid,
                                // VLESS 本身不加密，必须显式写 none。
                                ["encryption"] = "none",
                                ["flow"] = string.IsNullOrEmpty(node.Flow) ? string.Empty : node.Flow
                            }
                        }
                    }
                }
            },

            NodeProtocol.Trojan => new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = node.Address,
                        ["port"] = node.Port,
                        ["password"] = node.Password
                    }
                }
            },

            NodeProtocol.Shadowsocks => new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = node.Address,
                        ["port"] = node.Port,
                        ["method"] = node.Method,
                        ["password"] = node.Password
                    }
                }
            },

            _ => throw new NotSupportedException($"不支持的协议：{node.Protocol}")
        };

        private static JsonObject BuildStreamSettings(ProxyNode node)
        {
            var stream = new JsonObject
            {
                ["network"] = TransportName(node.Transport),
                ["security"] = node.Tls ? "tls" : "none"
            };

            if (node.Tls)
            {
                // serverName 缺失时回退到 address，否则握手会因 SNI 不匹配失败。
                var serverName = string.IsNullOrEmpty(node.ServerName) ? node.Address : node.ServerName;

                var tls = new JsonObject
                {
                    ["serverName"] = serverName,
                    ["allowInsecure"] = node.AllowInsecure
                };

                if (!string.IsNullOrEmpty(node.Fingerprint))
                {
                    tls["fingerprint"] = node.Fingerprint;
                }

                stream["tlsSettings"] = tls;
            }

            switch (node.Transport)
            {
                case NodeTransport.WebSocket:
                {
                    var ws = new JsonObject { ["path"] = node.Path };

                    if (!string.IsNullOrEmpty(node.Host))
                    {
                        // 必须用独立的 host 字段。写成 headers.Host 在 Xray 26 上会报
                        // 「This feature "host" in "headers" is deprecated」，官方已迁移到独立字段。
                        ws["host"] = node.Host;
                    }

                    stream["wsSettings"] = ws;
                    break;
                }

                case NodeTransport.Grpc:
                    stream["grpcSettings"] = new JsonObject { ["serviceName"] = node.ServiceName };
                    break;

                case NodeTransport.HttpUpgrade:
                {
                    var upgrade = new JsonObject { ["path"] = node.Path };

                    if (!string.IsNullOrEmpty(node.Host))
                    {
                        upgrade["host"] = node.Host;
                    }

                    stream["httpupgradeSettings"] = upgrade;
                    break;
                }

                case NodeTransport.Xhttp:
                {
                    // 字段结构经 protobuf 往返实测确认：path / host / mode 是内核真实识别的字段
                    // （当时故意混入的 bogusFieldName 在往返中被丢弃，可作为该验证手段有效的佐证）。
                    var xhttp = new JsonObject { ["path"] = node.Path };

                    if (!string.IsNullOrEmpty(node.Host))
                    {
                        xhttp["host"] = node.Host;
                    }

                    if (!string.IsNullOrEmpty(node.Mode))
                    {
                        // 留空即用内核默认 auto，不必显式写入。
                        xhttp["mode"] = node.Mode;
                    }

                    stream["xhttpSettings"] = xhttp;
                    break;
                }
            }

            return stream;
        }

        private static string TransportName(NodeTransport transport) => transport switch
        {
            NodeTransport.Tcp => "tcp",
            NodeTransport.WebSocket => "ws",
            NodeTransport.Grpc => "grpc",
            NodeTransport.HttpUpgrade => "httpupgrade",
            // 对外统一写 xhttp；内核内部仍叫 splithttp，但两者都认。
            NodeTransport.Xhttp => "xhttp",
            _ => "tcp"
        };
    }
}
