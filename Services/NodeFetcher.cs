using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>一个节点源。</summary>
    public sealed record NodeSource(string Name, string BaseUrl)
    {
        public string Host =>
            Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : BaseUrl;

        /// <summary>界面上展示的名称，带上域名便于排查。</summary>
        public string DisplayName => $"{Name}（{Host}）";
    }

    /// <summary>拉取失败的性质。上层据此决定该不该把用户登出。</summary>
    public enum NodeFetchFailure
    {
        None,

        /// <summary>所有源都明确表示该文件不存在 —— 说明这个账号根本不存在。</summary>
        NotFound,

        /// <summary>网络或服务端问题，无法判断账号是否存在。</summary>
        Transport
    }

    /// <summary>拉取结果。失败时 <see cref="Message"/> 是可以直接展示给用户的原因。</summary>
    public sealed record NodeFetchResult(
        bool Success,
        byte[]? Content,
        string Message,
        NodeSource? Source,
        NodeFetchFailure Failure);

    /// <summary>
    /// 从远端拉取账号的加密节点文件。
    ///
    /// 约定：**文件名即用户名**，放在源的根路径下，例如
    ///     https://rayshuttle.netlify.app/admin.txt
    ///
    /// 拉取顺序由用户在设置里选择（默认主源优先）：
    /// 先按当前源重试 3 次，仍失败则换另一个源再试 3 次，都失败才报错。
    /// 对**确定性失败**（404 / 403 之类）不重试——重试一个不存在的文件只是白白拖慢启动。
    /// </summary>
    public static class NodeFetcher
    {
        public const int AttemptsPerSource = 3;

        private static readonly NodeSource PrimarySource =
            new("主源", "https://rayshuttle.netlify.app/");

        private static readonly NodeSource FallbackSource =
            new("备用源", "https://raw.githubusercontent.com/JiGuroLGC/RayShuttle_Config_Reserve/main/");

        /// <summary>单次请求超时。取值偏短：宁可快点换源，也不要让用户干等。</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(400);

        private static readonly HttpClient Client = CreateClient();

        /// <summary>全部源，按固定顺序（主源 → 备用源）。设置界面的选项列表用它，保证顺序稳定。</summary>
        public static IReadOnlyList<NodeSource> All => new[] { PrimarySource, FallbackSource };

        /// <summary>按设置里的优先级排序，索引 0 是优先使用的源。</summary>
        public static IReadOnlyList<NodeSource> OrderBy(NodeSourcePreference preference) =>
            preference == NodeSourcePreference.Fallback
                ? new[] { FallbackSource, PrimarySource }
                : new[] { PrimarySource, FallbackSource };

        /// <summary>取优先级最高的那个源，用于界面上「当前优先使用」的展示。</summary>
        public static NodeSource Preferred(NodeSourcePreference preference) =>
            preference == NodeSourcePreference.Fallback ? FallbackSource : PrimarySource;

        public static async Task<NodeFetchResult> FetchAsync(
            string userName,
            NodeSourcePreference preference,
            CancellationToken cancellationToken)
        {
            var failures = new List<string>();

            foreach (var source in OrderBy(preference))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await FetchFromSourceAsync(BuildUrl(source, userName), cancellationToken);

                if (result.Success)
                {
                    return new NodeFetchResult(
                        true, result.Content, $"已从{source.Name}获取", source, NodeFetchFailure.None);
                }

                if (result.Failure == NodeFetchFailure.NotFound)
                {
                    // 「文件不存在」是确定性结论，足以说明问题：
                    // 本源不重试（重试循环里已跳过），也不必再试另一个源。
                    // 之前会继续走备用源，在备用源不可达时要多等十几秒才报错，得不偿失。
                    return new NodeFetchResult(
                        false, null, "该账号的文件不存在", source, NodeFetchFailure.NotFound);
                }

                failures.Add($"{source.Host}（{result.Message}）");
            }

            // 走到这里说明每个源都是网络/服务端问题：确实需要「每源 3 次」的韧性。
            return new NodeFetchResult(false, null, string.Join("；", failures), null, NodeFetchFailure.Transport);
        }

        private static string BuildUrl(NodeSource source, string userName) =>
            source.BaseUrl + Uri.EscapeDataString(userName) + ".txt";

        private static async Task<NodeFetchResult> FetchFromSourceAsync(string url, CancellationToken cancellationToken)
        {
            var delay = FirstRetryDelay;
            var lastFailure = "未发起请求";

            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var response = await Client.GetAsync(url, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        if (content.Length > 0)
                        {
                            return new NodeFetchResult(true, content, "成功", null, NodeFetchFailure.None);
                        }

                        // 空内容多半是 CDN 抖动，值得重试。
                        lastFailure = "返回内容为空";
                    }
                    else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        // 文件确实不存在，重试没有意义，直接换源。
                        return new NodeFetchResult(false, null, "该账号的文件不存在", null, NodeFetchFailure.NotFound);
                    }
                    else if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        lastFailure = $"服务端暂时不可用（{(int)response.StatusCode}）";
                    }
                    else
                    {
                        return new NodeFetchResult(false, null, $"请求被拒绝（{(int)response.StatusCode}）", null, NodeFetchFailure.Transport);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastFailure = exception is TaskCanceledException
                        ? "请求超时"
                        : $"网络错误（{exception.GetType().Name}）";
                }

                if (attempt < AttemptsPerSource)
                {
                    await Task.Delay(delay, cancellationToken);
                    delay += delay; // 指数退避
                }
            }

            return new NodeFetchResult(
                false, null, $"{AttemptsPerSource} 次尝试均失败：{lastFailure}", null, NodeFetchFailure.Transport);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = RequestTimeout };

            // 部分 CDN 会拒绝没有 User-Agent 的请求，必须带上。
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RayShuttle/0.1");
            return client;
        }
    }
}
