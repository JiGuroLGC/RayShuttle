using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RayShuttle.Models;
using RayShuttle.Services.Routing;

namespace RayShuttle.Services
{
    /// <summary>
    /// 把 <see cref="ProxyNode"/> 翻译成 Xray 的 JSON 配置。
    ///
    /// 分流规则（routing）：
    /// - 始终先写「局域网 / 本机地址直连」（写死 CIDR，零额外依赖）。
    /// - 其余规则由 <see cref="RoutingSettings"/> 决定：
    ///   · <see cref="RoutingMode.Global"/>   —— 不额外分流，全部走代理（仅局域网直连）；
    ///   · <see cref="RoutingMode.BypassMainland"/> —— 大陆直连；当 <c>Core/geoip.dat</c> 与
    ///     <c>Core/geosite.dat</c> 同时存在时升级为 geo 规则（覆盖最全），否则退化为内置精简清单；
    ///   · <see cref="RoutingMode.Custom"/>   —— 只按用户自己写的直连 / 代理清单。
    ///
    /// <see cref="RoutingSettings"/> 的变更需要「断开重连」才生效：内核每次启动读的是那一刻的配置，
    /// 改了配置不重连，流量仍走旧规则。
    /// </summary>
    public static class XrayConfigBuilder
    {
        // JsonNode.ToJsonString() 自带内置转换器，不依赖反射，在 PublishTrimmed / AOT 下也安全；
        // 因此这里不传任何 JsonSerializerOptions（更不传反射式的 DefaultJsonTypeInfoResolver，
        // 后者在裁剪构建里会被剥掉，连 Xray 配置生成都会抛 JsonTypeInfo 缺失）。

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

