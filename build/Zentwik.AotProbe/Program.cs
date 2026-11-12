using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zentwik.Core.DependencyInjection;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;

namespace Zentwik.AotProbe;

/// <summary>
/// Core 的 AOT 冒烟测试。
///
/// 只做只读探测：读 CPU Set 拓扑、扫进程快照、走一遍 IPC 契约的序列化与版本协商、
/// 发一次 SMU 测试消息。不写任何系统设置，不改任何用户配置。
/// </summary>
internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok)
            _failures++;

        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Zentwik.AotProbe", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        Console.WriteLine($"[INFO] root={root}");
        Console.WriteLine(
            $"[INFO] runtime={Environment.Version}, aot={IsAot()}, " +
            $"dynamicCompiled={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled}, " +
            $"dynamicSupported={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");

        ProbeJson(root);
        ProbeLog(root);
        ProbeIpc();
        ProbeProcessPriority();
        ProbeProcessSnapshot();
        ProbeContainer(root);

        Console.WriteLine($"[SUMMARY] failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 是否跑在 Native AOT 产物里。
    ///
    /// 不能用 RuntimeFeature 的两个开关判断：PublishAot=true 会让 SDK 在构建期
    /// 就把 <c>IsDynamicCodeSupported</c> 写进 runtimeconfig，JIT 跑也会报 false。
    /// 可靠判据只有 <c>Assembly.Location</c> —— AOT 产物没有 IL 文件，恒为空串。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "探测 AOT 就是要观察 Location 为空；这条警告描述的正是被测行为本身。")]
    private static bool IsAot()
    {
        try
        {
            return string.IsNullOrEmpty(typeof(Program).Assembly.Location);
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- JSON

    private static void ProbeJson(string root)
    {
        string path = Path.Combine(root, "ui.json");

        UiSettings source = new()
        {
            ThemeMode = AppThemeMode.Dark,
            AutoStartMode = AutoStartMode.WorkerWithTray,
            CloseBehavior = CloseBehavior.Tray,
            CustomAccentHex = "#60CDFF",
        };

        JsonFile.Write(path, source, AppJsonContext.Default.UiSettings);
        UiSettings? restored = JsonFile.Read(path, AppJsonContext.Default.UiSettings);

        // 枚举必须序列化成**名称**而不是数字：数字形态在配置文件里既不可读，
        // 也会让「往枚举中间插一个值」变成一次静默的语义漂移。
        Check("json.roundtrip",
            restored is not null
                && restored.ThemeMode == AppThemeMode.Dark
                && restored.AutoStartMode == AutoStartMode.WorkerWithTray
                && restored.CloseBehavior == CloseBehavior.Tray,
            $"theme={restored?.ThemeMode}, autoStart={restored?.AutoStartMode}, close={restored?.CloseBehavior}");

        // 键名必须是 PascalCase：老配置全靠这个形状才读得出来。
        string raw = File.ReadAllText(path);
        Check("json.pascalCase", raw.Contains("\"ThemeMode\"", StringComparison.Ordinal),
            raw.ReplaceLineEndings(" ")[..Math.Min(72, raw.Length)]);

        Check("json.enumAsString",
            raw.Contains("\"WorkerWithTray\"", StringComparison.Ordinal)
                && raw.Contains("\"Tray\"", StringComparison.Ordinal),
            raw.ReplaceLineEndings(" ")[..Math.Min(120, raw.Length)]);
    }

    // ---------------------------------------------------------------- 日志

    private static void ProbeLog(string root)
    {
        LogOptions options = new()
        {
            Directory = Path.Combine(root, "Logs"),
            Verbose = true,
            MaxEntries = 200,
        };

        using LogService log = new(options);

        for (int index = 0; index < 5; index++)
            log.Info($"第 {index} 条");

        log.Warn("一条警告");
        log.Error("一条错误");
        log.Critical("一条致命错误");
        log.Flush();

        Check("log.counters",
            log.CountInfo == 5 && log.CountWarn == 1 && log.CountError == 1 && log.CountCritical == 1,
            $"info={log.CountInfo}, warn={log.CountWarn}, error={log.CountError}, critical={log.CountCritical}");

        Check("log.capacity", log.Count <= 200, $"count={log.Count}, 上限=200");

        // Flush 之后目录与文件应当已存在；轮询几百毫秒是为了容忍写盘线程的尾部延迟。
        string[] files = [];
        for (int attempt = 0; attempt < 40 && files.Length == 0; attempt++)
        {
            if (Directory.Exists(options.Directory))
                files = Directory.GetFiles(options.Directory, "*.log");

            if (files.Length == 0)
                Thread.Sleep(25);
        }
        Check("log.file",
            files.Length > 0 && File.ReadAllLines(files[0]).Length >= 8,
            files.Length > 0
                ? $"files={files.Length}, lines={File.ReadAllLines(files[0]).Length}"
                : $"未生成日志文件（目录存在={Directory.Exists(options.Directory)}, LogDirectory={log.LogDirectory}）");

        Check("log.snapshot", log.Snapshot().Count == log.Count, $"snapshot={log.Snapshot().Count}");
    }

    // ---------------------------------------------------------------- IPC 契约

    /// <summary>
    /// 三条只依赖 <see cref="WorkerPipeContract"/> 与源生成序列化的检查。
    ///
    /// 不建管道、不起进程：真正的往返要等批次三 Worker 落地后才有对端。
    /// 这里覆盖的是「契约本身在 AOT 下能否完成序列化往返」与「版本判定是否可判定」，
    /// 也就是 source-generated <see cref="IpcJsonContext"/> 有没有被裁剪掉。
    /// </summary>
    private static void ProbeIpc()
    {
        PipeRequest request = new()
        {
            Version = WorkerPipeContract.ProtocolVersion,
            Command = WorkerPipeContract.CommandPing,
            Payload = null
        };

        string json = JsonSerializer.Serialize(request, IpcJsonContext.Default.PipeRequest);
        PipeRequest? restored = JsonSerializer.Deserialize(json, IpcJsonContext.Default.PipeRequest);

        Check("ipc.roundtrip",
            restored is not null
                && restored.Version == request.Version
                && string.Equals(restored.Command, request.Command, StringComparison.Ordinal),
            $"version={restored?.Version}, command={restored?.Command ?? "(null)"}, bytes={json.Length}");

        bool matched = WorkerPipeContract.ValidateVersion(
            WorkerPipeContract.ProtocolVersion, out string matchedError);
        Check("ipc.versionMatch", matched && matchedError.Length == 0,
            matched
                ? $"version={WorkerPipeContract.ProtocolVersion}, error=空"
                : $"error={matchedError}");

        bool rejectedZero = WorkerPipeContract.ValidateVersion(0, out string zeroError);
        bool rejectedFuture = WorkerPipeContract.ValidateVersion(2, out string futureError);
        bool rejectedNegative = WorkerPipeContract.ValidateVersion(-1, out string negativeError);
        bool allRejected = !rejectedZero && !rejectedFuture && !rejectedNegative;

        // 三条失败路径必须给同一句话：文案分叉会让「UI 提示重装、Worker 只说 invalid」重现。
        bool sameMessage =
            string.Equals(zeroError, WorkerPipeContract.VersionMismatchMessage, StringComparison.Ordinal)
            && string.Equals(futureError, WorkerPipeContract.VersionMismatchMessage, StringComparison.Ordinal)
            && string.Equals(negativeError, WorkerPipeContract.VersionMismatchMessage, StringComparison.Ordinal);

        Check("ipc.versionMismatch", allRejected && sameMessage,
            $"0/2/-1 全部拒绝={allRejected}, 文案一致={sameMessage}");
    }

    // ---------------------------------------------------------------- 进程优先级

    /// <summary>
    /// 只保留 <see cref="ProcessPriority"/> 的文案映射。
    /// 原电源索引相关检查已随批次 A-2 的电源子系统删除一并移除。
    /// </summary>
    private static void ProbeProcessPriority()
    {
        Check("power.priorityLabel", ProcessPriority.FromBasePriority(8).Length > 0,
            ProcessPriority.FromBasePriority(8));
    }

    // ---------------------------------------------------------------- 进程快照

    /// <summary>
    /// 这条路径覆盖 Toolhelp32 + OpenProcess + QueryFullProcessImageNameW，
    /// 也就是本轮从 DllImport 改成源生成封送、并从 StringBuilder / ByValTStr
    /// 改成指针与定长缓冲区的三处关键改动。
    /// </summary>
    private static void ProbeProcessSnapshot()
    {
        LogOptions options = new()
        {
            Directory = Path.Combine(Path.GetTempPath(), "Zentwik.AotProbe", "snapshot-logs"),
        };

        using LogService log = new(options);
        ProcessRefresher refresher = new(log);
        refresher.Sample();

        IReadOnlyList<ProcessRefresher.Entry> snapshot = refresher.Latest;
        Check("process.snapshot", snapshot.Count > 0, $"进程数={snapshot.Count}");

        int withName = snapshot.Count(entry => !string.IsNullOrWhiteSpace(entry.Name));
        Check("process.nameMarshaling", withName == snapshot.Count, $"有效进程名={withName}/{snapshot.Count}");

        int withPath = snapshot.Count(entry => entry.Path.Length > 0);
        Check("process.pathMarshaling", withPath > 0, $"已解析路径={withPath}/{snapshot.Count}");

        ProcessRefresher.Entry? sample = snapshot.FirstOrDefault(entry => entry.Path.Length > 0);
        if (sample is not null)
        {
            Check("process.pathShape",
                sample.Path.StartsWith(@"\\?\", StringComparison.Ordinal) || Path.IsPathRooted(sample.Path),
                sample.Path);
        }
    }

    // ---------------------------------------------------------------- 容器

    private static void ProbeContainer(string root)
    {
        ServiceCollection services = new();
        services.AddZentwikCore(
            configureLog: options => options.Directory = Path.Combine(root, "ContainerLogs"));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        IAppPaths paths = provider.GetRequiredService<IAppPaths>();
        Check("di.paths", paths.Root.Length > 0, paths.Root);

        ILogService log = provider.GetRequiredService<ILogService>();
        log.Info("AOT 探针：容器已构建");
        Check("di.log", log.Count > 0, $"entries={log.Count}");

        Check("di.maskStore", provider.GetRequiredService<IMaskStore>().Masks.Count >= 0,
            $"masks={provider.GetRequiredService<IMaskStore>().Masks.Count}");
        Check("di.bindingStore", provider.GetRequiredService<ISchedulingStore>().Profile is not null, "已加载");
        Check("di.tuningStore", provider.GetRequiredService<ITuningStore>().Profile is not null, "已加载");
        Check("di.uiSettings", provider.GetRequiredService<IUiSettingsStore>().Settings is not null, "已加载");

        // 只构造，不查计划任务：IsEnabled 会拉起 schtasks.exe，
        // 构建机上的安全策略把它列进了程序黑名单，跑一次就被拦一次。
        // 构造本身已经覆盖了 GetOEMCP（ConsoleEncoding 静态字段初始化）。
        _ = provider.GetRequiredService<AutoStartService>();
        Check("di.autoStart", true, "已构造（控制台代码页探测完成）");

        // SmuReader 放在最后：它的构造会真的去开 PawnIO 驱动。
        // 若 SMU 通道被裁剪或内部用了动态代码生成，异常就在这里冒出来。
        // 注意这不是「被裁剪」的证明 —— 没装驱动时它也返回 false。
        try
        {
            SmuReader smu = provider.GetRequiredService<SmuReader>();
            Check("smu.construct", true,
                $"IsAvailable={smu.IsAvailable}, CreatedCount={SmuReader.CreatedCount}, LastError={smu.LastError ?? "无"}");
        }
        catch (Exception ex)
        {
            Check("smu.construct", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            CpuSetScheduler binder = provider.GetRequiredService<CpuSetScheduler>();
            binder.ResetTopology();
            bool ccd0 = binder.IsCcdAvailable(0);
            Check("cpuset.topology", true, $"CCD0 可用={ccd0}");
        }
        catch (Exception ex)
        {
            Check("cpuset.topology", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
