using RayShuttle.Models;

namespace RayShuttle.Services
{
    /// <summary>
    /// 把文件记录（<see cref="NodeDto"/>）校验并转换成节点。
    ///
    /// 抽出来是因为有**两个来源**都要产出节点：节点文件里的 JSON 结构，
    /// 以及 vmess:// / vless:// 这类分享链接。两者必须遵守完全相同的必填项规则，
    /// 各写一份迟早会漂移——那类问题表现为「某条链接能导入但连不上」，很难查。
    ///
    /// 任何必填项缺失一律返回 null，**不猜默认值**：猜出来的配置只会让连接在更靠后的
    /// 地方失败，排查成本高得多。
    /// </summary>
    internal static class NodeFactory
    {
        public static ProxyNode? Create(NodeDto dto)
        {
            var address = dto.Address?.Trim();
            if (string.IsNullOrEmpty(address))
            {
                return null;
            }

            if (dto.Port is <= 0 or > 65535)
            {
                return null;
            }

            if (!TryParseProtocol(dto.Protocol, out var protocol)
                || !TryParseTransport(dto.Transport, out var transport))
            {
                return null;
            }

            if (!HasRequiredCredentials(dto, protocol))
            {
                return null;
            }

            if (!HasRequiredTransportOptions(dto, transport))
            {
                return null;
            }

            // REALITY 少了 publicKey，内核会以 “REALITY: empty public key” 直接拒绝启动；
            // 与其让连接在很久之后失败，不如在这里就明确拒绝这条节点。
            if (dto.Reality && string.IsNullOrWhiteSpace(dto.PublicKey))
            {
                return null;
            }

            return new ProxyNode
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? address : dto.Id.Trim(),
                SlotId = dto.SlotId?.Trim() ?? string.Empty,
                Name = string.IsNullOrWhiteSpace(dto.Name) ? address : dto.Name.Trim(),
                Group = dto.Group?.Trim() ?? string.Empty,
                Country = dto.Country?.Trim() ?? string.Empty,
                Protocol = protocol,
                Address = address,
                Port = dto.Port,
                Uuid = dto.Uuid?.Trim() ?? string.Empty,
                AlterId = dto.AlterId,
                Security = string.IsNullOrWhiteSpace(dto.Security) ? "auto" : dto.Security.Trim(),
                Flow = dto.Flow?.Trim() ?? string.Empty,
                Password = dto.Password ?? string.Empty,
                Method = dto.Method?.Trim() ?? string.Empty,
                Transport = transport,
                Path = dto.Path?.Trim() ?? string.Empty,
                Host = dto.Host?.Trim() ?? string.Empty,
                ServiceName = dto.ServiceName?.Trim() ?? string.Empty,
                Mode = dto.Mode?.Trim() ?? string.Empty,
                Tls = dto.Tls,
                ServerName = dto.ServerName?.Trim() ?? string.Empty,
                AllowInsecure = dto.AllowInsecure,
                Fingerprint = dto.Fingerprint?.Trim() ?? string.Empty,
                Alpn = dto.Alpn?.Trim() ?? string.Empty,
                Reality = dto.Reality,
                PublicKey = dto.PublicKey?.Trim() ?? string.Empty,
                ShortId = dto.ShortId?.Trim() ?? string.Empty,
                SpiderX = dto.SpiderX?.Trim() ?? string.Empty,
                LatencyMs = dto.LatencyMs,
                IsRecommended = dto.Recommended
            };
        }

        private static bool HasRequiredCredentials(NodeDto dto, NodeProtocol protocol) => protocol switch
        {
            NodeProtocol.Vmess or NodeProtocol.Vless => !string.IsNullOrWhiteSpace(dto.Uuid),
            NodeProtocol.Trojan => !string.IsNullOrWhiteSpace(dto.Password),
            NodeProtocol.Shadowsocks =>
                !string.IsNullOrWhiteSpace(dto.Password) && !string.IsNullOrWhiteSpace(dto.Method),
            _ => false
        };

        private static bool HasRequiredTransportOptions(NodeDto dto, NodeTransport transport) => transport switch
        {
            // 传输层各自必需的参数，缺一个都会让 Xray 起不来。
            // XHTTP 与 WS / HTTPUpgrade 一样依赖 path 区分入口。
            NodeTransport.WebSocket or NodeTransport.HttpUpgrade or NodeTransport.Xhttp =>
                !string.IsNullOrWhiteSpace(dto.Path),
            NodeTransport.Grpc => !string.IsNullOrWhiteSpace(dto.ServiceName),
            _ => true
        };

        /// <summary>解析协议名。未知取值返回 false，而不是落到默认协议上。</summary>
        public static bool TryParseProtocol(string? value, out NodeProtocol protocol)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "vmess":
                    protocol = NodeProtocol.Vmess;
                    return true;
                case "vless":
                    protocol = NodeProtocol.Vless;
                    return true;
                case "trojan":
                    protocol = NodeProtocol.Trojan;
                    return true;
                case "shadowsocks" or "ss":
                    protocol = NodeProtocol.Shadowsocks;
                    return true;
                default:
                    protocol = default;
                    return false;
            }
        }

        public static bool TryParseTransport(string? value, out NodeTransport transport)
        {
            // 缺省即 TCP：多数分享链接不会显式写 transport。
            if (string.IsNullOrWhiteSpace(value))
            {
                transport = NodeTransport.Tcp;
                return true;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "tcp" or "raw" or "none":
                    transport = NodeTransport.Tcp;
                    return true;
                case "ws" or "websocket":
                    transport = NodeTransport.WebSocket;
                    return true;
                case "grpc":
                    transport = NodeTransport.Grpc;
                    return true;
                case "httpupgrade":
                    transport = NodeTransport.HttpUpgrade;
                    return true;
                case "xhttp" or "splithttp":
                    // 内部协议名是 splithttp，部分客户端导出时仍用旧名。
                    transport = NodeTransport.Xhttp;
                    return true;
                default:
                    transport = default;
                    return false;
            }
        }
    }
}
