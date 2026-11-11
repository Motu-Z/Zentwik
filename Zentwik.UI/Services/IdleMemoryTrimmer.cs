using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Zentwik.UI.Services;

/// <summary>窗口收进后台后的处理档位。</summary>
public enum IdleTrimMode
{
    /// <summary>不做任何处理。</summary>
    Off = 0,

    /// <summary>降优先级 + EcoQoS + 交还工作集，控件树保留。</summary>
    Light = 1,

    /// <summary>轻档之上再释放非当前页的控件树。</summary>
    Full = 2,
}

/// <summary>
/// 档位的持久化。只属于 UI 进程，不走 Worker 的 ui.json —— 这是纯本地行为，
/// 塞进同步链路要为一条协议命令加字段，收益不成比例。
/// </summary>
public static class IdleTrimSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zentwik", "idle-memory.txt");

    private static IdleTrimMode _cached = IdleTrimMode.Full;

    private static bool _loaded;

    /// <summary>默认重档：大多数用户希望后台的界面尽快让出资源。</summary>
    public static IdleTrimMode Current
    {
        get
        {
            if (_loaded)
                return _cached;

            _loaded = true;

            try
            {
                if (File.Exists(FilePath) &&
                    int.TryParse(File.ReadAllText(FilePath).Trim(), out int raw) &&
                    Enum.IsDefined(typeof(IdleTrimMode), raw))
                {
                    _cached = (IdleTrimMode)raw;
                }
            }
            catch (IOException)
            {
                // 读不到就用默认值
            }

            return _cached;
        }
        set
        {
            _cached = value;
            _loaded = true;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, ((int)value).ToString());
            }
            catch (IOException)
            {
                // 落盘失败不影响本次生效
            }
        }
    }
}

/// <summary>
/// 后台效率模式：窗口不可见时把进程压到「不抢资源」的状态，回来时还原。
///
/// 三件事各管一段 —— EcoQoS 让调度器把线程往小核 / 低效核上放，
/// BelowNormal 让 CPU 时间排在别人后面，EmptyWorkingSet 把物理页交还系统。
/// 工作集那一步是内存数字降得最明显的：隐藏后 160MB 通常落到 30MB 上下，
/// 切回来按需换入，视觉上无感。
/// </summary>
public static class IdleMemoryTrimmer
{
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;

    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        int processInformationSize);

    private static bool _entered;

    /// <summary>是否正处于效率模式。</summary>
    public static bool IsEntered => _entered;

    /// <summary>进入效率模式。重复调用无副作用。</summary>
    public static void Enter()
    {
        if (_entered)
            return;

        _entered = true;

        SetExecutionSpeedThrottle(true);
        SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS);
        TrimWorkingSet();
    }

    /// <summary>回到前台：撤销节流与优先级。工作集由系统按需换入。</summary>
    public static void Leave()
    {
        if (!_entered)
            return;

        _entered = false;

        SetExecutionSpeedThrottle(false);
        SetPriorityClass(GetCurrentProcess(), NORMAL_PRIORITY_CLASS);
    }

    /// <summary>把工作集交还系统。SetProcessWorkingSetSize(-1,-1) 等价 EmptyWorkingSet。</summary>
    public static void TrimWorkingSet() =>
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));

    private static void SetExecutionSpeedThrottle(bool on)
    {
        PROCESS_POWER_THROTTLING_STATE state = new()
        {
            Version = PowerThrottlingVersion,
            ControlMask = PowerThrottlingExecutionSpeed,
            StateMask = on ? PowerThrottlingExecutionSpeed : 0,
        };

        SetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottling,
            ref state,
            Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }
}
