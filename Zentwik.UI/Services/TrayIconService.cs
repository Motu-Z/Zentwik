using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>系统托盘图标：1×1 不可见 HwndSource 收消息 + Shell_NotifyIcon + Win32 菜单。</summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>托盘回调消息（自定义 WM_APP+1）。</summary>
    private const int WM_TRAY = 0x8001;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    /// <summary>托盘图标的内部 ID。</summary>
    private const int TrayIconId = 1;

    private HwndSource? _source;
    private IntPtr _iconHandle;
    private bool _added;

    /// <summary>托盘图标的矢量内容（几何 + 配色），换色时重新组装并渲染。</summary>
    private Drawing? _logoDrawing;

    /// <summary>按配色组装矢量内容的工厂 —— 由 AppThemeService 提供</summary>
    private Func<Color, Color, Drawing>? _drawingFactory;

    /// <summary>当前托盘图标的主体色。</summary>
    private Color _body;

    /// <summary>当前托盘图标的阴影色。</summary>
    private Color _shadow;

    /// <summary>上一次渲染用的物理像素边长，DPI 变了要重渲染。</summary>
    private int _renderedPixelSize;

    /// <summary>请求显示并激活主窗口。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>请求退出应用。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>菜单项 ID：显示主窗口。</summary>
    private const int MenuShowWindow = 1;

    /// <summary>菜单项 ID：退出。</summary>
    private const int MenuExit = 2;

    /// <summary>托盘图标是否已成功创建。</summary>
    public bool IsAvailable => _added;

    /// <summary>上一次渲染使用的物理像素边长（诊断用）。</summary>
    public int RenderedPixelSize => _renderedPixelSize;

    /// <summary>创建托盘图标</summary>
    public bool Initialize(Func<Color, Color, Drawing> drawingFactory, Color body, Color shadow)
    {
        _drawingFactory = drawingFactory;
        _body = body;
        _shadow = shadow;

        try
        {
            // 1×1 的不可见消息窗口
            var parameters = new HwndSourceParameters("ZentwikTrayMessageWindow")
            {
                Width = 1,
                Height = 1,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = 0,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            _logoDrawing = drawingFactory(body, shadow);

            // 按当前显示器 DPI 算托盘图标的物理像素尺寸
            int pixelSize = GetTrayPixelSize();

            _iconHandle = TrayIconRenderer.CreateHIcon(_logoDrawing, pixelSize);
            if (_iconHandle == IntPtr.Zero)
            {
                Debug.WriteLine("[TrayIconService] 托盘图标渲染失败");
                return false;
            }

            _renderedPixelSize = pixelSize;

            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _source.Handle,
                uID = TrayIconId,
                uFlags = (int)(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP),
                uCallbackMessage = WM_TRAY,
                hIcon = _iconHandle,
                szTip = "Zentwik",
            };

            _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
            if (!_added)
            {
                Debug.WriteLine("[TrayIconService] Shell_NotifyIcon(NIM_ADD) 失败");
            }

            return _added;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[TrayIconService] 初始化异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>计算托盘图标的物理像素边长</summary>
    private int GetTrayPixelSize()
    {
        // 【注意】进程是 Per-Monitor V2 感知的，GetSystemMetrics 返回的。
        int px = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        if (px <= 0) px = 16;

        // 兜底：极端缩放下限制在合理区间
        return Math.Clamp(px, 16, 64);
    }

    /// <summary>按新的配色重建托盘图标（系统任务栏明暗变化时调用）</summary>
    public void UpdateColors(Color body, Color shadow)
    {
        if (_source is null || _drawingFactory is null || !_added) return;
        if (body == _body && shadow == _shadow) return;

        _body = body;
        _shadow = shadow;

        // 【关键】必须用新配色**重新组装矢量内容**。
        _logoDrawing = _drawingFactory(body, shadow);

        Rerender(force: true);
    }

    /// <summary>重新渲染图标并替换托盘里的那一个</summary>
    private void Rerender(bool force = false)
    {
        if (_source is null || _logoDrawing is null) return;

        int pixelSize = GetTrayPixelSize();
        if (!force && pixelSize == _renderedPixelSize && _iconHandle != IntPtr.Zero)
            return;

        var newIcon = TrayIconRenderer.CreateHIcon(_logoDrawing, pixelSize);
        if (newIcon == IntPtr.Zero) return;

        // 先换托盘里的，再销毁旧的 —— 顺序反了会出现短暂空白
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = TrayIconId,
            uFlags = (int)NativeMethods.NIF_ICON,
            hIcon = newIcon,
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);

        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
        }

        _iconHandle = newIcon;
        _renderedPixelSize = pixelSize;
    }

    /// <summary>消息窗口的窗口过程</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAY) return IntPtr.Zero;

        switch (lParam.ToInt32())
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                ShowRequested?.Invoke(this, EventArgs.Empty);
                break;

            case WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }

        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>弹出 Win32 原生菜单（TrackPopupMenuEx）。</summary>
    /// <remarks>SetForegroundWindow 否则点外部不消失；TPM_BOTTOMALIGN 往上长；TPM_RETURNCMD 同步取命令。</remarks>
    private void ShowContextMenu()
    {
        if (_source is null)
        {
            return;
        }

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(
                menu,
                NativeMethods.MF_STRING,
                new IntPtr(MenuShowWindow),
                "显示主窗口");

            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);

            NativeMethods.AppendMenu(
                menu,
                NativeMethods.MF_STRING,
                new IntPtr(MenuExit),
                "退出 Zentwik");

            // 双击托盘图标等价于「显示主窗口」，所以它是默认项（系统画成粗体）
            NativeMethods.SetMenuDefaultItem(menu, MenuShowWindow, 0);

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_source.Handle);

            uint flags = NativeMethods.TPM_LEFTALIGN
                         | NativeMethods.TPM_BOTTOMALIGN
                         | NativeMethods.TPM_RIGHTBUTTON
                         | NativeMethods.TPM_RETURNCMD
                         | NativeMethods.TPM_NONOTIFY;

            int command = NativeMethods.TrackPopupMenuEx(
                menu,
                flags,
                pt.X,
                pt.Y,
                _source.Handle,
                IntPtr.Zero);

            NativeMethods.PostMessage(_source.Handle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case MenuShowWindow:
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    /// <summary>移除托盘图标并释放资源。</summary>
    public void Dispose()
    {
        if (_added && _source is not null)
        {
            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _source.Handle,
                uID = TrayIconId,
            };

            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        _source?.Dispose();
        _source = null;
    }
}
