using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RayShuttle.Common;
using RayShuttle.Models;
using RayShuttle.Services;
using Windows.System;

namespace RayShuttle.Views
{
    /// <summary>
    /// 设置页。「账户」「服务器」是真实数据；「关闭窗口最小化到托盘」已接入实际行为，
    /// 其余旧版占位开关（连接 / 高级）已移除。
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>
        /// 初始化控件期间抑制回写。设置 SelectedIndex 会触发 SelectionChanged，
        /// 不加这个标记会在页面刚打开时就触发一次无意义的重新拉取。
        /// </summary>
        private bool _isInitializing = true;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private static AppSettings Settings => AppSettings.Current;

        private static MainWindow? Shell => (Application.Current as App)?.MainWindow;

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            EntranceAnimation.Run(
                HeaderBlock,
                AccountCard,
                ServerCard,
                GeneralCard,
                RoutingCard,
                DeveloperCard,
                AboutCard);

            await LoadAccountAsync();

            PopulateServerSettings();

            // 版本号读 AppInfo（取自 csproj 的 Version），不要硬编码，否则升级后这里会过期。
            VersionText.Text = $"版本 {AppInfo.Version}";

            _isInitializing = false;
        }

        /// <summary>开机自启的初始值取注册表**实际**状态，而非 settings.json——用户在别处禁用后两边会不一致。</summary>
        private async Task PopulateStartupAsync()
        {
            LaunchAtStartupSwitch.IsOn = StartupRegistration.IsEnabled;
            await Task.CompletedTask;
        }

        private async Task LoadAccountAsync()
        {
            AccountNameText.Text = AccountStore.Current.UserName ?? "--";

            // 使用中再次读取邀请码：这一步验证了「加密写入 → 解密读出」的往返是通的。
            var inviteCode = await AccountStore.Current.ReadInviteCodeAsync();
            InviteCodeText.Text = inviteCode is null ? "--" : InviteCode.Mask(inviteCode);
        }

        private void PopulateServerSettings()
        {
            ApiBaseUrlBox.Text = Settings.ApiBaseUrl;
            BackupApiBaseUrlBox.Text = Settings.BackupApiBaseUrl;
            AutoConnectSwitch.IsOn = Settings.AutoConnectOnLaunch;
            AutoReconnectSwitch.IsOn = Settings.AutoReconnect;
            NotificationsSwitch.IsOn = Settings.NotificationsEnabled;
            SoundSwitch.IsOn = Settings.PlayConnectionSound;
            TunModeSwitch.IsOn = Settings.TunMode;

            // 给 ToggleSwitch 赋值会触发 Toggled，_isInitializing 守卫会挡掉这次误触发。
            MinimizeToTraySwitch.IsOn = Settings.MinimizeToTray;
            _ = PopulateStartupAsync();

            // 分流板块：读取当前配置初始化单选与文本框（不会回写，只有点「保存」才落盘）。
            PopulateRoutingAsync();
        }

        /// <summary>把 <see cref="AppSettings.Current.Routing"/> 显示到分流板块的控件上（只读初始化，不回写）。</summary>
        private void PopulateRoutingAsync()
        {
            var routing = AppSettings.Current.Routing;

            foreach (RadioButton button in ModeButtons.Items)
            {
                if (button.Tag is string tag && string.Equals(tag, routing.Mode.ToString(), StringComparison.Ordinal))
                {
                    button.IsChecked = true;
                    break;
                }
            }

            DirectDomainsBox.Text = string.Join('\n', routing.DirectDomains);
            DirectAddressesBox.Text = string.Join('\n', routing.DirectAddresses);
            ProxyDomainsBox.Text = string.Join('\n', routing.ProxyDomains);

            SyncRoutingVisibility(routing.Mode);
        }

        private void OnRoutingModeChanged(object sender, SelectionChangedEventArgs e)
        {
            // 初始化期间由 PopulateRoutingAsync 直接调用 SyncRoutingVisibility，避免重复与误触发。
            if (_isInitializing)
            {
                return;
            }

            SyncRoutingVisibility(SelectedRoutingMode());
        }

        private void SyncRoutingVisibility(RoutingMode mode)
        {
            CustomPanel.Visibility = mode == RoutingMode.Custom ? Visibility.Visible : Visibility.Collapsed;
            GeoHint.Visibility = mode == RoutingMode.BypassMainland ? Visibility.Visible : Visibility.Collapsed;
        }

        private RoutingMode SelectedRoutingMode()
        {
            foreach (RadioButton button in ModeButtons.Items)
            {
                if (button.IsChecked == true && button.Tag is string tag
                    && Enum.TryParse<RoutingMode>(tag, out var parsed))
                {
                    return parsed;
                }
            }

            return RoutingMode.Global;
        }

        private static List<string> SplitRoutingLines(string text) =>
            text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

        private async void OnRoutingSaveClicked(object sender, RoutedEventArgs e)
        {
            var routing = new RoutingSettings
            {
                Mode = SelectedRoutingMode(),
                DirectDomains = SplitRoutingLines(DirectDomainsBox.Text),
                DirectAddresses = SplitRoutingLines(DirectAddressesBox.Text),
                ProxyDomains = SplitRoutingLines(ProxyDomainsBox.Text)
            };

            await AppSettings.Current.SetRoutingAsync(routing);

            // 改了分流要断开重连才生效：当前正连着就提示用户一下。
            if (VpnConnectionService.Current.Status is VpnStatus.Connected or VpnStatus.Connecting)
            {
                ShowRoutingMessage("已保存。断开重连后生效。");
            }
            else
            {
                ShowRoutingMessage("已保存。");
            }
        }

        private void ShowRoutingMessage(string text)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "分流设置",
                Content = text,
                CloseButtonText = "好"
            };
            _ = dialog.ShowAsync();
        }

        private async void OnApiBaseUrlLostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            var value = ApiBaseUrlBox.Text.Trim();

            // 地址不合法就不写，也不重新拉取：宁可让用户改回来，也不要把设置存坏。
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Host != "localhost"))
            {
                ApiBaseUrlBox.Text = Settings.ApiBaseUrl;
                return;
            }

            if (value == Settings.ApiBaseUrl)
            {
                return;
            }

            await Settings.SetApiBaseUrlAsync(value);
            await ReloadNodesAsync();
        }

        /// <summary>
        /// 备用地址失焦即校验并保存。它与主地址一样必须是 https（或本机），
        /// 但**允许留空**——留空表示不启用备用回退。改备用地址不需要重拉节点。
        /// </summary>
        private async void OnBackupApiBaseUrlLostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            var value = BackupApiBaseUrlBox.Text.Trim();

            // 空串是合法值（关闭备用）；非空时必须是 https 或 localhost。
            if (value.Length > 0
                && (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttps && uri.Host != "localhost")))
            {
                BackupApiBaseUrlBox.Text = Settings.BackupApiBaseUrl;
                return;
            }

            if (value == Settings.BackupApiBaseUrl)
            {
                return;
            }

            await Settings.SetBackupApiBaseUrlAsync(value);
        }

        private async void OnMinimizeToTrayToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            await Settings.SetMinimizeToTrayAsync(MinimizeToTraySwitch.IsOn);
        }

        private async void OnLaunchAtStartupToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            // 写注册表并回读校验：被组策略 / 安全软件静默拦掉时，开关会回弹到真实状态，
            // 而不是骗用户「已开启」。（详见 StartupRegistration。）
            if (!StartupRegistration.Apply(LaunchAtStartupSwitch.IsOn))
            {
                LaunchAtStartupSwitch.IsOn = StartupRegistration.IsEnabled;
            }
        }

        private async void OnAutoConnectToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            await Settings.SetAutoConnectOnLaunchAsync(AutoConnectSwitch.IsOn);
        }

        private async void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            await Settings.SetAutoReconnectAsync(AutoReconnectSwitch.IsOn);
        }

        private async void OnNotificationsToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            await Settings.SetNotificationsEnabledAsync(NotificationsSwitch.IsOn);
        }

        private async void OnSoundToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            await Settings.SetPlayConnectionSoundAsync(SoundSwitch.IsOn);
        }

        private async void OnTunModeToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            await Settings.SetTunModeAsync(TunModeSwitch.IsOn);

            // 接管方式在「连接开始那一刻」确定：当前连着就提示重连（与分流保存同一语义）。
            if (VpnConnectionService.Current.Status is VpnStatus.Connected or VpnStatus.Connecting)
            {
                ShowRoutingMessage("已保存。断开重连后生效。");
            }
        }

        /// <summary>设置改了就立刻重新拉取，否则用户看不出任何变化。</summary>
        private static async Task ReloadNodesAsync()
        {
            if ((Application.Current as App)?.MainWindow is { } shell)
            {
                await shell.ReloadNodesAsync();
            }
        }

        /// <summary>打开外部链接（开发者主页 / 开源仓库）。地址写在按钮的 Tag 上。</summary>
        private async void OnLinkClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string url)
            {
                return;
            }

            try
            {
                await Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception)
            {
                // 系统没有可用浏览器等情况：忽略即可，不能把设置页搞崩。
            }
        }

        private async void OnSignOutClicked(object sender, RoutedEventArgs e)
        {
            await AccountStore.Current.SignOutAsync();

            // 走与首次启动完全相同的判定入口，避免这里另写一套状态转移。
            if (Shell is { } shell)
            {
                await shell.RefreshOnboardingStepAsync();
            }
        }

        private async void OnRevokeAgreementClicked(object sender, RoutedEventArgs e)
        {
            // 撤回会清掉本地凭据、并把用户踢回协议页，属于不可撤销的高影响操作，必须先确认。
            var dialog = new ContentDialog
            {
                // WinUI 3 的 ContentDialog 必须显式指定 XamlRoot，否则 ShowAsync 会抛异常。
                XamlRoot = XamlRoot,
                Title = "撤回用户协议同意",
                Content = "撤回后，本软件将清除本地保存的账号凭据，并退回用户协议页面。\n\n"
                          + "在您重新阅读并同意《用户协议》、且重新登录之前，无法继续使用本软件及本服务。",
                PrimaryButtonText = "撤回同意",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await AccountStore.Current.RevokePrivacyAsync();

            // 与「退出登录」共用同一个判定入口：这里 PrivacyAccepted 已为 false，
            // 于是 RefreshOnboardingStepAsync 会把用户退回协议页而不是登录页。
            if (Shell is { } shell)
            {
                await shell.RefreshOnboardingStepAsync();
            }
        }
    }
}
