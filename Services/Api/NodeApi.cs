using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RayShuttle.Services.Provider;

namespace RayShuttle.Services.Api
{
    internal sealed record NodeApiResult(bool Success, byte[]? Content, string? Token, string Code)
    {
        public static NodeApiResult Fail(string code) => new(false, null, null, code);
    }

    /// <summary>
    /// 从云端取加密节点内容。
    ///
    /// 两处重试是**针对 KV 的最终一致性**，不是网络重试：
    ///   - `TOKEN_EXPIRED`：token 真的过期了 → 用本地加密保存的邀请码自动重新登录 → 再来一次；
    ///   - `TOKEN_INVALID`：可能是刚签发的 token 还没传播到这个机房 → 先短暂等一下重试，
    ///     再不行才作废旧会话重新登录。
    ///
    /// 全都失败就返回失败，由 `NodeRepository` 回落到本地文件（仅本地联调）；
    /// **不会把用户登出**。
    /// </summary>
    internal static class NodeApi
    {
        /// <summary>等 KV 传播的时间。取短一点：宁可快，也不要让用户干等。</summary>
        private static readonly TimeSpan PropagationDelay = TimeSpan.FromMilliseconds(1500);

        public static async Task<NodeApiResult> FetchAsync(CancellationToken cancellationToken)
        {
            var store = AccountStore.Current;
            if (!store.IsSignedIn)
            {
                return NodeApiResult.Fail("NOT_SIGNED_IN");
            }

            var token = await AuthService.Current.EnsureSessionAsync(cancellationToken);
            if (token is null)
            {
                return NodeApiResult.Fail("NO_SESSION");
            }

            var userName = store.UserName;
            if (userName is null)
            {
                return NodeApiResult.Fail("NOT_SIGNED_IN");
            }

            var fingerprint = DeviceFingerprint.Get();
            var result = await ApiClient.GetNodeAsync(token, userName, fingerprint, cancellationToken);
            if (result.Success && result.Data is not null)
            {
                // 服务端做了滑动续期就把新的过期时间存下来，下次少一次无效请求。
                if (result.Data.Renewed)
                {
                    await store.SaveSessionAsync(token, result.Data.ExpiresAt);
                }

                return new NodeApiResult(
                    true,
                    Encoding.ASCII.GetBytes(result.Data.Payload),
                    token,
                    "OK");
            }

            if (result.Code == ApiErrorCodes.TokenExpired)
            {
                await AuthService.Current.InvalidateSessionAsync();

                var refreshed = await AuthService.Current.EnsureSessionAsync(cancellationToken);
                return refreshed is null
                    ? NodeApiResult.Fail(ApiErrorCodes.TokenExpired)
                    : await FetchOnceAsync(refreshed, userName, fingerprint, cancellationToken);
            }

            if (result.Code == ApiErrorCodes.TokenInvalid)
            {
                // 先按「KV 还没传播完」处理，等一下用同一个 token 再试一次。
                await Task.Delay(PropagationDelay, cancellationToken);
                var retried = await FetchOnceAsync(token, userName, fingerprint, cancellationToken);
                if (retried.Success)
                {
                    return retried;
                }

                await AuthService.Current.InvalidateSessionAsync();

                var refreshed = await AuthService.Current.EnsureSessionAsync(cancellationToken);
                return refreshed is null
                    ? NodeApiResult.Fail(ApiErrorCodes.TokenInvalid)
                    : await FetchOnceAsync(refreshed, userName, fingerprint, cancellationToken);
            }

            return NodeApiResult.Fail(result.Code);
        }

        /// <summary>
        /// 换掉一个通道的账号。
        ///
        /// 失败时返回 null，调用方按「这个通道暂时换不了」处理——**不要**反复重试：
        /// 每次刷新都会消耗供应商的一个账号，重试太勤等于烧账号。
        /// </summary>
        public static async Task<ProviderSlotRef?> RefreshSlotAsync(
            string slot,
            long reportedBytes,
            CancellationToken cancellationToken)
        {
            var store = AccountStore.Current;
            if (!store.IsSignedIn)
            {
                return null;
            }

            var token = await AuthService.Current.EnsureSessionAsync(cancellationToken);
            var userName = store.UserName;
            if (token is null || userName is null)
            {
                return null;
            }

            var fingerprint = DeviceFingerprint.Get();
            var request = new RefreshRequest { Slot = slot, ReportedBytes = reportedBytes };

            var result = await ApiClient.RefreshAsync(request, token, userName, fingerprint, cancellationToken);

            // 会话类失败沿用 /node 的处理：过期就重登再来一次，无效先等 KV 传播。
            if (result.Code == ApiErrorCodes.TokenExpired)
            {
                await AuthService.Current.InvalidateSessionAsync();
                token = await AuthService.Current.EnsureSessionAsync(cancellationToken);
                if (token is null)
                {
                    return null;
                }

                result = await ApiClient.RefreshAsync(request, token, userName, fingerprint, cancellationToken);
            }
            else if (result.Code == ApiErrorCodes.TokenInvalid)
            {
                await Task.Delay(PropagationDelay, cancellationToken);
                result = await ApiClient.RefreshAsync(request, token, userName, fingerprint, cancellationToken);
            }

            if (!result.Success || result.Data is null)
            {
                return null;
            }

            var json = NodeFileCrypto.TryDecryptToJson(
                NodeSessionKey.Derive(token),
                Encoding.ASCII.GetBytes(result.Data.Payload));

            if (json is null)
            {
                return null;
            }

            try
            {
                var document = JsonDocument.Parse(json);
                using (document)
                {
                    var root = document.RootElement;
                    var newUuid = root.GetProperty("uuid").GetString();
                    var slotId = root.GetProperty("slot").GetString();

                    // 注册时间服务端没回，按本地时间记：它只用于「还剩多久到期」的粗判。
                    return string.IsNullOrEmpty(newUuid) || string.IsNullOrEmpty(slotId)
                        ? null
                        : new ProviderSlotRef
                        {
                            Slot = slotId!,
                            Uuid = newUuid!,
                            RegisteredAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static async Task<NodeApiResult> FetchOnceAsync(
            string token,
            string userName,
            string fingerprint,
            CancellationToken cancellationToken)
        {
            var result = await ApiClient.GetNodeAsync(token, userName, fingerprint, cancellationToken);

            return result.Success && result.Data is not null
                ? new NodeApiResult(true, Encoding.ASCII.GetBytes(result.Data.Payload), token, "OK")
                : NodeApiResult.Fail(result.Code);
        }
    }
}
