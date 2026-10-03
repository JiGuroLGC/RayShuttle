using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RayShuttle.Common;
using RayShuttle.Models;
using RayShuttle.Services;
using Windows.Graphics;

namespace RayShuttle.Interop
{
    /// <summary>
    /// 托盘右键菜单：一块品牌风格的玻璃卡片，取代 Win32 那种老式系统弹出菜单。
    ///
    /// **为什么做成独立的 Window 而不是 MenuFlyout**：主窗口可能已经缩到托盘（被隐藏），
    /// 隐藏的窗口弹不出 Flyout；托盘图标本身又只有一个「仅消息」窗口，承载不了 XAML。
    /// 所以每次右键新建一个窗口，用完即弃，失焦自动关闭。
    /// </summary>
    public sealed partial class TrayMenuWindow : Window
    {
        /// <summary>菜单尺寸（DIP）。高度对应 XAML 里固定行高之和，**改行高必须同步改这里**。</summary>
        private const int MenuWidth = 216;
        // 高度 = 各固定行高之和(约 218) + Border 自身上下 Padding(12)。原值 224 漏算 Border Padding，
        // 会让卡片比客户区矮而被裁掉底部「退出」；且 ShowAtIcon 里必须让客户区与此值一致，否则露出白边。
        private const int MenuHeight = 232;

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_DONOTROUND = 1;
        // 卡片底色 PopupSurfaceBrush = #FF101A2C 转成 COLORREF(0x00BBGGRR) 的值。
        private const int MenuBorderColor = unchecked((int)0x002C1A10);

        // Win32 样式：WS_POPUP 彻底去掉非客户区，白边的最大嫌疑（1px 系统描边）就没了。
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_THICKFRAME = 0x00040000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_MINIMIZEBOX = 0x00020000;
        private const uint WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);

