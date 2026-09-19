using System;
using System.Runtime.InteropServices;

namespace RayShuttle.Interop
{
    /// <summary>
    /// 把窗口锁定为固定宽高比：拖动任意边或角时，另一边按比例跟随。
    ///
    /// Windows 没有提供现成的等比缩放能力，只能挂接窗口过程处理 WM_SIZING。
    /// 这里约束的是**客户区**比例（标题栏与边框不计入），因为背景图的宽度是按客户区高度推算的。
    ///
    /// 失败时不会破坏窗口：初始化失败就退化为普通可缩放窗口，处理过程中出错也不干预系统默认行为。
    /// </summary>
    public sealed class WindowAspectLock
    {
        private const int GwlWndProc = -4;
        private const uint WmSizing = 0x0214;

        // WM_SIZING 的 wParam：正在拖动的是哪条边或哪个角
        private const int EdgeLeft = 1;
        private const int EdgeRight = 2;
        private const int EdgeTop = 3;
        private const int EdgeTopLeft = 4;
        private const int EdgeTopRight = 5;
        private const int EdgeBottom = 6;
        private const int EdgeBottomLeft = 7;
        private const int EdgeBottomRight = 8;

        private readonly IntPtr _handle;
        private readonly double _aspectRatio;

        /// <summary>
        /// 最小客户区宽度（有效像素）。高度由比例推出，不单独设。
        /// 可在拿到屏幕可用区域后再收窄，避免在小屏 / 高 DPI 下把窗口顶出屏幕。
        /// </summary>
        public double MinimumClientWidth { get; set; }

        /// <summary>必须持有委托引用，否则被 GC 回收后窗口过程会指向已释放的内存。</summary>
        private readonly WindowProcedure _windowProcedure;

        private IntPtr _previousWindowProcedure;

        public WindowAspectLock(IntPtr handle, double aspectRatio, double minimumClientWidth)
        {
            _handle = handle;
            _aspectRatio = aspectRatio;
            MinimumClientWidth = minimumClientWidth;
            _windowProcedure = OnWindowMessage;
        }

        /// <summary>是否已成功挂接。</summary>
        public bool IsAttached => _previousWindowProcedure != IntPtr.Zero;

        public void Attach()
        {
            if (_handle == IntPtr.Zero || IsAttached)
            {
                return;
            }

            var pointer = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
            _previousWindowProcedure = SetWindowLongPtr(_handle, GwlWndProc, pointer);
        }

        public void Detach()
        {
            if (!IsAttached)
            {
                return;
            }

            SetWindowLongPtr(_handle, GwlWndProc, _previousWindowProcedure);
            _previousWindowProcedure = IntPtr.Zero;
        }

        /// <summary>测量标题栏与边框占用的尺寸（不受窗口大小影响）。</summary>
        public static void MeasureNonClientSize(IntPtr handle, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (!GetWindowRect(handle, out var window) || !GetClientRect(handle, out var client))
            {
                return;
            }

            width = (window.Right - window.Left) - (client.Right - client.Left);
            height = (window.Bottom - window.Top) - (client.Bottom - client.Top);
        }

        private IntPtr OnWindowMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmSizing && lParam != IntPtr.Zero)
            {
                try
                {
                    ApplyAspectRatio((int)wParam, lParam);
                }
                catch
                {
                    // 出错就不干预，退回系统默认缩放，绝不能把窗口卡死。
                }

                // 告知系统「本次已处理」，采用我们写回的矩形。
                return new IntPtr(1);
            }

            return CallWindowProc(_previousWindowProcedure, handle, message, wParam, lParam);
        }

        private void ApplyAspectRatio(int edge, IntPtr lParam)
        {
            var rect = Marshal.PtrToStructure<NativeRect>(lParam);
            MeasureNonClientSize(_handle, out var nonClientWidth, out var nonClientHeight);

            var clientWidth = (rect.Right - rect.Left) - nonClientWidth;
            var clientHeight = (rect.Bottom - rect.Top) - nonClientHeight;

            // 拖上下边时以高度为准反推宽度，其余情况以宽度为准反推高度。
            if (edge is EdgeTop or EdgeBottom)
            {
                clientWidth = (int)Math.Round(clientHeight * _aspectRatio);
            }
            else
            {
                clientHeight = (int)Math.Round(clientWidth / _aspectRatio);
            }

            // 必须在这里自己兜住下限。
            // 系统的最小尺寸检查针对的是「拖动方向那一维」，而被反推出来的另一维
            // 系统并没有检查过——不兜底的话窗口能被无限缩小（拖动下边缘时尤其明显）。
            var minimumClientWidth = GetMinimumClientWidth();
            if (clientWidth < minimumClientWidth)
            {
                clientWidth = minimumClientWidth;
                clientHeight = (int)Math.Round(clientWidth / _aspectRatio);
            }

            var width = clientWidth + nonClientWidth;
            var height = clientHeight + nonClientHeight;

            // 按拖动的方向锚定对边，这样窗口会跟着鼠标往正确的方向长大。
            if (IsLeftEdge(edge))
            {
                rect.Left = rect.Right - width;
            }
            else
            {
                rect.Right = rect.Left + width;
            }

            if (IsTopEdge(edge))
            {
                rect.Top = rect.Bottom - height;
            }
            else
            {
                rect.Bottom = rect.Top + height;
            }

            Marshal.StructureToPtr(rect, lParam, false);
        }

        /// <summary>
        /// 最小客户区宽度换算成物理像素。每次都按当前窗口 DPI 换算，
        /// 这样窗口被拖到不同缩放的显示器上时下限仍然正确。
        /// </summary>
        private int GetMinimumClientWidth()
        {
            var scale = GetDpiForWindow(_handle) / 96.0;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            return Math.Max(1, (int)Math.Round(MinimumClientWidth * scale));
        }

        private static bool IsLeftEdge(int edge) =>
            edge is EdgeLeft or EdgeTopLeft or EdgeBottomLeft;

        private static bool IsTopEdge(int edge) =>
            edge is EdgeTop or EdgeTopLeft or EdgeTopRight;

        private delegate IntPtr WindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr newValue);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr handle, int index, int newValue);

        private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr newValue) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(handle, index, newValue)
                : new IntPtr(SetWindowLong32(handle, index, newValue.ToInt32()));

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr handle);
    }
}
