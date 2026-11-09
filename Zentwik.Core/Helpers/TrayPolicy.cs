using System;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 托盘生命周期的一次决策结果。
    ///
    /// 刻意做成三值而不是 bool：调用方需要区分「什么都不做」和「真的要去建/去撤」，
    /// 用一个 bool 表达就会出现「每次刷新都 Dispose 一遍再重建」的实现，
    /// 用户能看到图标在闪。
    /// </summary>
    public enum TrayAction
    {
        /// <summary>当前状态已经正确，不动资源。</summary>
        None = 0,

        /// <summary>需要创建托盘图标。</summary>
        Create = 1,

        /// <summary>需要销毁托盘图标。</summary>
        Dispose = 2
    }

    /// <summary>
    /// 托盘图标「该不该存在」的纯决策逻辑。
    ///
    /// 真正的资源是 WinForms 的 <c>NotifyIcon</c>，只有 UI 进程能持有；
    /// 但「什么时候该有」是一条纯函数规则，与窗口、消息循环都无关。
    /// 把它抽到这里，测试工程（只引用 Core + Worker）就能直接钉住
    /// 「条件为真 → 建 / 条件为假 → 撤」这两条分支，不必拉起一个 WPF 进程。
    ///
    /// 两个设置回答的是两个不同的问题：
    ///   · <see cref="AutoStartMode.WorkerWithTray"/> 管「开机时拉起什么」——
    ///     计划任务带 <c>--tray</c>，这次启动压根不该有窗口，托盘是唯一入口；
    ///   · <see cref="CloseBehavior.Tray"/> 管「关窗口时留什么」——
    ///     窗口被销毁，进程要继续活着，用户得有个地方点回来。
    /// 任一条件成立都需要托盘；两个都不成立时，托盘只会成为一个多余的第二入口。
    /// </summary>
    public static class TrayPolicy
    {
        /// <summary>
        /// 当前设置下是否应该存在托盘图标。
        ///
        /// 注意这里**不**看 <c>--tray</c> 命令行参数：那是一次启动的临时形态，
        /// 不属于持久化设置，由 UI 侧在调用 <see cref="Decide"/> 时并入 forceShow。
        /// </summary>
        public static bool ShouldShowTray(AutoStartMode mode, CloseBehavior behavior)
            => mode == AutoStartMode.WorkerWithTray || behavior == CloseBehavior.Tray;

        /// <summary>
        /// 由「应不应该有」与「现在有没有」推出要执行的动作。
        /// </summary>
        public static TrayAction Decide(bool shouldShow, bool exists)
        {
            if (shouldShow)
                return exists ? TrayAction.None : TrayAction.Create;

            return exists ? TrayAction.Dispose : TrayAction.None;
        }

        /// <summary>
        /// 给日志用的一句话说明。排障时最常见的问题是「为什么没有托盘」，
        /// 把两个开关的实际取值一起打出来，比只打一句结论有用。
        /// </summary>
        public static string Describe(AutoStartMode mode, CloseBehavior behavior, bool exists)
            => $"托盘：自启形态={mode}，关闭行为={behavior}，应显示={ShouldShowTray(mode, behavior)}，" +
               $"当前{(exists ? "已创建" : "未创建")}";
    }
}
