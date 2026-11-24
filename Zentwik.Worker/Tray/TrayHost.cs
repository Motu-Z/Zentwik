using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Worker.Tray
{
    /// <summary>
    /// 后台服务自绘的托盘：1×1 隐藏顶层窗口 + Shell_NotifyIcon + 原生菜单。
    /// </summary>
    /// <remarks>
    /// 用顶层窗口而不是 message-only：TaskbarCreated 与主题变化都是广播，只送到顶层窗口。
    /// 窗口过程必须用委托 + GetFunctionPointerForDelegate —— UnmanagedCallersOnly 方法里不能调托管代码，
    /// 而这里要回落到 DefWindowProcW 并驱动菜单。
    /// </remarks>
    internal sealed class TrayHost : IDisposable
    {
        private const string WindowClassName = "ZentwikWorkerTray";

        private const int IconId = 1;

        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int SettingPollIntervalMs = 5000;

        private static TrayHost? _current;

        private static bool _classRegistered;

        /// <summary>委托必须长期持有，否则窗口过程地址会失效。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly WindowProcDelegate WindowProcCallback = WindowProc;

        private readonly ILogService _log;

        private readonly TrayIconSet _onDark = TrayIconSet.Load("Zentwik.Worker.tray-on-dark.bin");

        private readonly TrayIconSet _onLight = TrayIconSet.Load("Zentwik.Worker.tray-on-light.bin");

        private IntPtr _hwnd;

        private IntPtr _icon;

        private int _iconSize;

        private bool _added;

        private bool _onDarkTray = true;

        private int _taskbarCreatedMessage;

        public TrayHost(ILogService log)
        {
            _log = log;
        }

        /// <summary>右键那一刻现算菜单。</summary>
        public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

        /// <summary>切方案。返回 null 表示不弹回执（成功且用户关掉了托盘通知）。</summary>
        public Func<PlanKind, string, Task<string?>>? ApplyPlan { get; set; }

        public Action? ShowRequested { get; set; }

        public Action? ExitRequested { get; set; }

        /// <summary>当前设置下图标是否该显示；设置保存后由主线程重问一次。</summary>
        public Func<bool>? ShouldShow { get; set; }

        public bool IsAvailable => _added;

        /// <summary>建隐藏窗口与消息通道。图标是否显示由 <see cref="SetVisible"/> 决定。</summary>
        public bool Start()
        {
            _current = this;

            if (!RegisterWindowClass())
            {
                _log.Warn($"托盘：注册窗口类失败 err={Marshal.GetLastWin32Error()}");
                return false;
            }

            _hwnd = TrayNative.CreateWindowExW(
                WS_EX_TOOLWINDOW,
                WindowClassName,
                "Zentwik",
                0,
                -32000,
                -32000,
                1,
                1,
                IntPtr.Zero,
                IntPtr.Zero,
                TrayNative.GetModuleHandleW(IntPtr.Zero),
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _log.Warn($"托盘：创建消息窗口失败 err={Marshal.GetLastWin32Error()}");
                return false;
            }

            _taskbarCreatedMessage = TrayNative.RegisterWindowMessageW("TaskbarCreated");

            AllowMessages();

            return true;
        }

        /// <summary>显示或撤掉托盘图标。设置变化后由主线程调用，不重建窗口。</summary>
        public unsafe void SetVisible(bool visible)
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            if (visible)
            {
                if (_onDark.IsEmpty || _onLight.IsEmpty)
                {
                    _log.Warn("托盘：图标资源缺失，跳过显示");
                    return;
                }

                CheckEnvironment(force: true);

                if (!_added)
                {
                    _added = AddIcon();
                    _log.Info(_added ? "托盘：已显示" : "托盘：Shell_NotifyIcon 添加失败");
                }

                return;
            }

            if (!_added)
            {
                return;
            }

            TrayNative.NOTIFYICONDATAW data = new()
            {
                cbSize = sizeof(TrayNative.NOTIFYICONDATAW),
                hWnd = _hwnd,
                uID = IconId,
            };

            TrayNative.Shell_NotifyIconW(TrayNative.NIM_DELETE, ref data);
            _added = false;
            _log.Info("托盘：已撤下");
        }

        /// <summary>从任意线程请求重评估图标是否该显示。</summary>
        public void PostReevaluate()
        {
            if (_hwnd != IntPtr.Zero)
            {
                TrayNative.PostMessageW(_hwnd, TrayNative.WM_REEVALUATE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// <summary>主线程消息循环：等停止信号，顺带处理窗口消息与设置轮询。</summary>
        public void RunMessageLoop(WaitHandle stopSignal)
        {
            IntPtr handle = stopSignal.SafeWaitHandle.DangerousGetHandle();

            while (true)
            {
                uint result = TrayNative.MsgWaitForMultipleObjects(
                    1, ref handle, false, SettingPollIntervalMs, TrayNative.QS_ALLINPUT);

                if (result == TrayNative.WAIT_OBJECT_0)
                {
                    return;
                }

                if (result == TrayNative.WAIT_OBJECT_0 + 1)
                {
                    while (TrayNative.PeekMessageW(
                        out TrayNative.MSG msg, IntPtr.Zero, 0, 0, TrayNative.PM_REMOVE))
                    {
                        TrayNative.TranslateMessage(ref msg);
                        TrayNative.DispatchMessageW(ref msg);
                    }

                    continue;
                }

                // 超时：主题与缩放的变化不一定能跨完整性收到广播，定时空转核对一次。
                CheckEnvironment(force: false);
            }
        }

        /// <summary>托盘气泡。窗口收起时这是唯一能被看见的结果回执。</summary>
        public unsafe void ShowBalloon(string title, string text)
        {
            if (!_added)
            {
                return;
            }

            TrayNative.NOTIFYICONDATAW data = new()
            {
                cbSize = sizeof(TrayNative.NOTIFYICONDATAW),
                hWnd = _hwnd,
                uID = IconId,
                uFlags = TrayNative.NIF_INFO,
                uTimeoutOrVersion = 5000,
            };

            CopyText(data.szInfo, 256, text);
            CopyText(data.szInfoTitle, 64, title);

            TrayNative.Shell_NotifyIconW(TrayNative.NIM_MODIFY, ref data);
        }

        private unsafe bool RegisterWindowClass()
        {
            if (_classRegistered)
            {
                return true;
            }

            TrayNative.WNDCLASSEXW windowClass = new()
            {
                cbSize = sizeof(TrayNative.WNDCLASSEXW),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcCallback),
                hInstance = TrayNative.GetModuleHandleW(IntPtr.Zero),
                hCursor = TrayNative.LoadCursorW(IntPtr.Zero, (IntPtr)TrayNative.IDC_ARROW),
                lpszClassName = Marshal.StringToHGlobalUni(WindowClassName),
            };

            if (TrayNative.RegisterClassExW(ref windowClass) == 0)
            {
                return false;
            }

            _classRegistered = true;
            return true;
        }

        /// <summary>UIPI：不放行则点击无响应、Explorer 重启后图标回不来。</summary>
        private void AllowMessages()
        {
            uint[] messages =
            {
                TrayNative.WM_TRAY,
                TrayNative.WM_SETTINGCHANGE,
                TrayNative.WM_DISPLAYCHANGE,
                (uint)_taskbarCreatedMessage,
            };

            foreach (uint message in messages)
            {
                if (message == 0)
                {
                    continue;
                }

                if (!TrayNative.ChangeWindowMessageFilterEx(
                    _hwnd, message, TrayNative.MSGFLT_ALLOW, IntPtr.Zero))
                {
                    _log.Warn($"托盘：消息放行失败 0x{message:X} err={Marshal.GetLastWin32Error()}");
                }
            }
        }

        private unsafe bool AddIcon()
        {
            TrayNative.NOTIFYICONDATAW data = new()
            {
                cbSize = sizeof(TrayNative.NOTIFYICONDATAW),
                hWnd = _hwnd,
                uID = IconId,
                uFlags = TrayNative.NIF_MESSAGE | TrayNative.NIF_ICON | TrayNative.NIF_TIP,
                uCallbackMessage = TrayNative.WM_TRAY,
                hIcon = _icon,
            };

            CopyText(data.szTip, 128, "Zentwik");

            _added = TrayNative.Shell_NotifyIconW(TrayNative.NIM_ADD, ref data);
            return _added;
        }

        private unsafe void ModifyIcon()
        {
            TrayNative.NOTIFYICONDATAW data = new()
            {
                cbSize = sizeof(TrayNative.NOTIFYICONDATAW),
                hWnd = _hwnd,
                uID = IconId,
                uFlags = TrayNative.NIF_ICON,
                hIcon = _icon,
            };

            TrayNative.Shell_NotifyIconW(TrayNative.NIM_MODIFY, ref data);
        }

        /// <summary>按任务栏明暗与系统小图标尺寸重选一档；没变化就不动资源。</summary>
        private void CheckEnvironment(bool force)
        {
            bool dark = IsTaskbarDark();
            int size = Math.Clamp(TrayNative.GetSystemMetrics(TrayNative.SM_CXSMICON), 16, 48);

            if (!force && dark == _onDarkTray && size == _iconSize)
            {
                return;
            }

            IntPtr newIcon = (dark ? _onDark : _onLight).CreateHIcon(size);

            if (newIcon == IntPtr.Zero)
            {
                return;
            }

            IntPtr previous = _icon;

            _icon = newIcon;
            _iconSize = size;
            _onDarkTray = dark;

            if (_added)
            {
                ModifyIcon();
            }

            if (previous != IntPtr.Zero)
            {
                TrayNative.DestroyIcon(previous);
            }
        }

        private static bool IsTaskbarDark()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                // 任务栏跟随「Windows 模式」，与应用模式是两个键。
                if (key?.GetValue("SystemUsesLightTheme") is int light)
                {
                    return light == 0;
                }
            }
            catch (Exception)
            {
            }

            return true;
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            TrayHost? host = _current;

            if (host is not null && msg == TrayNative.WM_TRAY)
            {
                switch ((int)(lParam.ToInt64() & 0xFFFF))
                {
                    case TrayNative.WM_LBUTTONUP:
                    case TrayNative.WM_LBUTTONDBLCLK:
                        host.ShowRequested?.Invoke();
                        return IntPtr.Zero;

                    case TrayNative.WM_RBUTTONUP:
                        host.ShowMenu();
                        return IntPtr.Zero;
                }
            }

            if (host is not null && host._taskbarCreatedMessage != 0 && msg == (uint)host._taskbarCreatedMessage)
            {
                // Explorer 重启后图标全部丢失，必须重新加。
                if (host._added)
                {
                    host._added = host.AddIcon();
                }

                return IntPtr.Zero;
            }

            if (msg == TrayNative.WM_REEVALUATE)
            {
                host?.SetVisible(host.ShouldShow?.Invoke() ?? false);
                return IntPtr.Zero;
            }

            if (msg == TrayNative.WM_SETTINGCHANGE || msg == TrayNative.WM_DISPLAYCHANGE)
            {
                host?.CheckEnvironment(force: false);
                return TrayNative.DefWindowProcW(hwnd, msg, wParam, lParam);
            }

            return TrayNative.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        /// <summary>原生右键菜单。TPM_RETURNCMD 同步取命令，点空白返回 0。</summary>
        private void ShowMenu()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            Dictionary<int, string> commands = new();
            int nextId = 1;

            IReadOnlyList<TrayMenuItem> items =
                MenuProvider?.Invoke() ?? TrayMenu.Build(Array.Empty<TrayPlanGroup>());

            IntPtr menu = BuildMenu(items, commands, ref nextId);

            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                foreach (KeyValuePair<int, string> pair in commands)
                {
                    if (pair.Value == TrayMenu.KeyShow)
                    {
                        TrayNative.SetMenuDefaultItem(menu, (uint)pair.Key, 0);
                        break;
                    }
                }

                TrayNative.GetCursorPos(out TrayNative.POINT point);
                TrayNative.SetForegroundWindow(_hwnd);

                uint flags = TrayNative.TPM_LEFTALIGN
                             | TrayNative.TPM_BOTTOMALIGN
                             | TrayNative.TPM_RIGHTBUTTON
                             | TrayNative.TPM_RETURNCMD
                             | TrayNative.TPM_NONOTIFY;

                int command = TrayNative.TrackPopupMenuEx(
                    menu, flags, point.X, point.Y, _hwnd, IntPtr.Zero);

                TrayNative.PostMessageW(_hwnd, TrayNative.WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (command > 0 && commands.TryGetValue(command, out string? key))
                {
                    Invoke(key);
                }
            }
            finally
            {
                // 子菜单句柄随父菜单一起释放。
                TrayNative.DestroyMenu(menu);
            }
        }

        private static IntPtr BuildMenu(
            IReadOnlyList<TrayMenuItem> items, Dictionary<int, string> commands, ref int nextId)
        {
            IntPtr menu = TrayNative.CreatePopupMenu();

            if (menu == IntPtr.Zero)
            {
                return menu;
            }

            foreach (TrayMenuItem item in items)
            {
                if (item.IsSeparator)
                {
                    TrayNative.AppendMenuW(menu, TrayNative.MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }

                if (item.Children is { Count: > 0 })
                {
                    IntPtr submenu = BuildMenu(item.Children, commands, ref nextId);
                    TrayNative.AppendMenuW(
                        menu, TrayNative.MF_POPUP | TrayNative.MF_STRING, submenu, item.Label);
                    continue;
                }

                int id = nextId++;
                commands[id] = item.Key;

                uint flags = TrayNative.MF_STRING | (item.IsChecked ? (uint)TrayNative.MF_CHECKED : 0u);
                TrayNative.AppendMenuW(menu, flags, new IntPtr(id), item.Label);
            }

            return menu;
        }

        private void Invoke(string key)
        {
            switch (key)
            {
                case TrayMenu.KeyShow:
                    ShowRequested?.Invoke();
                    return;

                case TrayMenu.KeyExit:
                    ExitRequested?.Invoke();
                    return;
            }

            if (!TrayMenu.TryParsePlanKey(key, out PlanKind kind, out string id) || ApplyPlan is null)
            {
                return;
            }

            // 写 SMU 会阻塞消息循环，挪到线程池。
            _ = Task.Run(async () =>
            {
                try
                {
                    string? message = await ApplyPlan(kind, id).ConfigureAwait(false);

                    if (message is not null)
                    {
                        ShowBalloon(kind == PlanKind.Power ? "电源方案" : "已应用方案", message);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"托盘：应用方案失败 {ex.GetType().Name}: {ex.Message}");
                    ShowBalloon("应用方案失败", ex.Message);
                }
            });
        }

        private static unsafe void CopyText(char* destination, int capacity, string value)
        {
            int length = Math.Min(value.Length, capacity - 1);

            for (int i = 0; i < length; i++)
            {
                destination[i] = value[i];
            }

            destination[length] = '\0';
        }

        public unsafe void Dispose()
        {
            if (_added)
            {
                TrayNative.NOTIFYICONDATAW data = new()
                {
                    cbSize = sizeof(TrayNative.NOTIFYICONDATAW),
                    hWnd = _hwnd,
                    uID = IconId,
                };

                TrayNative.Shell_NotifyIconW(TrayNative.NIM_DELETE, ref data);
                _added = false;
            }

            if (_icon != IntPtr.Zero)
            {
                TrayNative.DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                TrayNative.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        }
    }
}
