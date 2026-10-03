using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Services;

namespace RayShuttle.Services.Api
{
    internal sealed record ApiResult<T>(bool Success, T? Data, string Code, int Status)
        where T : class
    {
        public static ApiResult<T> Ok(T data, int status) => new(true, data, "OK", status);

        public static ApiResult<T> Fail(string code, int status) => new(false, null, code, status);
    }

    /// <summary>
    /// 云端 API 的 HTTP 层：负责签名头的组装、超时与非 2xx 的归类。
    ///
    /// **必须绕开系统代理**：连接状态下系统代理被指向本机 Xray，默认 HttpClient 会把 API
    /// 请求也送进内核（绕不过去、还可能被内核的路由规则拦掉）。见 <see cref="CreateClient"/>。
    /// </summary>
    internal static class ApiClient
    {
        /// <summary>主地址单次尝试的最长时间。超时就切到备用地址。</summary>
        private static readonly TimeSpan PrimaryAttemptTimeout = TimeSpan.FromSeconds(10);

        /// <summary>备用地址单次尝试的最长时间。</summary>
        private static readonly TimeSpan BackupAttemptTimeout = TimeSpan.FromSeconds(10);

        private static readonly HttpClient Client = CreateClient();

        public static Task<ApiResult<SessionResponse>> LoginAsync(
            CredentialRequest request,
            CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(request, ApiJsonContext.Default.CredentialRequest);

            return SendAsync(
                HttpMethod.Post,
                "/login",
                body,
                token: null,
                userName: null,
                fingerprint: null,
                ApiJsonContext.Default.SessionResponse,
                cancellationToken);
        }

        public static Task<ApiResult<NodeResponse>> GetNodeAsync(
            string token,
            string userName,
            string fingerprint,
            CancellationToken cancellationToken) =>
            SendAsync(
                HttpMethod.Get,
                "/node",
                body: string.Empty,
                token,
                userName,
                fingerprint,
                ApiJsonContext.Default.NodeResponse,
                cancellationToken);

        /// <summary>
        /// 换掉一个通道的账号。会消耗供应商的一个账号，所以服务端卡得很严：
        /// 通道必须存在、且它复核过确实将尽才会放行。
        /// </summary>
        public static Task<ApiResult<RefreshResponse>> RefreshAsync(
            RefreshRequest request,
            string token,
            string userName,
            string fingerprint,
            CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(request, ApiJsonContext.Default.RefreshRequest);

            return SendAsync(
                HttpMethod.Post,
                "/refresh",
                body,
                token,
                userName,
                fingerprint,
                ApiJsonContext.Default.RefreshResponse,
                cancellationToken);
        }

        /// <summary>
        /// 按「主地址 → 备用地址」依次尝试。
        ///
        /// **只在该次尝试发生传输层失败（超时 / DNS / TLS / 连不上）时回退**：服务端已经答复的
        /// 业务错误（4xx / 5xx / 细粒度 code）不换端点——备用地址指向同一套后端，换过去结果一样；
        /// 而 /refresh 这类有副作用的请求更不能因为一个业务错误就换端点重放。
        ///
        /// 若未配置备用地址（或与主地址相同），则只尝试一次。
        /// </summary>
        private static async Task<ApiResult<T>> SendAsync<T>(
            HttpMethod method,
            string path,
            string body,
            string? token,
            string? userName,
            string? fingerprint,
            JsonTypeInfo<T> typeInfo,
            CancellationToken cancellationToken)
            where T : class
        {
            var endpoints = Endpoints();
            var last = ApiResult<T>.Fail(ApiErrorCodes.Transport, 0);

            for (var i = 0; i < endpoints.Count; i++)
            {
                var isLast = i == endpoints.Count - 1;
                var timeout = isLast ? BackupAttemptTimeout : PrimaryAttemptTimeout;

                last = await SendOnceAsync(
                    method, endpoints[i], path, body, token, userName, fingerprint, typeInfo, timeout, cancellationToken);

                // 成功，或服务端明确答复（业务错误）——都到此为止，不再尝试其它端点。
                if (last.Success || last.Code != ApiErrorCodes.Transport)
                {
                    return last;
                }

                if (!isLast)
                {
                    NodeDiagnostics.Log($"主 API 地址不可达（{endpoints[i]}），回退备用地址 {endpoints[i + 1]}。");
                }
            }

            return last;
        }

