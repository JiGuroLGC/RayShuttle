using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace RayShuttle.Interop
{
    /// <summary>托盘气泡通知的语气，决定图标与系统提示音。</summary>
    internal enum TrayNotificationLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 系统托盘图标（通知区域）。
    ///
    /// 实现要点：
    /// - 图标优先从随包的 <c>Assets/RayShuttle.ico</c> 载入（多尺寸，小尺寸清晰），
    ///   取不到才回退到程序化绘制。
    /// - 通知图标需要一扇消息窗口来接收回调（单击 / 右键）。这里用一个「仅消息」窗口
    ///   （HWND_MESSAGE），不会在屏幕上出现，只用来收 WM_TRAYICON。
    /// - 只负责**抛事件**，不自己弹菜单：<see cref="ShowRequested"/> 恢复窗口，
    ///   <see cref="ContextMenuRequested"/> 交给主窗口弹自定义的品牌风格菜单
    ///   （系统那种老式弹出菜单太丑，已弃用）。
    /// - 顺带承担**气泡通知**（<see cref="ShowNotification"/>）：未打包运行的 WinUI 3 应用
    ///   发 WinRT Toast 需要额外注册 AUMID，走托盘气泡则零额外配置、且与应用图标天然一致。
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        // 通知图标的标志位。每个方法都要显式指定本次要更新哪些字段——
        // 它们是共用同一份 NOTIFYICONDATA 的，改完必须还原，否则下次调用会带着不该带的标志。
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;
        private const uint IconFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;

        private const uint NIIF_INFO = 0x00000001;
        private const uint NIIF_WARNING = 0x00000002;
        private const uint NIIF_ERROR = 0x00000003;

        /// <summary>气泡的展示时长（毫秒）。系统可能按用户的辅助功能设置覆盖它。</summary>
        private const uint BalloonTimeoutMilliseconds = 5000;
        private const uint WM_TRAYICON = 0x8000;
        private const uint WM_DESTROY = 0x0002;
        private const uint LR_DEFAULTCOLOR = 0x00000000;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_SHARED = 0x00008000;
        private const uint IMAGE_ICON = 1;
        private const int SM_CXSMICON = 49;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONUP = 0x0205;
        private const int GWLP_WNDPROC = -4;
        private const uint SW_RESTORE = 9;
        private const uint SW_HIDE = 0;

        private const int CallbackId = 1;

        /// <summary>
        /// NOTIFYICONDATAW 的**完整**布局（V4）。
        ///
        /// 字段顺序与大小必须和 Win32 的结构体逐字节一致：Shell_NotifyIcon 用 cbSize 来判定
        /// 结构版本，尺寸对不上就直接返回 FALSE、图标根本不显示。这里曾经把 szInfo / szInfoTitle
        /// 写成 IntPtr（想缩小结构），算出来 cbSize 只有三百多字节，而 Windows 只认几个固定值
        /// （x64 下 V2=952 / V4=976），于是托盘图标一直不出现。
        /// **不要「优化」成更短的结构**，缺一个字段 cbSize 就不对了。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public int fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public IntPtr bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RGBQUAD
        {
            public byte rgbBlue;
            public byte rgbGreen;
            public byte rgbRed;
            public byte rgbReserved;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
            IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("shell32", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

        [DllImport("user32")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32")]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

        [DllImport("gdi32")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("gdi32")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32")]
        private static extern bool DeleteObject(IntPtr hgdiobj);

        [DllImport("gdi32")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32")]
        private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

        [DllImport("user32")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("shell32")]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>NOTIFYICONIDENTIFIER：向系统查托盘图标位置时用的身份。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        /// <summary>单击 / 双击托盘图标时触发，由主窗口把窗口恢复出来。</summary>
        public event EventHandler? ShowRequested;

        /// <summary>右键托盘图标时触发，由主窗口弹出自定义菜单（本类不弹 Win32 菜单）。</summary>
        public event EventHandler? ContextMenuRequested;

        /// <summary>取当前鼠标屏幕坐标，作为拿不到图标位置时的兜底。</summary>
        public static bool TryGetCursorPosition(out int x, out int y)
        {
            if (GetCursorPos(out var point))
            {
                x = point.X;
                y = point.Y;
                return true;
            }

            x = 0;
            y = 0;
            return false;
        }

        /// <summary>
        /// 取托盘图标在屏幕上的矩形（物理像素）。菜单据此贴到图标右上角。
        /// 图标被折叠进溢出区、系统不提供时返回 false（调用方退回光标位置）。
        /// </summary>
        public bool TryGetIconRect(out int left, out int top, out int right, out int bottom)
        {
            var identifier = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = _messageWindow,
                uID = CallbackId
            };

            if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 /*S_OK*/)
            {
                left = rect.Left;
                top = rect.Top;
                right = rect.Right;
                bottom = rect.Bottom;
                return true;
            }

            left = top = right = bottom = 0;
            return false;
        }

        private readonly IntPtr _messageWindow;
        private readonly WndProcDelegate _wndProc;
        private IntPtr _prevWndProc;
        private IntPtr _hIcon;
        private NOTIFYICONDATA _notifyData;
        private bool _added;
        private bool _disposed;

        public TrayIcon()
        {
            _wndProc = WndProc;

            // 仅消息窗口（HWND_MESSAGE）：屏幕上不出现，仅用于接收托盘回调。
            _messageWindow = CreateWindowEx(
                0, "Static", "RayShuttleTray", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            _prevWndProc = SetWindowLongPtr(_messageWindow, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

            // 优先用随应用分发的多尺寸 .ico：系统会按当前 DPI 直接取到 16/20/24 这类小尺寸，
            // 比「画一张 64px 再让外壳缩放」清晰得多（后者在托盘里是一团发虚的点）。
            // 取不到文件才退回程序化绘制。
            _hIcon = LoadIconFromFile();
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = CreateBrandIcon(64);
            }
        }

        /// <summary>从 Assets/RayShuttle.ico 载入托盘尺寸的图标；失败返回 IntPtr.Zero。</summary>
        private static IntPtr LoadIconFromFile()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "RayShuttle.ico");
                if (!File.Exists(path))
                {
                    return IntPtr.Zero;
                }

                var size = GetSystemMetrics(SM_CXSMICON);
                if (size <= 0)
                {
                    size = 16;
                }

                return LoadImage(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>在通知区域显示图标并注册回调消息。tip 为悬停提示文本。</summary>
        public void Add(string tip)
        {
            if (_added)
            {
                return;
            }

            _notifyData = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _messageWindow,
                uID = CallbackId,
                uFlags = IconFlags,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = Truncate(tip, 127),
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };

            _added = Shell_NotifyIcon(NIM_ADD, ref _notifyData);
        }

        public void UpdateTip(string tip)
        {
            if (!_added)
            {
                return;
            }

            _notifyData.uFlags = IconFlags;
            _notifyData.szTip = Truncate(tip, 127);
            Shell_NotifyIcon(NIM_MODIFY, ref _notifyData);
        }

        /// <summary>
        /// 弹一条托盘气泡通知。
        ///
        /// **长度必须先截断**：`szInfo` 是 256 个宽字符的定长数组（含结尾 NUL，实际可用 255），
        /// `szInfoTitle` 只有 64（可用 63）。超长会让整次 `Shell_NotifyIcon` 直接返回 FALSE——
        /// 表现就是"通知偶尔不出现"，而且不报任何错。
        /// </summary>
        public void ShowNotification(string title, string message, TrayNotificationLevel level = TrayNotificationLevel.Info)
        {
            if (!_added)
            {
                return;
            }

            _notifyData.uFlags = NIF_INFO;
            _notifyData.szInfo = Truncate(message, 255);
            _notifyData.szInfoTitle = Truncate(title, 63);
            _notifyData.uTimeoutOrVersion = BalloonTimeoutMilliseconds;
            _notifyData.dwInfoFlags = level switch
            {
                TrayNotificationLevel.Warning => NIIF_WARNING,
                TrayNotificationLevel.Error => NIIF_ERROR,
                _ => NIIF_INFO
            };

            Shell_NotifyIcon(NIM_MODIFY, ref _notifyData);

            // 还原成图标自身的标志位，否则后续 UpdateTip 会带着 NIF_INFO 发出去、改不到悬停提示。
            _notifyData.uFlags = IconFlags;
        }

        private static string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            if (hWnd == _messageWindow)
            {
                if (uMsg == WM_TRAYICON)
                {
                    var msg = (uint)lParam.ToInt32();
                    if (msg == WM_LBUTTONUP || msg == WM_LBUTTONDBLCLK)
                    {
                        // 单击 / 双击托盘图标 = 恢复主窗口（不弹菜单）。
                        ShowRequested?.Invoke(this, EventArgs.Empty);
                        return IntPtr.Zero;
                    }

                    if (msg == WM_RBUTTONUP)
                    {
                        // 菜单不在这里弹：交给主窗口，用 XAML 画一套品牌风格的菜单。
                        ContextMenuRequested?.Invoke(this, EventArgs.Empty);
                        return IntPtr.Zero;
                    }
                }
            }

            return CallWindowProc(_prevWndProc, hWnd, uMsg, wParam, lParam);
        }

        /// <summary>把主窗口从托盘恢复：先还原再置前，避免在任务栏/托盘间闪烁。</summary>
        public static void RestoreWindow(IntPtr windowHandle)
        {
            ShowWindow(windowHandle, SW_RESTORE);
            SetForegroundWindow(windowHandle);
        }

        /// <summary>把主窗口藏到托盘（不退出进程）。</summary>
        public static void HideWindow(IntPtr windowHandle) => ShowWindow(windowHandle, SW_HIDE);

        /// <summary>按品牌色程序化生成一枚青→紫渐变光点图标（带羽化边缘与暗色核心）。</summary>
        private static IntPtr CreateBrandIcon(int size)
        {
            try
            {
                var hdc = GetDC(IntPtr.Zero);
                var hMemDc = CreateCompatibleDC(hdc);

                // 颜色位图：32bpp BGRA，逐像素绘制。
                var colorInfo = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = size,
                        biHeight = size,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0,
                        biSizeImage = (uint)(size * size * 4)
                    }
                };

                var hColor = CreateDIBSection(hdc, ref colorInfo, 0, out var pBits, IntPtr.Zero, 0);
                if (hColor == IntPtr.Zero || pBits == IntPtr.Zero)
                {
                    DeleteObject(hMemDc);
                    DeleteObject(hdc);
                    return IntPtr.Zero;
                }

                // 掩码位图：1bpp，全 0（表示颜色位图完全生效，由 alpha 决定透明）。
                var maskInfo = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = size,
                        biHeight = size,
                        biPlanes = 1,
                        biBitCount = 1,
                        biCompression = 0
                    },
                    bmiColors = Marshal.AllocHGlobal(Marshal.SizeOf<RGBQUAD>() * 2)
                };

                var black = new RGBQUAD { rgbBlue = 0, rgbGreen = 0, rgbRed = 0, rgbReserved = 0 };
                var white = new RGBQUAD { rgbBlue = 255, rgbGreen = 255, rgbRed = 255, rgbReserved = 0 };
                Marshal.StructureToPtr(black, maskInfo.bmiColors, false);
                Marshal.StructureToPtr(white, IntPtr.Add(maskInfo.bmiColors, Marshal.SizeOf<RGBQUAD>()), false);

                var hMask = CreateDIBSection(hdc, ref maskInfo, 0, out _, IntPtr.Zero, 0);

                Marshal.FreeHGlobal(maskInfo.bmiColors);

                // 逐像素绘制渐变光点。
                var stride = size * 4;
                var data = new byte[stride * size];

                // 品牌色（sRGB）。
                const byte cyanR = 0x4F, cyanG = 0xE3, cyanB = 0xFF;
                const byte vioR = 0xA8, vioG = 0x55, vioB = 0xF7;
                const byte coreR = 0x04, coreG = 0x12, coreB = 0x1E;

                var cx = (size - 1) / 2.0;
                var cy = (size - 1) / 2.0;
                var radius = size / 2.0;

                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var dx = (x - cx) / radius;
                        var dy = (y - cy) / radius;
                        var dist = Math.Sqrt(dx * dx + dy * dy);

                        // DIB 的 biHeight 是正数 → 像素行自底向上存储，所以这里要把 y 翻过来；
                        // 否则画出来的图标上下镜像（斜向渐变方向会反过来，与界面里的品牌光点不一致）。
                        var offset = (size - 1 - y) * stride + x * 4;

                        if (dist > 1.0)
                        {
                            // 透明背景。
                            data[offset] = 0;
                            data[offset + 1] = 0;
                            data[offset + 2] = 0;
                            data[offset + 3] = 0;
                            continue;
                        }

                        // 对角线渐变：左上青 → 右下紫。
                        var t = Math.Clamp((dx + dy + 2) / 4.0, 0.0, 1.0);
                        byte r = (byte)(cyanR + (vioR - cyanR) * t);
                        byte g = (byte)(cyanG + (vioG - cyanG) * t);
                        byte b = (byte)(cyanB + (vioB - cyanB) * t);

                        // 暗色核心。
                        if (dist < 0.30)
                        {
                            var k = (0.30 - dist) / 0.30;
                            r = (byte)(r + (coreR - r) * k);
                            g = (byte)(g + (coreG - g) * k);
                            b = (byte)(b + (coreB - b) * k);
                        }

                        // 边缘羽化。
                        byte alpha = dist > 0.90 ? (byte)((1.0 - dist) / 0.10 * 255) : (byte)255;

                        data[offset] = b;
                        data[offset + 1] = g;
                        data[offset + 2] = r;
                        data[offset + 3] = alpha;
                    }
                }

                Marshal.Copy(data, 0, pBits, data.Length);

                if (hMask == IntPtr.Zero)
                {
                    DeleteObject(hColor);
                    DeleteObject(hMemDc);
                    DeleteObject(hdc);
                    return IntPtr.Zero;
                }

                var iconInfo = new ICONINFO
                {
                    fIcon = 1,
                    xHotspot = 0,
                    yHotspot = 0,
                    hbmMask = hMask,
                    hbmColor = hColor
                };

                var hIcon = CreateIconIndirect(ref iconInfo);

                // 位图已被图标拷贝，立即释放。
                DeleteObject(hMask);
                DeleteObject(hColor);
                DeleteObject(hMemDc);
                DeleteObject(hdc);

                return hIcon;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_added)
            {
                Shell_NotifyIcon(NIM_DELETE, ref _notifyData);
                _added = false;
            }

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }

            if (_prevWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_messageWindow, GWLP_WNDPROC, _prevWndProc);
                _prevWndProc = IntPtr.Zero;
            }

            if (_messageWindow != IntPtr.Zero)
            {
                DestroyWindow(_messageWindow);
            }
        }
    }
}
