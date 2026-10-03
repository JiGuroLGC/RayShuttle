using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RayShuttle.Services
{
    /// <summary>落盘的状态。**邀请码与 token 都只以密文形式出现在这里。**</summary>
    internal sealed class AccountState
    {
        public bool PrivacyAccepted { get; set; }

        public string? UserName { get; set; }

        /// <summary>DPAPI 密文（Base64），见 <see cref="SecretProtector"/>。</summary>
        public string? ProtectedInviteCode { get; set; }

        /// <summary>云端长期 token 的 DPAPI 密文（Base64）。</summary>
        public string? ProtectedSessionToken { get; set; }

        /// <summary>
        /// token 的过期时间（Unix 秒）。不算机密，明文存便于判断是否要重新登录，
        /// 也省掉一次 DPAPI 解密。
        /// </summary>
        public long SessionExpiresAtUnix { get; set; }
    }

    /// <summary>
    /// 用源生成器而不是反射式序列化：Release 构建开了 PublishTrimmed，
    /// 反射式 JsonSerializer 会把属性裁掉，导致存出一串空对象。
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AccountState))]
    internal sealed partial class AccountStateJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// 账户状态的读写。
    ///
    /// 存放位置是 %LOCALAPPDATA%\RayShuttle，而不是 ApplicationData.Current：
    /// 后者在非打包运行（dotnet run）时会直接抛异常，而这个应用两种方式都要能跑。
    /// </summary>
    public sealed class AccountStore
    {
        private static readonly string StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RayShuttle");

        private static readonly string StateFilePath = Path.Combine(StateDirectory, "account.json");

        /// <summary>单窗口应用，用单例即可。若将来支持多账户应改成注入。</summary>
        public static AccountStore Current { get; } = new();

        private AccountState _state = new();

        /// <summary>
        /// 派生一次要跑 60 万次 PBKDF2（数百毫秒），因此缓存；
        /// 登录态一变立刻作废，避免用旧账号的密钥去解密新的节点文件。
        /// </summary>
        private byte[]? _cachedNodeKey;

        private AccountStore()
        {
        }

        public bool PrivacyAccepted => _state.PrivacyAccepted;

        public string? UserName => _state.UserName;

        public bool IsSignedIn =>
            !string.IsNullOrWhiteSpace(_state.UserName) && _state.ProtectedInviteCode is not null;

        /// <summary>本地是否保存了云端 token（不判断过期，过期由 AuthService 处理）。</summary>
        public bool HasSession => _state.ProtectedSessionToken is not null;

        public async Task LoadAsync()
        {
            _cachedNodeKey = null;

            try
            {
                if (!File.Exists(StateFilePath))
                {
                    return;
                }

                var json = await File.ReadAllTextAsync(StateFilePath);
                _state = JsonSerializer.Deserialize(json, AccountStateJsonContext.Default.AccountState)
                    ?? new AccountState();
            }
            catch
            {
                // 文件损坏、格式变更都按全新用户处理，绝不能让应用起不来。
                _state = new AccountState();
            }
        }

        public async Task AcceptPrivacyAsync()
        {
            _state.PrivacyAccepted = true;
            await SaveAsync();
        }

        /// <summary>写入前先加密，明文不会落盘。</summary>
        public async Task SignInAsync(string userName, string inviteCode)
        {
            _cachedNodeKey = null;
            _state.UserName = userName;
            _state.ProtectedInviteCode = await SecretProtector.ProtectAsync(inviteCode);
            await SaveAsync();
        }

        /// <summary>只清登录态，保留隐私协议的同意记录。会话 token 一并作废。</summary>
        public async Task SignOutAsync()
        {
            _cachedNodeKey = null;
            _state.UserName = null;
            _state.ProtectedInviteCode = null;
            _state.ProtectedSessionToken = null;
            _state.SessionExpiresAtUnix = 0;
            await SaveAsync();
        }

        /// <summary>
        /// 撤回对用户协议的同意。
        ///
        /// 与 <see cref="SignOutAsync"/> 的区别在于这里把 PrivacyAccepted 也清掉了，
        /// 于是 <c>MainWindow.RefreshOnboardingStepAsync</c> 会把用户退回协议页而不是登录页，
        /// 对应协议 9.9 条「撤回后不能继续使用本软件及本服务」。
        ///
        /// 凭据一并清空：同意既然已撤回，本地就没有继续持有邀请码密文的理由。
        /// 若将来接入了设备绑定，这里还应触发一次解绑请求，让服务端同步删除绑定记录。
        /// </summary>
        public async Task RevokePrivacyAsync()
        {
            _cachedNodeKey = null;
            _state.PrivacyAccepted = false;
            _state.UserName = null;
            _state.ProtectedInviteCode = null;
            _state.ProtectedSessionToken = null;
            _state.SessionExpiresAtUnix = 0;
            await SaveAsync();
        }

        /// <summary>
        /// 保存云端 token。明文不落盘，走与邀请码同一套 DPAPI 保护。
        /// </summary>
        public async Task SaveSessionAsync(string token, long expiresAtUnix)
        {
            _state.ProtectedSessionToken = await SecretProtector.ProtectAsync(token);
            _state.SessionExpiresAtUnix = expiresAtUnix;
            await SaveAsync();
        }

        /// <summary>
        /// 读取当前会话。token 解密失败（换用户 / 换机器 / 数据损坏）时按「没有会话」处理，
        /// 调用方会去重新登录。
        /// </summary>
        public async Task<(string? Token, long ExpiresAtUnix)> ReadSessionAsync()
        {
            if (_state.ProtectedSessionToken is null)
            {
                return (null, 0);
            }

            var token = await SecretProtector.UnprotectAsync(_state.ProtectedSessionToken);
            return token is null ? (null, 0) : (token, _state.SessionExpiresAtUnix);
        }

        /// <summary>作废会话（token 失效或退出登录时调用）。</summary>
        public async Task ClearSessionAsync()
        {
            _state.ProtectedSessionToken = null;
            _state.SessionExpiresAtUnix = 0;
            await SaveAsync();
        }

        /// <summary>使用过程中再次读取邀请码（设置页展示、后续接口鉴权）。</summary>
        public async Task<string?> ReadInviteCodeAsync() =>
            _state.ProtectedInviteCode is null
                ? null
                : await SecretProtector.UnprotectAsync(_state.ProtectedInviteCode);

        /// <summary>
        /// 取节点文件密钥——这就是「程序想取 key 时随时能取到」的入口。
        /// 未登录、或邀请码解密失败时返回 null。
        /// </summary>
        public async Task<byte[]?> DeriveNodeKeyAsync()
        {
            if (_cachedNodeKey is not null)
            {
                return _cachedNodeKey;
            }

            var inviteCode = await ReadInviteCodeAsync();
            if (inviteCode is null || string.IsNullOrWhiteSpace(_state.UserName))
            {
                return null;
            }

            _cachedNodeKey = NodeKeyDerivation.DeriveKey(_state.UserName, inviteCode);
            return _cachedNodeKey;
        }

        private async Task SaveAsync()
        {
            Directory.CreateDirectory(StateDirectory);
            var json = JsonSerializer.Serialize(_state, AccountStateJsonContext.Default.AccountState);
            await File.WriteAllTextAsync(StateFilePath, json);
        }
    }
}
