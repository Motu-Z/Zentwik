using System.IO;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.UI.Services;

/// <summary>只读 tuning.json：SMU 不可用时用它给界面占位</summary>
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

    /// <summary>读取最后一次下发过的值</summary>
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
