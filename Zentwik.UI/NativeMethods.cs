using System.Runtime.InteropServices;

namespace Zentwik.UI;

/// <summary>
/// 少量 Win32 互操作：仅用于修正无边框窗口的最大化工作区。
/// </summary>
/// <remarks>
/// 互操作结构一律用 32 位整数字段。Win32 的 POINT / RECT 是 LONG，
/// WPF 的 Point / Rect 是 double，混用会把结构撑大一倍并越界读写。
/// </remarks>
internal static class NativeMethods
{
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>
    /// MonitorFromPoint —— 取某个屏幕坐标所在的显示器。
    /// </summary>
    /// <remarks>
    /// 托盘菜单需要按**鼠标所在的那块屏**定位并避开任务栏，
    /// 用 MonitorFromWindow 拿不到（托盘没有窗口句柄）。
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ============================================================
    //  系统托盘（Shell_NotifyIcon）
    // ============================================================

    /// <summary>添加图标到托盘。</summary>
    public const uint NIM_ADD = 0x00000000;

    /// <summary>修改托盘图标。</summary>
    public const uint NIM_MODIFY = 0x00000001;

    /// <summary>从托盘移除图标。</summary>
    public const uint NIM_DELETE = 0x00000002;

    /// <summary>结构体包含回调消息。</summary>
    public const uint NIF_MESSAGE = 0x00000001;

    /// <summary>结构体包含图标句柄。</summary>
    public const uint NIF_ICON = 0x00000002;

    /// <summary>结构体包含提示文本。</summary>
    public const uint NIF_TIP = 0x00000004;

    /// <summary>
    /// GetSystemMetrics 的索引：SM_CXSMICON —— 小图标（16×16 逻辑）的宽度。
    /// </summary>
    /// <remarks>
    /// 托盘图标的正确物理尺寸由这个值 × 当前 DPI 得出。
    /// 在 Per-Monitor DPI 感知的进程里，GetSystemMetrics 返回的是
    /// **当前显示器**的值 —— 这正是我们要的（不同屏不同缩放时托盘图标尺寸也不同）。
    /// </remarks>
    public const int SM_CXSMICON = 49;

    /// <summary>
    /// GetSystemMetrics 的索引：SM_CYSMICON —— 小图标的高度。
    /// </summary>
    public const int SM_CYSMICON = 50;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// GetDpiForWindow —— 取窗口所在显示器的 DPI。
    /// </summary>
    /// <remarks>
    /// Win10 1607+ 才有这个 API；旧版本上返回 0，
    /// 调用方需要自己回落到 96（100% 缩放）。
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>NOTIFYICONDATA —— 托盘图标描述结构。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ============================================================
    //  由 DIB 创建 HICON（托盘图标运行时渲染用）
    // ============================================================

    /// <summary>CreateDIBSection 的用法：DIB_RGB_COLORS。</summary>
    public const uint DIB_RGB_COLORS = 0;

    /// <summary>biHeight 取负表示自上而下（top-down）DIB。</summary>
    public const int DIB_TOPDOWN = -1;

    /// <summary>biCompression：BI_RGB，不压缩。</summary>
    public const uint BI_RGB = 0;

    /// <summary>
    /// BITMAPINFOHEADER + 附加的 RGBQUAD 数组（biSizeImage 之后紧跟）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
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

        // RGBQUAD[1] —— 32bpp 时不需要调色板，但结构体要留出这个位置，
        // 否则 CreateDIBSection 会因为结构体长度不符而失败。
        public uint biColors;
    }

    /// <summary>
    /// ICONINFO —— 描述一个图标（彩色位图 + 掩码位图）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(
        int nWidth, int nHeight, uint nPlanes, uint nBitsPerPixel, IntPtr lpBits);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>屏幕坐标点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    // ============================================================
    //  原生弹出菜单（TrackPopupMenuEx）
    // ============================================================

    /// <summary>普通文字菜单项。</summary>
    public const uint MF_STRING = 0x00000000;

    /// <summary>分隔线。uIDNewItem 与文字都要给 0 / null。</summary>
    public const uint MF_SEPARATOR = 0x00000800;

    /// <summary>菜单左边缘与指定 x 对齐。</summary>
    public const uint TPM_LEFTALIGN = 0x0000;

    /// <summary>菜单底边与指定 y 对齐 —— 托盘在屏幕底部，必须向上展开。</summary>
    public const uint TPM_BOTTOMALIGN = 0x0020;

    /// <summary>允许用右键选中菜单项。</summary>
    public const uint TPM_RIGHTBUTTON = 0x0002;

    /// <summary>同步返回被选中的命令 ID，而不是给 owner 发 WM_COMMAND。</summary>
    public const uint TPM_RETURNCMD = 0x0100;

    /// <summary>不发送 WM_INITMENU / WM_MENUSELECT 等通知。</summary>
    public const uint TPM_NONOTIFY = 0x0080;

    public const uint WM_NULL = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    /// <summary>把某一项设为默认项：系统会把它画成粗体，双击托盘图标即触发它。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        IntPtr hWnd,
        IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 让进程内的 Win32 原生控件（菜单、消息框、滚动条）跟随系统深浅色。
    /// </summary>
    /// <remarks>
    /// uxtheme.dll 的 #135 是未文档化的 SetPreferredAppMode：
    /// 0 = Default，1 = AllowDark，2 = ForceDark，3 = ForceLight。
    /// Win11（22000）才有；旧系统上找不到入口，静默跳过即可。
    /// 不调用它的话，深色任务栏下会弹出一张纯白菜单 —— 那正是当初改自绘的原因。
    /// </remarks>
    public static void EnableNativeDarkMenus()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22000)
            {
                return;
            }

            SetPreferredAppMode(1);
            FlushMenuThemes();
        }
        catch (Exception)
        {
            // 精简版系统可能缺这个导出 —— 退回系统默认浅色菜单，不影响功能
        }
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
