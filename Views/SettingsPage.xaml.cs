using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RayShuttle.Common;
using RayShuttle.Services;

namespace RayShuttle.Views
{
    /// <summary>
    /// 设置页。「账户」与「节点源优先级」是真实数据，其余开关尚未绑定到实际配置。
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
                NodeCard,
                ConnectionCard,
                GeneralCard,
                AdvancedCard,
                AboutCard);

            await LoadAccountAsync();

            PopulateNodeSources();
            _isInitializing = false;
        }

        private async Task LoadAccountAsync()
        {
            AccountNameText.Text = AccountStore.Current.UserName ?? "--";

            // 使用中再次读取邀请码：这一步验证了「加密写入 → 解密读出」的往返是通的。
            var inviteCode = await AccountStore.Current.ReadInviteCodeAsync();
            InviteCodeText.Text = inviteCode is null ? "--" : InviteCode.Mask(inviteCode);
        }

        private void PopulateNodeSources()
        {
            NodeSourceBox.Items.Clear();

            foreach (var source in NodeFetcher.All)
            {
                NodeSourceBox.Items.Add(source.DisplayName);
            }

            NodeSourceBox.SelectedIndex = Settings.PreferredNodeSource == NodeSourcePreference.Fallback
                ? 1
                : 0;

            UpdateNodeSourceHint();
        }

        private void UpdateNodeSourceHint()
        {
            var preferred = NodeFetcher.Preferred(Settings.PreferredNodeSource);
            NodeSourceHintText.Text =
                $"优先使用{preferred.Name}，{NodeFetcher.AttemptsPerSource} 次失败后自动切换另一个源";
        }

        private async void OnNodeSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || NodeSourceBox.SelectedIndex < 0)
            {
                return;
            }

            var preference = NodeSourceBox.SelectedIndex == 1
                ? NodeSourcePreference.Fallback
                : NodeSourcePreference.Primary;

            if (preference == Settings.PreferredNodeSource)
            {
                return;
            }

            await Settings.SetPreferredNodeSourceAsync(preference);
            UpdateNodeSourceHint();

            // 立刻按新优先级重新拉取，否则用户改完看不出任何变化。
            if (Shell is { } shell)
            {
                await shell.ReloadNodesAsync();
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
    }
}
