using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>日志级别筛选：一次只看一档，等同于参考稿的分段组。</summary>
public enum LogFilterMode
{
    All,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 日志页。数据源是 Worker 的 <c>snapshot-logs</c>（最近 50 条，行文本），
/// 不做本地落盘 —— 日志的主副本在 Worker 那边，界面只负责显示与筛选。
/// </summary>
/// <remarks>
/// 【为什么刷新是手动 + 进页自动，而不是订阅推送】
/// 管道是「一个连接一条消息」，没有推送通道。要做实时日志得再加一条长连接协议，
/// 而日志页的价值在于「出问题后回来看」，不在于盯着滚动。手动刷新够用。
/// </remarks>
public sealed class LogsViewModel : ViewModelBase
{
    /// <summary>诊断 / 截图模式下不连后台，构造时直接铺种子。</summary>
    public static bool SuppressWorkerInit { get; set; }

    public ObservableCollection<LogEntryItem> Entries { get; } = new();

    public ObservableCollection<LogEntryItem> FilteredEntries { get; } = new();

    private LogFilterMode _filterMode = LogFilterMode.All;
    private string _searchText = string.Empty;

    /// <summary>
    /// 日志落盘目录。与 Worker 侧 <c>AppPaths.Logs</c> 必须一致：
    /// 这里曾经写成 LocalApplicationData，点「打开目录」打开的是一个空目录，
    /// 而真正的日志在 Roaming 下 —— 用户只会以为日志没写。
    /// </summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zentwik",
        "Logs");

    /// <summary>右栏「文件信息」用。静态属性走不了实例绑定，这里透出一份。</summary>
    public string LogFolderPath => LogDirectory;