        /// <summary>
        /// 生成完整配置。socksPort / httpPort 是本机入口端口，statsPort 是 StatsService 监听端口。
        /// routing 为 null 时退化为「仅局域网直连」的全局代理。
        /// TUN 模式传 enableSniffing: true——链路上只有 IP 包，靠 sniffing 从 TLS SNI /
        /// HTTP Host 还原域名供分流规则匹配；routeOnly=true 让「连接目标」保持原始 IP
        /// （只影响路由判断），避免内核代为解析域名引入 DNS 污染。
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "JsonArray.Add<T> 收到的都是 JsonNode 派生类型（JsonObject/JsonValue），" +
                           "ConvertFromValue<T> 会原样返回、不依赖任何裁剪元数据；唯一一个字符串已显式转为 (JsonNode)。")]
        public static string Build(
            ProxyNode node,
            int socksPort,
            int httpPort,
            int statsPort,
            RoutingSettings? routing = null,
            bool enableSniffing = false)
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
                    // 必须用 (JsonNode) 把字符串转成 JsonNode，否则集合初始化器会选中泛型的
                    // JsonArray.Add<T>(string)，在裁剪构建里 JsonNode.ConvertFromValue<string>
                    // 因缺少 System.String 的元数据而抛 NotSupportedException（EmptyJsonTypeInfoResolver）。
                    ["services"] = new JsonArray { (JsonNode)"StatsService" }
                },
                ["inbounds"] = new JsonArray
                {
                    BuildSocksInbound(socksPort, enableSniffing),
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
                ["routing"] = BuildRouting(routing)
            };

            return root.ToJsonString();
        }

        /// <summary>
        /// 按模式生成 routing 段。始终包含「局域网 / 本机地址直连」这一条；
        /// 其余规则由模式决定。geo 规则只有在数据文件齐全时才写——否则 Xray 会因找不到
        /// geoip:cn / geosite:cn 而直接启动失败。
        /// </summary>
        private static JsonObject BuildRouting(RoutingSettings? routing)
        {
            var rules = new JsonArray();

            // 局域网 / 本机地址总是直连。geo 规则即使存在也不覆盖这一段（IP 段已显式列出）。
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "direct",
                ["ip"] = new JsonArray(Array.ConvertAll(PrivateNetworks, value => (JsonNode)value))
            });

            var mode = routing?.Mode ?? RoutingMode.Global;

            if (mode == RoutingMode.Global)
            {
                // 全局代理：不附加任何分流规则。
            }
            else if (mode == RoutingMode.BypassMainland)
            {
                if (GeoAssets.AreAvailable)
                {
                    // 完整覆盖：geoip:cn + geosite:cn 直连。
                    rules.Add(new JsonObject
                    {
                        ["type"] = "field",
                        ["outboundTag"] = "direct",
                        ["domain"] = new JsonArray { (JsonNode)"geosite:cn" }
                    });
                    rules.Add(new JsonObject
                    {
                        ["type"] = "field",
                        ["outboundTag"] = "direct",
                        ["ip"] = new JsonArray { (JsonNode)"geoip:cn" }
                    });
                }
                else
                {
                    // 退化清单：内置常见大陆站点与地址段。
                    rules.Add(new JsonObject
                    {
                        ["type"] = "field",
                        ["outboundTag"] = "direct",
                        ["domain"] = new JsonArray(Array.ConvertAll(BuiltinDirectRules.Domains.ToArray(), value => (JsonNode)value))
                    });
                    rules.Add(new JsonObject
                    {
                        ["type"] = "field",
                        ["outboundTag"] = "direct",
                        ["ip"] = new JsonArray(Array.ConvertAll(BuiltinDirectRules.Addresses.ToArray(), value => (JsonNode)value))
                    });
                }
            }
            else if (mode == RoutingMode.Custom)
            {
                // 仅按用户清单：直连清单在前，代理清单在后（后者优先级更高，可把误判域名捞回代理）。
                AddUserRules(rules, routing!, "direct");
                AddUserRules(rules, routing!, "proxy");
            }

            return new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = rules
            };
        }

        /// <summary>把用户的直连 / 代理清单转成 routing 规则。空清单跳过。</summary>
        private static void AddUserRules(JsonArray rules, RoutingSettings routing, string outboundTag)
        {
            var isDirect = outboundTag == "direct";
            var domains = isDirect ? routing.DirectDomains : routing.ProxyDomains;
            var addresses = routing.DirectAddresses;

            if (domains.Count > 0)
            {
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = outboundTag,
                    ["domain"] = new JsonArray(Array.ConvertAll(domains.ToArray(), value => (JsonNode)value))
                });
            }

            if (isDirect && addresses.Count > 0)
            {
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["ip"] = new JsonArray(Array.ConvertAll(addresses.ToArray(), value => (JsonNode)value))
                });
            }
        }

        private static JsonObject BuildSocksInbound(int port, bool enableSniffing)
        {
            var inbound = new JsonObject
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

            if (enableSniffing)
            {
                inbound["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray { (JsonNode)"http", (JsonNode)"tls", (JsonNode)"quic" },
                    // 只用嗅探结果做路由判断，连接目标保持原始 IP——
                    // 避免内核替我们解析域名时吃到 DNS 污染。
                    ["routeOnly"] = true
                };
            }

            return inbound;
        }

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

        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "同 Build：JsonArray 里放的都是 JsonObject/JsonValue，ConvertFromValue<T> 原样返回，安全。")]
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
                                ["flow"] = FlowFor(node)
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
                ["security"] = SecurityName(node)
            };

            if (node.Reality)
            {
                // REALITY **不是**普通 TLS：必须写 realitySettings，若写成 tlsSettings 会退化成
                // 「普通 TLS 打到 REALITY 服务端」——服务端把连接当成未授权探测转发到 fallback，
                // 客户端再往里发 VLESS/内层 TLS 就全是垃圾，表现为浏览器 ERR_SSL_PROTOCOL_ERROR、
                // 流量几乎为零（备用节点只下发 vless 时踩过）。
                // 字段名经 `Core\xray.exe convert pb -debug` 往返实测确认（serverName / fingerprint /
                // publicKey / shortId / spiderX 均为内核真实字段，拼错会被静默丢弃）。
                var reality = new JsonObject
                {
                    // serverName 即分享链接的 sni，也是 REALITY “借”证书的目标域名，不能缺。
                    ["serverName"] = string.IsNullOrEmpty(node.ServerName) ? node.Address : node.ServerName,
                    ["publicKey"] = node.PublicKey
                };

                if (!string.IsNullOrEmpty(node.Fingerprint))
                {
                    reality["fingerprint"] = node.Fingerprint;
                }

                if (!string.IsNullOrEmpty(node.ShortId))
                {
                    reality["shortId"] = node.ShortId;
                }

                if (!string.IsNullOrEmpty(node.SpiderX))
                {
                    reality["spiderX"] = node.SpiderX;
                }

                stream["realitySettings"] = reality;
            }
            else if (node.Tls)
            {
                // serverName 缺失时回退到 address，否则握手会因 SNI 不匹配失败。
                var serverName = string.IsNullOrEmpty(node.ServerName) ? node.Address : node.ServerName;

                // 注意：Xray 26.x 已彻底移除 TLS 的 allowInsecure 字段（旧版仅是弃用警告，
                // 新版直接报 “failed to build TLS config” 导致内核起不来）。该字段本意是
                // “跳过证书校验”，迁移目标是 pinnedPeerCertSha256 / verifyPeerCertByName，
                // 二者都需要事先知道对端证书信息，生成端并不持有。因此这里只保留 serverName
                // 与 fingerprint，让 Xray 走默认的系统 CA 校验——对正规公网证书即可正常握手。
                // AllowInsecure 仍被解析保留在模型里（不破坏分享链接/订阅解析），但不再写进配置。
                var tls = new JsonObject
                {
                    ["serverName"] = serverName
                };

                if (!string.IsNullOrEmpty(node.Fingerprint))
                {
                    tls["fingerprint"] = node.Fingerprint;
                }

                AddAlpn(tls, node.Alpn);

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

        /// <summary>
        /// streamSettings.security 取值。REALITY 优先于普通 TLS——模型里 <c>Reality</c> 与 <c>Tls</c>
        /// 会同时为真（分享链接的 <c>security=reality</c> 也属于「用了 TLS 外壳」），必须先判 REALITY。
        /// </summary>
        private static string SecurityName(ProxyNode node) => node.Reality
            ? "reality"
            : node.Tls ? "tls" : "none";

        /// <summary>
        /// VLESS 的 flow（如 xtls-rprx-vision）只有 <b>TCP(RAW) + TLS/REALITY</b> 这一种组合下有效，
        /// 其它情况内核会以 “flow is not supported” 直接拒绝启动。这里统一收敛：不满足即写空串
        /// （等于不使用 flow），保证生成的配置一定能被内核接受。
        /// </summary>
        private static string FlowFor(ProxyNode node) =>
            node.Transport == NodeTransport.Tcp && node.Tls && !string.IsNullOrEmpty(node.Flow)
                ? node.Flow
                : string.Empty;

        /// <summary>
        /// 把逗号分隔的 ALPN 写成 <c>tlsSettings.alpn</c> 数组；留空则不写（用内核默认）。
        /// 必须显式转 (JsonNode)，否则集合初始化会选中泛型 <c>JsonArray.Add&lt;string&gt;</c>，
        /// 在裁剪构建下会因缺少 System.String 元数据而抛异常。
        /// </summary>
        private static void AddAlpn(JsonObject tls, string alpn)
        {
            var items = alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (items.Length == 0)
            {
                return;
            }

            var array = new JsonArray();
            foreach (var item in items)
            {
                array.Add((JsonNode)item);
            }

            tls["alpn"] = array;
        }

        private static string TransportName(NodeTransport transport) => transport switch
        {
            // Xray 26 已把 tcp 传输更名为 raw（tcp 仍作别名兼容），新配置统一写 raw，
            // 与 v2rayN 等主流客户端的产出一致。
            NodeTransport.Tcp => "raw",
            NodeTransport.WebSocket => "ws",
            NodeTransport.Grpc => "grpc",
            NodeTransport.HttpUpgrade => "httpupgrade",
            // 对外统一写 xhttp；内核内部仍叫 splithttp，但两者都认。
            NodeTransport.Xhttp => "xhttp",
            _ => "tcp"
        };
    }
}
