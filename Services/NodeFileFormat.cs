using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RayShuttle.Services
{
    /// <summary>
    /// 加密节点文件解密后的结构。字段与 tools/make_nodes.py 产出的 JSON 一一对应。
    /// 枚举一律用字符串承载，在 <see cref="NodeRepository"/> 里显式解析——
    /// 这样未知取值可以被明确拒绝，而不是悄悄落到某个默认值上。
    /// </summary>
    internal sealed class NodeFileDto
    {
        public int Version { get; set; }

        public string? UpdatedAt { get; set; }

        public List<NodeDto>? Nodes { get; set; }
    }

    internal sealed class NodeDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Group { get; set; }

        public string? Country { get; set; }

        public string? Protocol { get; set; }

        public string? Address { get; set; }

        public int Port { get; set; }

        public string? Uuid { get; set; }

        public int AlterId { get; set; }

        public string? Security { get; set; }

        public string? Flow { get; set; }

        public string? Password { get; set; }

        public string? Method { get; set; }

        public string? Transport { get; set; }

        public string? Path { get; set; }

        public string? Host { get; set; }

        public string? ServiceName { get; set; }

        /// <summary>XHTTP 的 mode，取值 auto / packet-up / stream-up / stream-one。</summary>
        public string? Mode { get; set; }

        public bool Tls { get; set; }

        public string? ServerName { get; set; }

        public bool AllowInsecure { get; set; }

        public string? Fingerprint { get; set; }

        public int LatencyMs { get; set; }

        public bool Recommended { get; set; }
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(NodeFileDto))]
    internal sealed partial class NodeFileJsonContext : JsonSerializerContext
    {
    }
}
