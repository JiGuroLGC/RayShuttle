using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RayShuttle.Common;
using RayShuttle.Services;
using RayShuttle.Services.Api;
using Windows.System;

namespace RayShuttle.Views
{
    /// <summary>
    /// 首次运行的第二步：用户名 + 邀请码。
    ///
    /// 提交后走云端校验（<see cref="AuthService"/>）：服务端校验邀请码、账号状态与设备指纹，
    /// 通过后才写本地状态。失败文案刻意统一，不区分「账号不存在」与「邀请码错误」。
    /// </summary>
    public sealed partial class LoginPage : Page
    {
        private const int UserNameMinLength = 2;
        private const int UserNameMaxLength = 10;

        public LoginPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private static MainWindow? Shell => (Application.Current as App)?.MainWindow;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EntranceAnimation.Run(HeaderBlock, ContentBlock);
            UserNameBox.Focus(FocusState.Programmatic);
        }

        private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            OnSignInClicked(sender, e);
        }

        private void OnSignInClicked(object sender, RoutedEventArgs e)
        {
            var userName = UserNameBox.Text.Trim();
            var inviteCode = InviteCode.Normalize(InviteCodeBox.Password);

            if (!Validate(userName, inviteCode, out var message))
            {
                ShowError(message);
                return;
            }

            _ = SignInAsync(userName, inviteCode);
        }

        private static bool Validate(string userName, string inviteCode, out string message)
        {
            if (userName.Length < UserNameMinLength || userName.Length > UserNameMaxLength)
            {
                message = $"用户名需要 {UserNameMinLength}–{UserNameMaxLength} 个字符。";
                return false;
            }

            if (!IsValidUserName(userName))
            {
                message = "用户名只能使用字母、数字与下划线。";
                return false;
            }

            if (inviteCode.Length == 0)
            {
                message = "请输入邀请码。";
                return false;
            }

            if (!InviteCode.IsWellFormed(inviteCode))
            {
                message = $"邀请码应为 {InviteCode.RequiredLength} 位数字或大写字母。";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private static bool IsValidUserName(string userName)
        {
            foreach (var character in userName)
            {
                var allowed = (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= 'a' && character <= 'z')
                    || character == '_';

                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        private async Task SignInAsync(string userName, string inviteCode)
        {
            HideError();
            SignInButton.IsEnabled = false;

            try
            {
                var result = await AuthService.Current.SignInAsync(userName, inviteCode);

                if (!result.Success)
                {
                    ShowError(DescribeFailure(result.Code));
                    SignInButton.IsEnabled = true;
                    return;
                }

                await (Shell?.RefreshOnboardingStepAsync() ?? Task.CompletedTask);
            }
            catch (Exception)
            {
                ShowError("登录失败，请检查网络后重试。");
                SignInButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 把服务端返回的细粒度 code 翻成给用户看的话。
        ///
        /// 「用户名或邀请码不正确」是刻意合并的两种失败：区分「账号不存在」与
        /// 「邀请码错误」等于告诉外人某个用户名是否已注册。
        /// </summary>
        private static string DescribeFailure(string code) => code switch
        {
            ApiErrorCodes.FingerprintMismatch => "该账号已绑定其它设备，无法在本机登录。",
            ApiErrorCodes.AccountLocked => "账号因多次失败已被临时锁定，请稍后再试。",
            ApiErrorCodes.AccountDisabled => "账号已被停用，请联系发放方。",
            ApiErrorCodes.Transport => "无法连接服务器，请检查网络后重试。",
            _ => "用户名或邀请码不正确。",
        };

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
    }
}
