using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RayShuttle.Common;
using RayShuttle.Services;
using Windows.System;

namespace RayShuttle.Views
{
    /// <summary>
    /// 首次运行的第二步：用户名 + 邀请码。
    ///
    /// **当前没有服务端**，邀请码只做本地格式校验，任意符合格式的码都能通过。
    /// 接入服务端后，把校验换成请求结果即可，界面逻辑不用动。
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
                // 邀请码在写盘前就会被加密，明文不落地。
                await AccountStore.Current.SignInAsync(userName, inviteCode);
                await (Shell?.RefreshOnboardingStepAsync() ?? Task.CompletedTask);
            }
            catch (Exception)
            {
                ShowError("保存登录信息失败，请重试。");
                SignInButton.IsEnabled = true;
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
    }
}
