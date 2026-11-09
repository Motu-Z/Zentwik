namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 开机自启的形态。None 表示不注册计划任务。
    ///
    /// 双进程架构下「开机自启」至少有三种合理形态，它们对用户的意义完全不同：
    ///   · WorkerOnly      —— 只拉起后台（写 SMU、跑调度），一个界面都不弹；
    ///   · WorkerWithTray  —— 后台 + 托盘图标，用户想用再点开；
    ///   · WorkerWithUI    —— 后台 + 直接开主界面。
    /// 旧版用一个 bool 把后两种压成了同一种（永远开窗口），于是想要「静默常驻」的用户
    /// 只能忍着每次开机弹窗，或者干脆关掉自启 —— 两种都不是他想要的。
    ///
    /// 界面要不要出现由 Worker 按本枚举决定（见 Worker 的 <c>--open-ui</c> / <c>--tray</c>
    /// 命令行开关）。计划任务里直接写 UI 的路径会让「谁负责常驻」这件事再次分叉。
    /// </summary>
    public enum AutoStartMode
    {
        /// <summary>不注册计划任务。</summary>
        None = 0,

        /// <summary>只拉起 Worker：无窗口、无托盘，任务管理器之外看不见它。</summary>
        WorkerOnly = 1,

        /// <summary>拉起 Worker，并由它再拉起 UI 的托盘（UI 进程持有 NotifyIcon）。</summary>
        WorkerWithTray = 2,

        /// <summary>拉起 Worker，并由它再拉起 UI 主窗口。旧版 bool AutoStart=true 对应的形态。</summary>
        WorkerWithUI = 3
    }
}
