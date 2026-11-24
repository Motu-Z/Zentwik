using System.Runtime.InteropServices;

namespace Zentwik.UI;

/// <summary>少量 Win32 互操作：仅用于修正无边框窗口的最大化工作区</summary>
internal static class NativeMethods
{
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>MonitorFromPoint —— 取某个屏幕坐标所在的显示器</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>窗口所在显示器的 DPI；失败返回 0。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

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

    //  系统托盘（Shell_NotifyIcon）

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

    /// <summary>结构体包含气泡正文与标题。</summary>
    public const uint NIF_INFO = 0x00000010;

    /// <summary>GetSystemMetrics 的索引：SM_CXSMICON —— 小图标（16×16 逻辑）的宽度</summary>
    public const int SM_CXSMICON = 49;

    /// <summary>GetSystemMetrics 的索引：SM_CYSMICON —— 小图标的高度</summary>
    public const int SM_CYSMICON = 50;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

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

    /// <summary>SHCNE_ASSOCCHANGED：告诉外壳「文件关联 / 右键菜单变了」，让它重读注册表。</summary>
    public const int SHCNE_ASSOCCHANGED = 0x08000000;

    public const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>屏幕坐标点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    //  原生弹出菜单（TrackPopupMenuEx）

    /// <summary>普通文字菜单项。</summary>
    public const uint MF_STRING = 0x00000000;

    /// <summary>这一项是子菜单，uIDNewItem 传子菜单句柄。</summary>
    public const uint MF_POPUP = 0x00000010;

    /// <summary>给这一项画勾。</summary>
    public const uint MF_CHECKED = 0x00000008;

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
}
