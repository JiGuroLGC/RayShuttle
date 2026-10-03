using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>
    /// 读取 Xray 的 StatsService 流量统计。
    ///
    /// 不引入任何 gRPC 代码生成依赖：Xray 的 StatsService 只有两个简单的 unary RPC，
    /// 这里直接用 HttpClient 走 h2c（明文 HTTP/2）发一个 gRPC 调用，并用极小的手写
    /// protobuf 编解码解析返回。这样既不依赖 protoc / Grpc.Tools 原生工具链，
    /// 也不会因 ARM64 等平台缺 protoc 二进制而编译失败。
    /// </summary>
    internal sealed class XrayStatsClient : IDisposable
    {
        private readonly HttpClient _httpClient;

        static XrayStatsClient()
        {
            // Xray 的 api 监听是明文 HTTP/2（h2c），需要打开这个开关才能协商成功。
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        public XrayStatsClient(string host, int port)
        {
            _httpClient = new HttpClient(new SocketsHttpHandler())
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = TimeSpan.FromSeconds(3)
            };
        }

        public void Dispose() => _httpClient.Dispose();

        public async Task<XrayTraffic> QueryAsync(CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/xray.app.stats.command.StatsService/QueryStats")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };

            // gRPC 要求 TE 头与 application/grpc 内容类型。
            request.Headers.TryAddWithoutValidation("TE", "trailers");
            request.Content = new ByteArrayContent(EncodeGrpcFrame(Array.Empty<byte>()));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var payload = DecodeGrpcFrame(body);

            return ParseTraffic(payload);
        }

        // ---- gRPC 帧 ----

        private static byte[] EncodeGrpcFrame(byte[] message)
        {
            var frame = new byte[5 + message.Length];
            frame[0] = 0; // 不压缩
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
            message.CopyTo(frame, 5);
            return frame;
        }

        private static byte[] DecodeGrpcFrame(byte[] body)
        {
            if (body.Length < 5 || body[0] != 0)
            {
                return Array.Empty<byte>();
            }

            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1));
            if (length <= 0 || length > body.Length - 5)
            {
                return Array.Empty<byte>();
            }

            return body.AsSpan(5, length).ToArray();
        }

        // ---- protobuf（只解析此处用到的两个消息）----

        private static XrayTraffic ParseTraffic(byte[] data)
        {
            long download = 0;
            long upload = 0;

            foreach (var stat in ParseRepeatedMessages(data, fieldNumber: 1))
            {
                var name = string.Empty;
                long value = 0;

                foreach (var (field, _, bytes, v) in ParseFields(stat))
                {
                    if (field == 1 && bytes is not null)
                    {
                        name = Encoding.UTF8.GetString(bytes);
                    }
                    else if (field == 2)
                    {
                        value = (long)v;
                    }
                }

                // 计数语义（Xray 官方命名 inbound>>>[tag]>>>traffic>>>uplink/downlink）：
                // - downlink = 数据从「广域网一侧」抵达代理、再回送给客户端的，即用户实际「下载」到的
                // - uplink   = 客户端发出、经代理送往广域网的，即用户实际「上传」的
                // 实测：客户端下载大文件时 downlink 快速增长，故 downlink → download、
                // uplink → upload（此前两值写反，导致首页下载/上传卡片颠倒）。
                // 只统计用户面 inbound，排除 api 这个管理通道自身产生的极小流量。
                if (name.StartsWith("inbound>>>", StringComparison.Ordinal) &&
                    !name.Contains(">>>api>>>", StringComparison.Ordinal))
                {
                    if (name.EndsWith(">>>downlink", StringComparison.Ordinal))
                    {
                        download += value;
                    }
                    else if (name.EndsWith(">>>uplink", StringComparison.Ordinal))
                    {
                        upload += value;
                    }
                }
            }

            return new XrayTraffic(download, upload);
        }

        /// <summary>取出所有指定 field 的 length-delimited 子缓冲区（repeated 嵌套消息）。</summary>
        private static IEnumerable<byte[]> ParseRepeatedMessages(byte[] data, int fieldNumber)
        {
            foreach (var (field, wire, bytes, _) in ParseFields(data))
            {
                if (field == fieldNumber && wire == 2 && bytes is not null)
                {
                    yield return bytes;
                }
            }
        }

        private static IEnumerable<(int Field, int Wire, byte[]? Bytes, ulong Value)> ParseFields(byte[] data)
        {
            var position = 0;

            while (position < data.Length)
            {
                var (tag, c0) = ReadVarint(data, position);
                position += c0;

                var field = (int)(tag >> 3);
                var wire = (int)(tag & 0x7);

                switch (wire)
                {
                    case 0:
                    {
                        var (value, c1) = ReadVarint(data, position);
                        position += c1;
                        yield return (field, wire, null, value);
                        break;
                    }

                    case 1: // 32-bit 定长，跳过
                        position += 4;
                        break;

                    case 2:
                    {
                        var (length, c2) = ReadVarint(data, position);
                        position += c2;
                        var bytes = data.AsSpan(position, (int)length).ToArray();
                        position += (int)length;
                        yield return (field, wire, bytes, 0);
                        break;
                    }

                    case 5: // 64-bit 定长，跳过
                        position += 8;
                        break;

                    default: // 3/4 已废弃的分组编码，本服务用不到
                        yield break;
                }
            }
        }

        private static (ulong Value, int Consumed) ReadVarint(byte[] data, int offset)
        {
            ulong result = 0;
            var shift = 0;
            var position = offset;

            while (position < data.Length)
            {
                var byteValue = data[position++];
                result |= (ulong)(byteValue & 0x7f) << shift;
                if ((byteValue & 0x80) == 0)
                {
                    return (result, position - offset);
                }

                shift += 7;
            }

            return (result, position - offset);
        }
    }

    /// <summary>一次采样得到的累计流量字节数。</summary>
    internal readonly struct XrayTraffic
    {
        public XrayTraffic(long downloadBytes, long uploadBytes)
        {
            DownloadBytes = downloadBytes;
            UploadBytes = uploadBytes;
        }

        public long DownloadBytes { get; }

        public long UploadBytes { get; }
    }
}
