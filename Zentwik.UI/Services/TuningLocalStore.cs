using System.IO;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.UI.Services;

/// <summary>
/// 只读 tuning.json：SMU 不可用时用它给界面占位。
/// </summary>
/// <remarks>
/// 【只读】
/// 写盘由 Worker 负责（save-profile-tuning / apply-tuning 都在它那一侧）。
/// UI 再开一个写入口就有两个真源，缓存与磁盘迟早不一致，
/// 表现为「保存成功，重启后值回退」。
///
/// 【为什么复用 Core 的 TuningStore】
/// 它是 tuning.json 的唯一实现（原子替换 + 内存缓存），
/// UI 另写一份 JsonFile 调用等于复制一份解析逻辑。
///
/// 【日志为什么单独一个前缀】
/// 两个进程共用 %AppData%\Zentwik\Logs。同名文件会让先打开的一方独占追加句柄，
/// 后写的一方随机丢日志。UI 用 <c>zentwik-</c>、Worker 用 <c>worker-</c>。
/// </remarks>
internal static class TuningLocalStore
{
    private static readonly Lazy<TuningStore> Store = new(Create);

    private static TuningStore Create()
    {
        // 目录解析只做一次：AppPaths 首次访问会跑一遍旧目录搬迁探测，
        // 建两个实例等于把那次探测重复一遍。
        AppPaths paths = new();

        ILogService log = new LogService(new LogOptions
        {
            FilePrefix = "zentwik-",
            Directory = paths.Logs
        });

        return new TuningStore(log, paths);
    }

    /// <summary>
    /// 读取最后一次下发过的值。没有存档时返回空数组 + 0
    /// （调用方据此保持界面原值，不要清零）。
    /// </summary>
    public static (int[] Margins, uint FMaxMHz) Load()
    {
        try
        {
            TuningStore store = Store.Value;
            store.Load();

            TuningProfile profile = store.Profile;

            return (profile.CurveOptimizer ?? Array.Empty<int>(), profile.FMaxMHz);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不出来不等于「没有配置」—— 但界面此时无法区分，按「无存档」处理，
            // 真正的原因由 TuningStore 自己记日志。
            return (Array.Empty<int>(), 0);
        }
    }
}