        [DllImport("user32")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32")]
        private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

        [DllImport("gdi32")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        [DllImport("gdi32")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("dwmapi")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        [DllImport("dwmapi")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        private bool _closing;

        public event EventHandler? ConnectRequested;
        public event EventHandler? OpenRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;

        public TrayMenuWindow()
        {
            InitializeComponent();

            // 卡片不钉死尺寸：根 Grid（见 XAML）默认铺满整个客户区，卡片在其内 Stretch 铺满。
            // 客户区多大，深色内容就铺多大，从根上消除「卡片比窗口小一圈、圆角弧缝里
            // 露出窗口默认浅色背景（白边）」这一类问题；窗口尺寸由 ShowAtIcon 的
            // ResizeClient(MenuWidth, MenuHeight) 唯一决定。
            //
            // 白边的两个来源（根因分析见 XAML 头注释）：
            // ① 内容根元素未盖住的区域会透出 XAML island 的系统主题色（浅色主题 = 白）
            //    —— 已由根 Grid 深色铺满兜底；
            // ② 系统在无边框窗口上画的 1px 非客户区边（部分 Win11 下 DWM_COLOR_NONE 不生效，
            //    呈白色）—— 见 ApplyRoundedCorners 里把该边设成与卡片同色。

            AppWindow.IsShownInSwitchers = false;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 白边用「三重保险」一次堵死（此前多轮修改卡片都无效，因为白边根本不在
            // XAML 内容里，而在非客户区 / 窗口形状上）：
            // ① WS_POPUP 彻底消灭非客户区 —— 1px 系统描边的最大嫌疑；
            // ② 关闭 DWM 系统圆角 —— 形状只保留一个权威来源；
            // ③ ShowAtIcon 里用 SetWindowRgn 把整窗直接裁成圆角矩形 —— region 之外的
            //    像素一概不渲染，「把弹窗改成圆角」本身，白边物理上不存在。
            ApplyFramelessStyle(handle);
            DisableDwmRoundAndBorder(handle);

            Activated += OnActivated;
            RefreshState();
        }

        /// <summary>
        /// 把窗口改成 WS_POPUP 弹窗样式：完全没有非客户区——没有系统边框，就没有那圈 1px
        /// 的白边来源。<c>SetBorderAndTitleBar(false,false)</c> 在部分 Win11 上仍会留下非客户区
        /// 描边；<c>DWMWA_BORDER_COLOR</c> 只对带边框的窗口生效，这就是之前把它设成深色也
        /// 压不住白边的原因。WS_POPUP 下客户区 = 窗口矩形，XAML 内容铺满即整窗铺满。
        /// 顺带加 WS_EX_TOOLWINDOW（不进任务栏 / Alt-Tab），并用 DwmExtendFrameIntoClientArea
        /// 补回 DWM 阴影（WS_POPUP 默认没有阴影，阴影会跟随后面的圆角 region 走）。
        /// </summary>
        private void ApplyFramelessStyle(IntPtr handle)
        {
            var style = (uint)GetWindowLong(handle, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            style |= WS_POPUP;
            SetWindowLong(handle, GWL_STYLE, unchecked((int)style));

            var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

            // 让样式改动立刻生效（强制重算非客户区）。
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            // 补回阴影：底边伸入 1px 触发 DWM 的窗口阴影。
            var margins = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 1 };
            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
        }

        /// <summary>
        /// 关闭 DWM 系统圆角，圆角完全交给 <see cref="ApplyWindowRegion"/>。
        /// DWM 的 ROUND 半径与 DPI 的关系不受我们控制，与 region 双重裁剪可能在四角留缝；
        /// 显式 DONOTROUND 保证窗口形状只有一个来源。深色边色保留，压住可能残留的 1px 描边。
        /// </summary>
        private static void DisableDwmRoundAndBorder(IntPtr handle)
        {
            var preference = DWMWCP_DONOTROUND;
            _ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

            var borderColor = MenuBorderColor;
            _ = DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        }

        /// <summary>
        /// 用 SetWindowRgn 把整窗裁成圆角矩形（半径 8 DIP，按窗口 DPI 换算成物理像素）。
        /// 尺寸从 GetWindowRect 实测拿，不关心 ResizeClient 的单位与 DPI 换算。
        /// region 之外的像素（无论什么颜色）一概不渲染，圆角外的点击也会穿透到下层。
        /// 必须在窗口尺寸定稿之后调用（见 ShowAtIcon）。成功后 region 归系统所有，无需 DeleteObject。
        /// </summary>
        private void ApplyWindowRegion(IntPtr handle)
        {
            if (!GetWindowRect(handle, out var rect))
            {
                return;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            // 16 = 8 DIP 圆角直径（CreateRoundRectRgn 的最后两个参数是椭圆宽高）。
            var diameter = (int)Math.Round(16.0 * GetDpiForWindow(handle) / 96.0);
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
            if (!SetWindowRgn(handle, region, redraw: true))
            {
                _ = DeleteObject(region);
            }
        }

        /// <summary>
        /// 把菜单贴到托盘图标的**右上角**：菜单左下角与图标右上角对齐，也就是从图标往左上方展开
        /// （任务栏通常在屏幕底部，所以必须向上弹）。参数是图标矩形在屏幕上的物理像素坐标。
        /// 超出所在屏幕的工作区时会自动收回。
        /// </summary>
        public void ShowAtIcon(int iconLeft, int iconTop, int iconRight, int iconBottom)
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // GetDpiForWindow 返回物理 DPI（如 125% → 120），scale = 物理 DPI / 96。
            var scale = GetDpiForWindow(handle) / 96.0;

            // 设计尺寸即有效像素（与 XAML 逻辑像素同单位）。AppWindow.ResizeClient / Move /
            // DisplayArea 都用有效像素，**不要乘 scale**——之前多乘一次让客户区比卡片大一圈，
            // 露出窗口默认浅色背景（白边）。
            var width = MenuWidth;
            var height = MenuHeight;

            // 图标矩形来自 Shell_NotifyIconGetRect，是物理像素，转成有效像素再参与布局。
            var iconLeftEp = (int)Math.Round(iconLeft / scale);
            var iconTopEp = (int)Math.Round(iconTop / scale);
            var iconRightEp = (int)Math.Round(iconRight / scale);
            var iconBottomEp = (int)Math.Round(iconBottom / scale);

            // 菜单左下角对齐图标右上角，从图标往左上方展开（任务栏通常在底部，向上弹）。
            var x = iconRightEp;
            var y = iconTopEp - height;

            var area = DisplayArea.GetFromPoint(
                new PointInt32(iconLeftEp, iconBottomEp), DisplayAreaFallback.Nearest);
            if (area is not null)
            {
                var work = area.WorkArea;

                if (x + width > work.X + work.Width)
                {
                    x = work.X + work.Width - width;
                }

                if (x < work.X)
                {
                    x = work.X;
                }

                if (y + height > work.Y + work.Height)
                {
                    y = work.Y + work.Height - height;
                }

                if (y < work.Y)
                {
                    y = work.Y;
                }
            }

            // 用 ResizeClient 而不是 MoveAndResize：客户区尺寸 = 卡片尺寸（均与 MenuCard 钉死的一致），
            // 窗口边框 / 非客户区一律落在卡片之外，卡片不会退到窗口里侧、露出底色。
            AppWindow.ResizeClient(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(x, y));

            // 窗口尺寸定稿后把整窗裁成圆角矩形（见 ApplyWindowRegion）：
            // region 之外的像素一概不渲染，圆角边缘就是 XAML 卡片自己的圆角与光边，
            // 物理上不存在任何「露白」的位置。
            ApplyWindowRegion(handle);

            Activate();

            // 从托盘弹出时本进程未必是前台，补一次置前，免得菜单刚显示就被判成「失焦」而立刻收起。
            SetForegroundWindow(handle);
        }

        /// <summary>收起菜单。可以安全地重复调用。</summary>
        public void Dismiss()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            Close();
        }

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            // 点到别处就收起（与系统菜单的行为一致）。
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                Dismiss();
            }
        }

        /// <summary>按当前连接状态刷新状态区与「连接 / 断开」那一行。</summary>
        private void RefreshState()
        {
            var connection = VpnConnectionService.Current;
            var busy = connection.Status is VpnStatus.Connected or VpnStatus.Connecting;

            if (busy)
            {
                StatusDot.Fill = ThemeResources.GetBrush("StatusOkBrush");
                StatusText.Text = connection.Status == VpnStatus.Connected ? "已连接" : "正在连接…";
                ConnectGlyph.Glyph = "\uE71A"; // Stop
                ConnectText.Text = "断开连接";
            }
            else
            {
                StatusDot.Fill = ThemeResources.GetBrush(
                    connection.Status == VpnStatus.Failed ? "StatusBadBrush" : "TextTertiaryBrush");
                StatusText.Text = connection.Status == VpnStatus.Failed ? "连接失败" : "未连接";
                ConnectGlyph.Glyph = "\uE768"; // Play
                ConnectText.Text = "连接";
            }

            NodeText.Text = VpnSessionState.CurrentNode?.Name ?? "未选择节点";
        }

        private void OnConnectClicked(object sender, RoutedEventArgs e) =>
            ConnectRequested?.Invoke(this, EventArgs.Empty);

        private void OnOpenClicked(object sender, RoutedEventArgs e) =>
            OpenRequested?.Invoke(this, EventArgs.Empty);

        private void OnSettingsClicked(object sender, RoutedEventArgs e) =>
            SettingsRequested?.Invoke(this, EventArgs.Empty);

        private void OnExitClicked(object sender, RoutedEventArgs e) =>
            ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
