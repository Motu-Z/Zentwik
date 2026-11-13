using System;
using System.IO;
using System.Threading.Tasks;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

public class AdvancedViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppSettings.Instance;

    // 安全

    public bool SmartAntiBrick
    {
        get => _settings.SmartAntiBrick;
        set
        {
            if (_settings.SmartAntiBrick == value) return;

            _settings.SmartAntiBrick = value;

            OnPropertyChanged();
        }
    }

    public bool SmartHibernateAware
    {
        get => _settings.SmartHibernateAware;
        set
        {
            if (_settings.SmartHibernateAware == value) return;

            _settings.SmartHibernateAware = value;

            OnPropertyChanged();
        }
    }

    // 防砖计数
    private int _bootFailures;

    /// <summary>连续异常启动次数。由 Worker 的 BootGuard 维护，进页时问一次。</summary>
    public int BootCount => _bootFailures;

    public string BootCountText => $"{BootCount} 次";

    /// <summary>读一次护栏计数。诊断模式与后台未连上时保持原值。</summary>
    public async Task RefreshBootGuardAsync()
    {
        if (!Services.WorkerBridge.AllowAutoStart)
            return;

        try
        {
            IpcOutcome<BootGuardStatusPayload> outcome =
                await Services.WorkerBridge.Instance.QueryBootGuardAsync().ConfigureAwait(true);

            // 老版本 Worker 不认识这条命令：保持 0 而不是报错，界面不该为此弹气泡。
            if (!outcome.Success || outcome.Value is null || outcome.Value.Failures == _bootFailures)
                return;

            _bootFailures = outcome.Value.Failures;

            OnPropertyChanged(nameof(BootCount));
            OnPropertyChanged(nameof(BootCountText));
        }
        catch (Exception)
        {
            // 读不到就维持原值：这是展示信息，不值得打断用户。
        }
    }

    // 显示

    public int ToastDuration
    {
        get => _settings.ToastDurationSeconds;
        set
        {
            int clamped = Math.Clamp(value, 1, 30);

            if (_settings.ToastDurationSeconds == clamped) return;

            _settings.ToastDurationSeconds = clamped;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ToastDurationText));
        }
    }

    public string ToastDurationText => $"{ToastDuration} 秒";

    // 诊断 / 恢复

    public RelayCommand RestoreSafeDefaultsCommand { get; }

    public RelayCommand RestorePowerDefaultsCommand { get; }

    public RelayCommand RunSelfTestCommand { get; }

    public RelayCommand ExportLogsCommand { get; }

    // 重置

    public RelayCommand DisableAllCommand { get; }

    public RelayCommand ClearAllCommand { get; }

    public AdvancedViewModel()
    {
        // 远程设置覆盖本地后，界面要跟着刷。
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ToastDurationSeconds))
            {
                OnPropertyChanged(nameof(ToastDuration));
                OnPropertyChanged(nameof(ToastDurationText));
            }
            else if (e.PropertyName == nameof(AppSettings.SmartAntiBrick))
            {
                OnPropertyChanged(nameof(SmartAntiBrick));
            }
            else if (e.PropertyName == nameof(AppSettings.SmartHibernateAware))
            {
                OnPropertyChanged(nameof(SmartHibernateAware));
            }
        };

        // 【高危确认】用户 2026-11-04。
        DisableAllCommand = new RelayCommand(_ =>
            ConfirmService.Instance.Request(
                "停用全部功能",
                new[]
                {
                    "自动应用调校参数将被关闭",
                    "智能休眠感知将被关闭",
                    "智能防砖保护将被关闭",
                },
                DisableAll,
                "停用"));

        ClearAllCommand = new RelayCommand(_ =>
            ConfirmService.Instance.Request(
                "清除全部数据",
                new[]
                {
                    "删除全部配置方案",
                    "删除全部已保存的调度规则",
                    "恢复所有设置到默认值",
                },
                ClearAll,
                "清除"));

        RestoreSafeDefaultsCommand = new RelayCommand(_ => Fire(RestoreSafeDefaultsAsync));
        RestorePowerDefaultsCommand = new RelayCommand(_ => Fire(RestorePowerDefaultsAsync));
        RunSelfTestCommand = new RelayCommand(_ => Fire(ExportSelfTestAsync));
        ExportLogsCommand = new RelayCommand(_ => Fire(ExportLogsAsync));
    }

    private void ClearAll() => Fire(ClearAllAsync);

    private static async Task ClearAllAsync()
    {
        var outcome = await Services.WorkerBridge.Instance.FactoryResetAsync().ConfigureAwait(true);

        if (!outcome.Success)
        {
            ToastService.Instance.ShowError(string.IsNullOrEmpty(outcome.Message)
                ? "清除全部数据失败"
                : outcome.Message);
            return;
        }

        // 后台已重置：把设置与三类方案重新读回来，界面不能停在旧值上。
        await AppSettings.Instance.SyncFromWorkerAsync().ConfigureAwait(true);
        await PlanHub.InitializeAllAsync().ConfigureAwait(true);
    }

    private void DisableAll()
    {
        _settings.AutoApplyTuning = false;
        _settings.WatchProcesses = false;
        _settings.SmartHibernateAware = false;
        _settings.SmartAntiBrick = false;

        OnPropertyChanged(nameof(SmartAntiBrick));
        OnPropertyChanged(nameof(SmartHibernateAware));
    }

    private static void Fire(Func<Task> action) => Services.AsyncRunner.FireError(action);

    /// <summary>电源策略五项写回出厂折中值（AC 50 / DC 80），直接落盘并回读。</summary>
    private static async Task RestorePowerDefaultsAsync()
    {
        PowerPolicyDefaults ac = PowerPolicyDefaults.Ac;
        PowerPolicyDefaults dc = PowerPolicyDefaults.Dc;

        ApplyPowerPolicyPayload payload = new()
        {
            Epp = ac.Epp,
            BoostMode = ac.BoostMode,
            MaxProcessorState = ac.MaxProcessorState,
            CoreParkingMin = ac.CoreParkingMin,
            Aspm = ac.Aspm,
            IdleDisable = ac.IdleDisable,
            DcEpp = dc.Epp,
            DcBoostMode = dc.BoostMode,
            DcMaxProcessorState = dc.MaxProcessorState,
            DcCoreParkingMin = dc.CoreParkingMin,
            DcAspm = dc.Aspm,
            DcIdleDisable = dc.IdleDisable
        };

        IpcOutcome<PowerPolicySnapshot> outcome = await Services.WorkerBridge.Instance
            .ApplyPowerPolicyAsync(payload).ConfigureAwait(true);

        if (!outcome.Success)
        {
            ToastService.Instance.ShowError(string.IsNullOrEmpty(outcome.Message)
                ? "恢复电源默认值失败"
                : outcome.Message);
        }
    }

    /// <summary>恢复默认安全参数：CO 归零、FMax 回基线，后台负责写盘并下发。</summary>
    private static async Task RestoreSafeDefaultsAsync()
    {
        var outcome = await Services.WorkerBridge.Instance.ResetTuningSafeAsync().ConfigureAwait(true);

        if (!outcome.Success)
        {
            ToastService.Instance.ShowError(string.IsNullOrEmpty(outcome.Message)
                ? "恢复默认安全参数失败"
                : outcome.Message);
        }
    }

    /// <summary>自检报告：把当前诊断信息快照写到桌面。</summary>
    private static Task ExportSelfTestAsync()
    {
        var logs = new LogsViewModel();
        string path = WriteReport("zentwik-selftest", logs.BuildDiagnostics());

        ToastService.Instance.Show($"自检报告已导出：{path}");
        return Task.CompletedTask;
    }

    /// <summary>导出全部后台日志到桌面。</summary>
    private static async Task ExportLogsAsync()
    {
        var outcome = await Services.WorkerBridge.Instance.SnapshotLogsAsync().ConfigureAwait(true);

        if (!outcome.Success)
        {
            ToastService.Instance.ShowError(string.IsNullOrEmpty(outcome.Message)
                ? "导出日志失败"
                : outcome.Message);
            return;
        }

        string path = WriteReport("zentwik-logs", outcome.Value ?? string.Empty);
        ToastService.Instance.Show($"日志已导出：{path}");
    }

    private static string WriteReport(string prefix, string body)
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = Path.GetTempPath();

        string path = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, body);
        return path;
    }
}
