using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Zentwik.Worker.Tray
{
    /// <summary>
    /// 托盘宿主用到的 Win32 声明。窗口有 DPI 语义，结构一律按 32 位 int / 指针布局。
    /// </summary>
    internal static partial class TrayNative
    {
        public const int WM_NULL = 0x0000;
        public const int WM_DESTROY = 0x0002;
        public const int WM_CLOSE = 0x0010;
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_ENDSESSION = 0x0016;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DISPLAYCHANGE = 0x007E;

        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONUP = 0x0205;

        /// <summary>托盘回调消息：WM_APP + 1。</summary>
        public const int WM_TRAY = 0x8001;

        /// <summary>内部消息：设置保存后请求重评估图标是否该显示（WM_APP + 2）。</summary>
        public const int WM_REEVALUATE = 0x8002;

        public const int NIM_ADD = 0;
        public const int NIM_MODIFY = 1;
        public const int NIM_DELETE = 2;

        public const int NIF_MESSAGE = 0x01;
        public const int NIF_ICON = 0x02;
        public const int NIF_TIP = 0x04;
        public const int NIF_INFO = 0x10;

        public const int MF_STRING = 0x0000;
        public const int MF_POPUP = 0x0010;
        public const int MF_SEPARATOR = 0x0800;
        public const int MF_CHECKED = 0x0008;

        public const uint TPM_LEFTALIGN = 0x0000;
        public const uint TPM_BOTTOMALIGN = 0x0040;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_RETURNCMD = 0x0100;
        public const uint TPM_NONOTIFY = 0x0080;

        public const uint MSGFLT_ALLOW = 1;

        public const uint QS_ALLINPUT = 0x04FF;
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 0x102;

        public const uint PM_REMOVE = 0x0001;
        public const int SM_CXSMICON = 49;
        public const int DIB_RGB_COLORS = 0;
        public const int GWLP_USERDATA = -21;
        public const int IDC_ARROW = 32512;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public POINT pt;
            public int lPrivate;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEXW
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            public IntPtr lpszClassName;
            public IntPtr hIconSm;
        }

        /// <summary>
        /// NOTIFYICONDATAW。szTip / szInfo / szInfoTitle 用定长内联缓冲区：
        /// 源生成的 P/Invoke 不支持字符串字段按值封送。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal unsafe struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            public fixed char szTip[128];
            public int dwState;
            public int dwStateMask;
            public fixed char szInfo[256];
            public int uTimeoutOrVersion;
            public fixed char szInfoTitle[64];
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ICONINFO
        {
            public int fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr GetModuleHandleW(IntPtr lpModuleName);

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr CreateWindowExW(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int RegisterWindowMessageW(string lpString);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial IntPtr CreatePopupMenu();

        [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyMenu(IntPtr hMenu);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial int TrackPopupMenuEx(
            IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        internal static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TranslateMessage(ref MSG lpMsg);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial IntPtr DispatchMessageW(ref MSG lpMsg);

        [LibraryImport("user32.dll")]
        internal static partial uint MsgWaitForMultipleObjects(
            uint nCount, ref IntPtr pHandles, [MarshalAs(UnmanagedType.Bool)] bool fWaitAll,
            uint dwMilliseconds, uint dwWakeMask);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(IntPtr hIcon);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteObject(IntPtr hObject);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetDC(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    }

    /// <summary>
    /// 预渲染的托盘位图包：magic "ZWT1" | int32 档数 | 每档 [宽][高][BGRA 像素]。
    /// 后台服务没有 WPF，图标由 UI 侧 <c>--trayicons</c> 渲染后内嵌。
    /// </summary>
    internal sealed class TrayIconSet
    {
        private const uint Magic = 0x3154575A;

        private readonly List<(int Size, byte[] Pixels)> _frames = new();

        private TrayIconSet()
        {
        }

        public static TrayIconSet Load(string resourceName)
        {
            TrayIconSet set = new();

            using Stream? stream = typeof(TrayIconSet).Assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                return set;
            }

            using BinaryReader reader = new(stream);

            if (reader.ReadUInt32() != Magic)
            {
                return set;
            }

            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                set._frames.Add((width, reader.ReadBytes(width * height * 4)));
            }

            return set;
        }

        public bool IsEmpty => _frames.Count == 0;

        /// <summary>取与目标像素边长最接近的一档。</summary>
        public byte[]? Nearest(int targetSize)
        {
            if (_frames.Count == 0)
            {
                return null;
            }

            (int Size, byte[] Pixels) best = _frames[0];
            int bestDelta = Math.Abs(best.Size - targetSize);

            foreach ((int Size, byte[] Pixels) frame in _frames)
            {
                int delta = Math.Abs(frame.Size - targetSize);
                if (delta < bestDelta)
                {
                    best = frame;
                    bestDelta = delta;
                }
            }

            return best.Pixels;
        }

        /// <summary>建 HICON。像素是 BGRA 直通 alpha，掩码位图只作协议要求存在。</summary>
        public IntPtr CreateHIcon(int targetSize)
        {
            byte[]? pixels = Nearest(targetSize);

            if (pixels is null)
            {
                return IntPtr.Zero;
            }

            int size = (int)Math.Round(Math.Sqrt(pixels.Length / 4.0));

            if (size <= 0)
            {
                return IntPtr.Zero;
            }

            IntPtr hdc = TrayNative.GetDC(IntPtr.Zero);

            try
            {
                TrayNative.BITMAPINFOHEADER header = new()
                {
                    biSize = 40,
                    biWidth = size,
                    biHeight = -size,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                };

                IntPtr hBitmap = TrayNative.CreateDIBSection(
                    hdc, ref header, TrayNative.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);

                if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                IntPtr hMask = TrayNative.CreateBitmap(size, size, 1, 1, IntPtr.Zero);

                try
                {
                    Marshal.Copy(pixels, 0, bits, pixels.Length);

                    TrayNative.ICONINFO info = new()
                    {
                        fIcon = 1,
                        hbmMask = hMask,
                        hbmColor = hBitmap,
                    };

                    return TrayNative.CreateIconIndirect(ref info);
                }
                finally
                {
                    if (hMask != IntPtr.Zero)
                    {
                        TrayNative.DeleteObject(hMask);
                    }

                    TrayNative.DeleteObject(hBitmap);
                }
            }
            finally
            {
                TrayNative.ReleaseDC(IntPtr.Zero, hdc);
            }
        }
    }
}
