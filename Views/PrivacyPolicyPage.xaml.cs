using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RayShuttle.Common;
using RayShuttle.Services;

namespace RayShuttle.Views
{
    /// <summary>
    /// 首次运行的第一步：必须同意隐私协议才能继续。
    /// 同意记录会落盘，之后不再出现。
    /// </summary>
    public sealed partial class PrivacyPolicyPage : Page
    {
        public PrivacyPolicyPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private static MainWindow? Shell => (Application.Current as App)?.MainWindow;

        private void OnLoaded(object sender, RoutedEventArgs e) =>
            EntranceAnimation.Run(HeaderBlock, ContentBlock);

        private void OnAgreeChanged(object sender, RoutedEventArgs e)
        {
            var agreed = AgreeCheckBox.IsChecked == true;

            AcceptButton.IsEnabled = agreed;
            HintText.Visibility = agreed ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnDeclineClicked(object sender, RoutedEventArgs e)
        {
            // 「不同意就无法使用」：直接退出应用。
            Application.Current.Exit();
        }

        private void OnAcceptClicked(object sender, RoutedEventArgs e)
        {
            // 落盘后再推进流程：即使此刻崩溃，也不会重复弹协议。
            _ = AcceptAsync();
        }

        private async System.Threading.Tasks.Task AcceptAsync()
        {
            AcceptButton.IsEnabled = false;
            await AccountStore.Current.AcceptPrivacyAsync();
            await (Shell?.RefreshOnboardingStepAsync() ?? System.Threading.Tasks.Task.CompletedTask);
        }
    }
}
