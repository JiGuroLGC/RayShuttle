using System;
using System.Threading;
using System.Threading.Tasks;

namespace RayShuttle.Services.Api
{
    /// <summary>登录结果。<see cref="Code"/> 只用于内部流程判断，不要展示给用户。</summary>
    internal sealed record AuthResult(bool Success, string Code)
    {
        public static AuthResult Ok() => new(true, "OK");

        public static AuthResult Fail(string code) => new(false, code);
    }

    /// <summary>
    /// 云端账号流程：登录换 token、保存会话、token 失效时自动重登。
    ///
    /// 两条刻意的设计：
    /// - **重登失败不登出用户**（与 `NodeRepository` 的既有约定一致）：
    ///   网络抖动证明不了凭据有问题，把用户踢回登录页只会让人莫名其妙。
    ///   失败后进入冷却期，避免每次刷新节点都打一次登录请求。
    /// - 登录全程串行化（<see cref="_gate"/>）：多个入口同时发现 token 过期时，
    ///   只应发生一次重登。
    /// </summary>
    internal sealed class AuthService
    {
        /// <summary>重登失败后的冷却时间。</summary>
        private static readonly TimeSpan ReloginCooldown = TimeSpan.FromMinutes(5);

        /// <summary>剩余有效期小于这个值就当作已过期，避免刚好卡在边界上。</summary>
        private const int ExpiryMarginSeconds = 60;

        public static AuthService Current { get; } = new();

        private readonly SemaphoreSlim _gate = new(1, 1);

        private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

        private AuthService()
        {
        }

        /// <summary>
        /// 登录页用：用「用户名 + 邀请码 + 设备指纹」换 token，成功后写入本地（邀请码与 token 均加密保存）。
        ///
        /// 注意**不自动注册**：账号由发放方预建，客户端自动注册等于开放一个用户名枚举入口。
        /// </summary>
        public async Task<AuthResult> SignInAsync(
            string userName,
            string inviteCode,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var normalized = InviteCode.Normalize(inviteCode);
                var result = await LoginCoreAsync(userName, normalized, cancellationToken);

                if (!result.Success)
                {
                    return result;
                }

                // 登录态与会话分两次落盘：前者是「这台机器上有这个账号」，后者是「当前有有效 token」。
                await AccountStore.Current.SignInAsync(userName, normalized);
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 取一个可用的 token；本地没有或已过期时，用本地加密保存的邀请码自动重新登录一次。
        /// 返回 null 表示拿不到（未登录、冷却中、或重登失败）——调用方按「拿不到节点」处理，
        /// **不要**据此登出用户。
        /// </summary>
        public async Task<string?> EnsureSessionAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var store = AccountStore.Current;
                var session = await store.ReadSessionAsync();

                if (session.Token is not null
                    && session.ExpiresAtUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ExpiryMarginSeconds)
                {
                    return session.Token;
                }

                if (DateTimeOffset.UtcNow < _cooldownUntil)
                {
                    return null;
                }

                var userName = store.UserName;
                var inviteCode = await store.ReadInviteCodeAsync();
                if (userName is null || inviteCode is null)
                {
                    return null;
                }

                var result = await LoginCoreAsync(userName, inviteCode, cancellationToken);
                if (!result.Success)
                {
                    _cooldownUntil = DateTimeOffset.UtcNow.Add(ReloginCooldown);
                    return null;
                }

                return (await store.ReadSessionAsync()).Token;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>作废本地会话，让下一次 <see cref="EnsureSessionAsync"/> 一定去重新登录。</summary>
        public async Task InvalidateSessionAsync()
        {
            await _gate.WaitAsync();
            try
            {
                await AccountStore.Current.ClearSessionAsync();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<AuthResult> LoginCoreAsync(
            string userName,
            string inviteCode,
            CancellationToken cancellationToken)
        {
            var request = new CredentialRequest
            {
                // 用户名统一小写：Worker 端的 KV 键也是小写化的，否则 alice 与 Alice 是两个账号。
                Username = NodeKeyDerivation.NormalizeUserName(userName),
                InviteCode = inviteCode,
                Fingerprint = DeviceFingerprint.Get(),
            };

            var result = await ApiClient.LoginAsync(request, cancellationToken);
            if (!result.Success || result.Data is null)
            {
                return AuthResult.Fail(result.Code);
            }

            await AccountStore.Current.SaveSessionAsync(result.Data.Token, result.Data.ExpiresAt);
            return AuthResult.Ok();
        }
    }
}