    /// <summary>
    /// 右栏显示用的路径。完整路径在 340 宽的栏里会折成两行，
    /// AppData 前缀缩成 <c>%AppData%</c> 正好一行；完整路径放在 ToolTip。
    /// </summary>
    public string LogFolderDisplay
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return appData.Length > 0 && LogDirectory.StartsWith(appData, StringComparison.OrdinalIgnoreCase)
                ? "%AppData%" + LogDirectory[appData.Length..]
                : LogDirectory;
        }
    }

    // ==================== 右栏折叠 ====================

    private bool _isStatsOpen = true;

    /// <summary>
    /// 右侧控制中心是否展开。宽度动画在 code-behind —— 触发器 Storyboard 会被
    /// 离屏截图拍到中间帧，code-behind 可以直接落到终值。
    /// </summary>
    public bool IsStatsOpen
    {
        get => _isStatsOpen;
        set
        {
            if (!SetProperty(ref _isStatsOpen, value)) return;

            StatsStateChanged?.Invoke(value);
        }
    }

    /// <summary>折叠状态变化：参数是「展开」。</summary>
    public event Action<bool>? StatsStateChanged;

    private string _logFolderSizeText = "—";

    /// <summary>日志目录占用。取不到（目录不存在 / 无权限）时显示破折号，不报错。</summary>
    public string LogFolderSizeText
    {
        get => _logFolderSizeText;
        private set => SetProperty(ref _logFolderSizeText, value);
    }

    // ==================== 筛选 ====================

    public LogFilterMode FilterMode
    {
        get => _filterMode;
        set
        {
            if (!SetProperty(ref _filterMode, value)) return;

            OnPropertyChanged(nameof(IsFilterAll));
            OnPropertyChanged(nameof(IsFilterInfo));
            OnPropertyChanged(nameof(IsFilterWarn));
            OnPropertyChanged(nameof(IsFilterError));

            ApplyFilter();
        }
    }

    public bool IsFilterAll
    {
        get => _filterMode == LogFilterMode.All;
        set { if (value) FilterMode = LogFilterMode.All; }
    }

    public bool IsFilterInfo
    {
        get => _filterMode == LogFilterMode.Info;
        set { if (value) FilterMode = LogFilterMode.Info; }
    }

    public bool IsFilterWarn
    {
        get => _filterMode == LogFilterMode.Warn;
        set { if (value) FilterMode = LogFilterMode.Warn; }
    }

    public bool IsFilterError
    {
        get => _filterMode == LogFilterMode.Error;
        set { if (value) FilterMode = LogFilterMode.Error; }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    // ==================== 统计 ====================

    public int TotalCount => Entries.Count;
    public int InfoCount => Entries.Count(e => e.Level == "INFO");
    public int WarnCount => Entries.Count(e => e.Level == "WARN");
    public int ErrorCount => Entries.Count(e => e.Level is "ERROR" or "FATAL");

    /// <summary>最近一条异常：优先 ERROR / FATAL，没有就退到 WARN。</summary>
    public LogEntryItem? LatestIssue =>
        Entries.LastOrDefault(e => e.Level is "ERROR" or "FATAL")
        ?? Entries.LastOrDefault(e => e.Level == "WARN");

    public bool HasIssue => LatestIssue is not null;

    public string LatestIssueTime => LatestIssue?.Time ?? string.Empty;

    public string LatestIssueLevel => LatestIssue?.Level ?? string.Empty;

    public string LatestIssueMessage => LatestIssue?.Message ?? string.Empty;

    // ==================== 命令 ====================

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand ToggleStatsCommand { get; }

    public LogsViewModel()
    {
        // 种子必须在构造函数里：InitializeAsync 有 await，截图 / 自检不等 continuation，
        // 放在那边会拍到一张空列表。
        if (SuppressWorkerInit)
            Seed();

        RefreshCommand = new RelayCommand(_ => Fire(RefreshAsync));
        ClearCommand = new RelayCommand(_ => Fire(ClearAsync));
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics());
        ToggleStatsCommand = new RelayCommand(_ => IsStatsOpen = !IsStatsOpen);

        ApplyFilter();
        UpdateFolderSize();
    }

    /// <summary>页面首次显示时拉一次。</summary>
    public async Task InitializeAsync()
    {
        if (SuppressWorkerInit)
            return;

        await RefreshAsync().ConfigureAwait(true);
    }

    // ==================== 数据链路 ====================

    private async Task RefreshAsync()
    {
        IpcOutcome<string> outcome = await WorkerBridge.Instance.SnapshotLogsAsync().ConfigureAwait(true);

        if (!outcome.Success)
        {
            // 刷新失败是「结果看不见」：列表还是旧内容，不提示的话用户以为已经是最新的。
            ToastService.Instance.ShowError(outcome.Message.Length > 0 ? outcome.Message : "读取日志失败");
            return;
        }

        if (outcome.Value is null)
        {
            // 空串是「确实没有日志」，null 是「后台没给数据」。
            // 后者不能 Parse 成空列表，否则整页日志被静默清掉。
            ToastService.Instance.Show("后台服务没有返回日志数据");
            return;
        }

        Replace(Parse(outcome.Value));
        UpdateFolderSize();
    }

    private async Task ClearAsync()
    {
        Entries.Clear();
        NotifyCounts();
        ApplyFilter();
        UpdateFolderSize();

        if (SuppressWorkerInit)
            return;

        IpcOutcome<string> outcome = await WorkerBridge.Instance.ClearLogsAsync().ConfigureAwait(true);

        // 后台没清掉的话，下次刷新会把同样的条目拉回来 —— 这件事必须让用户知道。
        if (!outcome.Success)
            ToastService.Instance.ShowError(outcome.Message.Length > 0 ? outcome.Message : "后台日志未能清空");
    }

    /// <summary>整体替换（快照是「最近 50 条」，不是增量）。</summary>
    private void Replace(IEnumerable<LogEntryItem> items)
    {
        Entries.Clear();

        foreach (LogEntryItem item in items)
            Entries.Add(item);

        NotifyCounts();
        ApplyFilter();
    }

    /// <summary>
    /// 解析 <c>snapshot-logs</c> 的行文本：<c>yyyy-MM-ddTHH:mm:ss.fff|级别|消息</c>。
    /// </summary>
    /// <remarks>
    /// 用 <c>Split('|', 3)</c> 而不是全切：消息正文里完全可能出现 <c>|</c>
    /// （比如命令行、正则），切成四段以上会把后半句吞掉。
    /// </remarks>
    internal static List<LogEntryItem> Parse(string? payload)
    {
        List<LogEntryItem> items = new();

        if (string.IsNullOrWhiteSpace(payload))
            return items;

        foreach (string raw in payload.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.Length == 0)
                continue;

            string[] parts = line.Split('|', 3);

            if (parts.Length < 3)
                continue;

            items.Add(new LogEntryItem(
                FormatTime(parts[0]),
                parts[1],
                parts[2].Replace("\\n", "\n", StringComparison.Ordinal)));
        }

        return items;
    }

    /// <summary>
    /// 时间只显示时分秒（当天）或月日 + 时分（跨天）—— 列表宽度固定 110，
    /// 塞完整 ISO 时间会把正文挤掉。
    /// </summary>
    private static string FormatTime(string raw)
    {
        if (!DateTime.TryParseExact(
                raw,
                "yyyy-MM-ddTHH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime time))
        {
            return raw;
        }

        return time.Date == DateTime.Today
            ? time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : time.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // ==================== 诊断 ====================

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(BuildDiagnostics());
            ToastService.Instance.Show("诊断信息已复制到剪贴板");
        }
        catch (ExternalException ex)
        {
            // 剪贴板被别的进程锁住时 SetText 会抛，属于可以重试的失败。
            ToastService.Instance.ShowError($"复制失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 诊断信息 = 环境 + 版本 + 后台连通性 + 统计 + 日志尾部。
    ///
    /// 用户在反馈里最常缺的就是这几项，让他自己一项项去翻远不如一次复制。
    /// 这里**不**包含用户名、路径以外的机器信息。
    /// </summary>
    public string BuildDiagnostics()
    {
        StringBuilder builder = new();

        builder.AppendLine("Zentwik 诊断信息");
        builder.AppendLine("----------------------------------------");
        builder.AppendLine($"生成时间    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"应用版本    {AppVersion}");
        builder.AppendLine($"进程架构    {RuntimeInformation.ProcessArchitecture} / 系统 {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"系统版本    {Environment.OSVersion.VersionString}");
        builder.AppendLine($"运行时      {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"后台服务    {WorkerStatus.Instance.StatusText}（{WorkerStatus.Instance.DetailText}）");
        builder.AppendLine($"日志目录    {LogDirectory}");
        builder.AppendLine($"内存条目    {TotalCount}（信息 {InfoCount} / 警告 {WarnCount} / 错误 {ErrorCount}）");
        builder.AppendLine();
        builder.AppendLine($"最近日志（{Entries.Count} 条）");
        builder.AppendLine("----------------------------------------");

        foreach (LogEntryItem item in Entries)
            builder.Append('[').Append(item.Time).Append("] [").Append(item.Level).Append("] ")
                   .AppendLine(item.Message);

        return builder.ToString();
    }

    private static string AppVersion
    {
        get
        {
            Version? version = typeof(LogsViewModel).Assembly.GetName().Version;
            return version is null ? "未知" : version.ToString();
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ToastService.Instance.ShowError($"打开日志目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 重算日志目录占用。只统计顶层文件 —— 日志按天分文件，没有子目录。
    /// 单个文件取长度失败就跳过：文件正在被 Worker 写入时可能锁住。
    /// </summary>
    private void UpdateFolderSize()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                LogFolderSizeText = "0 B";
                return;
            }

            long total = 0;

            foreach (string file in Directory.EnumerateFiles(LogDirectory))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // 正在被写入，这一轮不计入。
                }
            }

            LogFolderSizeText = FormatSize(total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFolderSizeText = "—";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };

    // ==================== 内部 ====================

    private static void Fire(Func<Task> action) => _ = RunSafeAsync(action);

    private static async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show(ex.Message);
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(WarnCount));
        OnPropertyChanged(nameof(ErrorCount));

        OnPropertyChanged(nameof(LatestIssue));
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(LatestIssueTime));
        OnPropertyChanged(nameof(LatestIssueLevel));
        OnPropertyChanged(nameof(LatestIssueMessage));
    }

    private void ApplyFilter()
    {
        List<LogEntryItem> visible = Entries.Where(Matches).ToList();

        FilteredEntries.Clear();

        foreach (LogEntryItem entry in visible)
            FilteredEntries.Add(entry);
    }

    private bool Matches(LogEntryItem entry)
    {
        bool levelOn = _filterMode switch
        {
            LogFilterMode.Info => entry.Level == "INFO",
            LogFilterMode.Warn => entry.Level == "WARN",
            LogFilterMode.Error => entry.Level is "ERROR" or "FATAL",
            _ => true,
        };

        if (!levelOn) return false;

        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return entry.Time.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void Seed()
    {
        Add("09:30:12.442", "INFO", "启动应用调校：读取到已保存配置，16 核，最高频率 4750 MHz");
        Add("09:30:12.458", "INFO", "曲线偏移写入开始：16 核，每 CCD 8 核，SMU 型号 TYPE_CPU4，版本 0x4621A00");
        Add("09:30:12.461", "DEBUG", "核 0 → core=0 ccd=0 mask=0x00000000 值=-18 接受=True");
        Add("09:30:12.463", "DEBUG", "核 1 → core=1 ccd=0 mask=0x00000000 值=-22 接受=True");
        Add("09:30:12.489", "INFO", "启动应用调校：16 核写入并回读一致");
        Add("09:30:12.492", "INFO", "最高频率写入：请求 4750，SMU 接受 True，回读 4750");
        Add("09:30:15.102", "WARN", "绑定失败 ZenForge.exe（错误 5：权限不足），已节流，60 秒内不再重复");
        Add("09:35:00.001", "INFO", "检测到 cs2.exe 启动（PID: 10240），匹配场景规则「极限游戏」");
        Add("09:35:00.015", "INFO", "已切换方案至：极限游戏；已应用进程绑核：CCD0，优先级：高");
        Add("09:40:22.880", "ERROR", "设置进程优先级失败：拒绝访问，目标进程可能受到反作弊保护");
        Add("10:15:00.000", "INFO", "检测到 cs2.exe 退出，恢复默认方案：日常办公");
        Add("10:30:11.500", "WARN", "系统电源策略应用部分失败：链路电源管理超出允许范围，已跳过该项");
        Add("10:31:02.224", "DEBUG", "[perf] 界面渲染：日志列表 363ms");
    }

    private void Add(string time, string level, string message) =>
        Entries.Add(new LogEntryItem(time, level, message));
}

public sealed class LogEntryItem
{
    public string Time { get; }
    public string Level { get; }
    public string Message { get; }

    public LogEntryItem(string time, string level, string message)
    {
        Time = time;
        Level = level;
        Message = message;
    }
}
