using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RayShuttle.Models;
using RayShuttle.Services;
using Windows.ApplicationModel.DataTransfer;

namespace RayShuttle.Views
{
    /// <summary>
    /// 连接失败的详情弹窗。
    ///
    /// 目的是把「失败原因」从首页那一行红字里搬出来：
    /// 首页只留一句短结论，完整的分级说明、建议、内核原始输出都在这里，
    /// 并且可以**一键复制成一份脱敏的诊断报告**交给开发者。
    ///
    /// 用法是 <see cref="ShowAsync(Microsoft.UI.Xaml.XamlRoot, ConnectionFailure)"/>，
    /// 返回用户是否选择了「重试」。
    /// </summary>
    public sealed partial class ErrorDialog : ContentDialog
    {
        private readonly string _report;

        public ErrorDialog(ConnectionFailure failure)
        {
            ArgumentNullException.ThrowIfNull(failure);

            InitializeComponent();

            Title = failure.Title;
            KindLabel.Text = failure.KindText;
            SummaryLabel.Text = failure.Summary;

            if (string.IsNullOrWhiteSpace(failure.Suggestion))
            {
                SuggestionLabel.Visibility = Visibility.Collapsed;
            }
            else
            {
                SuggestionLabel.Text = failure.Suggestion;
            }

            if (string.IsNullOrWhiteSpace(failure.Detail))
            {
                // 没有原始细节就不占版面，也别让用户复制一份空报告时以为是坏了。
                DetailSection.Visibility = Visibility.Collapsed;
            }
            else
            {
                DetailText.Text = failure.Detail;
            }

            // 内核都不在的时候给个「重试」只是浪费用户时间。
            IsPrimaryButtonEnabled = failure.IsRetryable;
            DefaultButton = failure.IsRetryable ? ContentDialogButton.Primary : ContentDialogButton.Close;

            _report = DiagnosticReport.Build(failure);
        }

        /// <summary>弹出对话框。返回 true 表示用户点了「重试」。</summary>
        public static async Task<bool> ShowAsync(XamlRoot xamlRoot, ConnectionFailure failure)
        {
            ArgumentNullException.ThrowIfNull(xamlRoot);

            var dialog = new ErrorDialog(failure) { XamlRoot = xamlRoot };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private void OnCopyClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(_report);
                Clipboard.SetContent(package);

                CopyHintText.Visibility = Visibility.Visible;
                CopyButton.IsEnabled = false;
            }
            catch (Exception)
            {
                // 剪贴板被占用（另一个进程正锁着）时 SetContent 会抛。这时退回让用户手动选中复制——
                // DetailText 本来就是可选的文本，不必为此再弹一个错误框。
                CopyHintText.Text = "复制失败：剪贴板被占用，请手动选中上面的文本复制。";
                CopyHintText.Visibility = Visibility.Visible;
            }
        }
    }
}
