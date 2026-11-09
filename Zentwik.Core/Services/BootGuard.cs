using System;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 启动护栏的持久化状态（<c>boot-guard.json</c>）。
    /// </summary>
    public sealed class BootGuardState
    {
        /// <summary>
        /// 上次启动在「开始写 SMU」之后没有走到「写完了」。
        /// 只有真正挂死（蓝屏 / 硬断电 / 卡在写入）才会留下这个标记。
        /// </summary>
        public bool Pending { get; set; }

        /// <summary>连续多少次启动留了未清的 <see cref="Pending"/>。</summary>
        public int Failures { get; set; }

        /// <summary>最近一次进入写入窗口的时间（ISO8601）。</summary>
        public string? LastAttemptAt { get; set; }

        /// <summary>最近一次触发拦截的时间（ISO8601）。</summary>
        public string? BlockedAt { get; set; }
    }

    /// <summary>启动护栏的判定结果。</summary>
    /// <param name="Blocked">本次是否禁止自动写入调校。</param>
    /// <param name="Failures">当前连续失败次数。</param>
    /// <param name="RecoveredFromCrash">上次启动是否在写入后没走完。</param>
    public readonly record struct BootGuardSnapshot(bool Blocked, int Failures, bool RecoveredFromCrash);

    /// <summary>
    /// 防砖护栏。开机静默应用调校是唯一会在用户看不见的时候写硬件的动作 ——
    /// 如果那套参数本身让机器起不来，用户就会被锁在一个「每次开机都更砖」的循环里。
    /// </summary>
    /// <remarks>
    /// 导致起不来的可能是蓝屏、可能是硬断电，也可能卡在 SMU 写入里永远不返回。
    /// 这三种情况下进程都没有机会写任何「我失败了」的记录。
    /// 所以只能反着记：**进危险窗口前先落标记，活着出来才擦掉**。
    /// 下次启动看到标记还在，就说明上次没能活着出来。
    ///
    /// 单次可能是用户自己按了电源键、或者系统更新重启。开到 3 次说明这不是巧合。
    /// 拦下来只是**本次不写**，调校数据一条都不删 —— 用户手动在界面上再点一次
    /// 「应用参数」即可恢复（<see cref="MarkHealthy"/> 会把计数清零）。
    /// </remarks>
    public interface IBootGuard
    {
        /// <summary>启动时调用一次，必须早于任何 SMU 写入。</summary>
        BootGuardSnapshot Enter();

        /// <summary>即将写 SMU：落危险标记。</summary>
        void BeginWrite();

        /// <summary>已安全走完：清除标记与计数。</summary>
        void MarkHealthy();
    }

    /// <inheritdoc cref="IBootGuard"/>
    public sealed class BootGuard : IBootGuard
    {
        /// <summary>连续多少次「写入后未正常完成」判定为不稳定。</summary>
        public const int MaxConsecutiveFailures = 3;

        private readonly ILogService _log;
        private readonly IAppPaths _paths;

        private BootGuardState _state = new();

        public BootGuard(ILogService log, IAppPaths paths)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;

            Load();
        }

        private string FilePath => _paths.File("boot-guard.json");

        public BootGuardSnapshot Enter()
        {
            Load();

            bool recovered = _state.Pending;

            if (recovered)
                _state.Failures++;

            // 进来就清：本次要么写完并 MarkHealthy，要么被拦下来，两种都不该继续挂着。
            _state.Pending = false;

            bool blocked = _state.Failures >= MaxConsecutiveFailures;

            if (blocked)
            {
                _state.BlockedAt = DateTime.Now.ToString("o");
                _log.Critical(
                    $"启动护栏触发：连续 {_state.Failures} 次启动在写入调校后未能正常完成，" +
                    "本次跳过自动调校，硬件保持出厂值。确认无问题后可在调校页手动点「应用参数」恢复");
            }
            else if (recovered)
            {
                _log.Warn(
                    $"上次启动在应用调校后未正常完成（连续 {_state.Failures}/{MaxConsecutiveFailures} 次），" +
                    "再累计到上限将自动跳过开机调校");
            }

            Save();

            return new BootGuardSnapshot(blocked, _state.Failures, recovered);
        }

        public void BeginWrite()
        {
            _state.Pending = true;
            _state.LastAttemptAt = DateTime.Now.ToString("o");
            Save();
        }

        public void MarkHealthy()
        {
            // 没写过、也没失败过：不产生无意义的写盘。
            if (!_state.Pending && _state.Failures == 0 && _state.BlockedAt is null)
                return;

            _state.Pending = false;
            _state.Failures = 0;
            _state.BlockedAt = null;

            Save();
        }

        private void Load()
        {
            try
            {
                if (JsonFile.Read(FilePath, AppJsonContext.Default.BootGuardState) is { } loaded)
                    _state = loaded;
            }
            catch
            {
                // 读不出来就当作全新状态：宁可放过一次，也不要在启动阶段抛异常。
                _state = new BootGuardState();
            }
        }

        private void Save()
        {
            try
            {
                JsonFile.Write(FilePath, _state, AppJsonContext.Default.BootGuardState);
            }
            catch (Exception ex)
            {
                // 护栏本身写不进盘时不能拖垮启动。没有标记就没保护，这是已知的降级。
                _log.Warn($"写入启动护栏状态失败：{ex.Message}");
            }
        }
    }
}