        /// <summary>请求端点序列：主地址在前；配置了且与主地址不同时才追加备用地址。</summary>
        private static IReadOnlyList<string> Endpoints()
        {
            var primary = AppSettings.Current.ApiBaseUrl.TrimEnd('/');
            var backup = AppSettings.Current.BackupApiBaseUrl;

            if (string.IsNullOrEmpty(backup)
                || string.Equals(backup, primary, StringComparison.OrdinalIgnoreCase))
            {
                return new[] { primary };
            }

            return new[] { primary, backup };
        }

        private static async Task<ApiResult<T>> SendOnceAsync<T>(
            HttpMethod method,
            string baseUrl,
            string path,
            string body,
            string? token,
            string? userName,
            string? fingerprint,
            JsonTypeInfo<T> typeInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            where T : class
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = ApiSigner.NewNonce();

            // GET 没有请求体，用「用户名\n指纹」参与签名：否则这两个头可以被随意替换。
            var signedBody = method == HttpMethod.Get ? $"{userName}\n{fingerprint}" : body;
            var canonical = ApiSigner.CanonicalString(method.Method, path, timestamp, nonce, signedBody);

            using var request = new HttpRequestMessage(method, baseUrl + path);
            AddHeader(request, "X-Client-Version", ApiCredentials.ClientVersion.ToString(CultureInfo.InvariantCulture));
            AddHeader(request, "X-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            AddHeader(request, "X-Nonce", nonce);
            AddHeader(request, "X-Signature", ApiSigner.ComputeSignature(canonical));

            if (token is not null)
            {
                AddHeader(request, "Authorization", "Bearer " + token);
            }

            if (userName is not null)
            {
                AddHeader(request, "X-Username", userName);
            }

            if (fingerprint is not null)
            {
                AddHeader(request, "X-Fingerprint", fingerprint);
            }

            if (body.Length > 0)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            // 单次尝试的超时交给链接令牌控制（HttpClient.Timeout 已设为无限）：
            // 这样才能给主 / 备地址不同的等待上限。
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(timeout);

            try
            {
                using var response = await Client.SendAsync(request, attemptCts.Token);
                var text = await response.Content.ReadAsStringAsync(attemptCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var data = JsonSerializer.Deserialize(text, typeInfo);
                    return data is null
                        ? ApiResult<T>.Fail(ApiErrorCodes.Transport, (int)response.StatusCode)
                        : ApiResult<T>.Ok(data, (int)response.StatusCode);
                }

                return ApiResult<T>.Fail(ReadErrorCode(text), (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方主动取消（例如页面离开），原样抛出，不当成端点故障。
                throw;
            }
            catch (Exception ex)
            {
                // 网络问题、DNS 失败、超时都归到这一类：调用方只按「拿不到」处理，
                // 由上层决定是否回退备用地址。但把具体异常类型与消息落盘，
                // 方便区分「域名解析不了 / 连不通 / TLS 失败 / 超时」。
                NodeDiagnostics.Log(
                    $"API 请求传输层异常（{method} {path} @ {baseUrl}）：{ex.GetType().Name} - {ex.Message}" +
                    (ex.InnerException is not null ? $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : string.Empty));
                return ApiResult<T>.Fail(ApiErrorCodes.Transport, 0);
            }
        }

        /// <summary>取服务端返回的细粒度 code；解析不出来就退回 TRANSPORT。</summary>
        private static string ReadErrorCode(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return ApiErrorCodes.Transport;
            }

            try
            {
                var error = JsonSerializer.Deserialize(responseText, ApiJsonContext.Default.ApiErrorResponse);
                return string.IsNullOrWhiteSpace(error?.Code) ? ApiErrorCodes.Transport : error!.Code;
            }
            catch (JsonException)
            {
                return ApiErrorCodes.Transport;
            }
        }

        private static void AddHeader(HttpRequestMessage request, string name, string value) =>
            request.Headers.TryAddWithoutValidation(name, value);

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                // **不能走系统代理**：连接状态下系统代理指向本机 Xray，API 请求会被送进内核。
                // 而且带代理时 ConnectCallback 拿到的 DnsEndPoint 是代理地址，
                // 将来做 IP 优选会直接失效 —— 所以这里必须显式关掉。
                UseProxy = false,

                // IP 优选的接入点：将来在这里设 ConnectCallback，
                // 用探测出的最优 Cloudflare 边缘 IP 直连（SNI 与 Host 仍是自定义域名，
                // 因此证书校验照常生效，不要关掉验证）。
                // handler.ConnectCallback = async (context, ct) => { ... };
            };

            // 超时改由每次尝试的链接令牌控制（主 / 备地址给不同上限），这里不再设固定超时。
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ApiCredentials.UserAgent);
            return client;
        }
    }
}
