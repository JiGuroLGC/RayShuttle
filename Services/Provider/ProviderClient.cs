using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services.Provider
{
    /// <summary>
    /// 访问供应商的**查询**接口（取账号信息 + 拉订阅）。
    ///
    /// 注册接口不在这里，也不可以在这里：它只在 Worker 侧（server/src/provider.ts）。
    /// 「一个账号 5GB」这条约束成立的前提，就是客户端拿不到注册能力。
    ///
    /// 两个接口用的是不同域名，且 UA 不同：
    ///   - init：  https://broq1api.apbdmte.com:3927/api/data/init/{uuid}.json，UA okhttp/4.12.0
    ///   - 订阅：  订阅地址自带域名，UA ClashforWindows/0.19.23
    /// </summary>
    internal static class ProviderClient
    {
        /// <summary>查账号信息用这个域名（注册用的是 broq11api，两个 1，别混）。</summary>
        private const string InitHost = "broq1api.apbdmte.com:3927";

        private const string AppUa = "okhttp/4.12.0";
        private const string SubscriptionUa = "ClashforWindows/0.19.23";

        /// <summary>init 的响应有 200KB 以上，给足超时。</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static readonly HttpClient Client = CreateClient();

        /// <summary>
        /// **不走系统代理。** 供应商 API 在国内可直连，而连接状态下系统代理指向本机 Xray，
        /// 走代理既绕远又会被自己内核的路由规则影响。刷新一般发生在断开之后，直连更稳。
        /// </summary>
        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler { UseProxy = false };
            return new HttpClient(handler) { Timeout = Timeout };
        }

        /// <summary>
        /// 查账号用量与订阅地址。
        ///
        /// 注意 `userinfo` 为 null 是**正常情况**（未注册的 uuid 也返回 HTTP 200 + code 200），
        /// 所以判空要判 `userinfo`，不能只看 HTTP 状态码。
        /// </summary>
        public static async Task<ProviderQuery> QueryUsageAsync(string uuid, CancellationToken cancellationToken)
        {
            string body;
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://{InitHost}/api/data/init/{Uri.EscapeDataString(uuid)}.json");
                request.Headers.TryAddWithoutValidation("User-Agent", AppUa);

                using var response = await Client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return ProviderQuery.Unavailable();
                }

                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return ProviderQuery.Unavailable();
            }

            return ParseInit(uuid, body);
        }

        private static ProviderQuery ParseInit(string uuid, string body)
        {
            var plaintext = ProviderCrypto.Decrypt(body);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(plaintext);
            }
            catch (JsonException)
            {
                return ProviderQuery.Unavailable();
            }

            using (document)
            {
                var root = document.RootElement;

                if (!root.TryGetProperty("userinfo", out var userinfo)
                    || userinfo.ValueKind != JsonValueKind.Object)
                {
                    return ProviderQuery.Gone();
                }

                if (!userinfo.TryGetProperty("vpn", out var vpn) || vpn.ValueKind != JsonValueKind.Object)
                {
                    return ProviderQuery.Gone();
                }

                return new ProviderQuery(ProviderQueryStatus.Ok, new ProviderUsage
                {
                    Uuid = uuid,
                    SubUrl = ReadString(vpn, "sub_url_clash"),
                    TransferEnableBytes = TrafficText.Parse(ReadString(vpn, "transfer_enable")),
                    UnusedBytes = TrafficText.Parse(ReadString(vpn, "unused_traffic")),
                    UsedBytes = TrafficText.Parse(ReadString(vpn, "used_traffic")),
                    ExpireAtUnix = ProviderLimits.ParseExpire(ReadString(vpn, "user_class_expire_date")),
                    UserClass = ReadInt(vpn, "user_class")
                });
            }
        }

        /// <summary>拉订阅原文。失败返回 null。</summary>
        public static async Task<string?> FetchSubscriptionAsync(string subUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(subUrl))
            {
                return null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, subUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", SubscriptionUa);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");

                using var response = await Client.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode
                    ? await response.Content.ReadAsStringAsync(cancellationToken)
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static int? ReadInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;
    }
}
