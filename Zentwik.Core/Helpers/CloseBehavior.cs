namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 关闭主窗口时的行为。对应事实卡 §7.3 组二（B1 / B2 / B3）。
    ///
    /// 它要落进 ui.json（<see cref="UiSettings.CloseBehavior"/>），而 ui.json 走
    /// <c>AppJsonContext</c> 的源生成序列化 —— 那个上下文在 Core 里，Core 不能引用 UI。
    /// 放在 UI 侧就只能退化成「存字符串再解析」，那等于把类型安全换成一次运行时 switch。
    ///
    /// 这三个值对应「点 X 之后还剩什么」的三种可能结局：
    ///   什么都不剩（Exit）/ 剩托盘和进程（Tray）/ 只剩后台服务（WorkerOnly）。
    /// 再细分下去都是这三者的组合，组合本身属于「开机自启形态」的问题，
    /// 归 <see cref="AutoStartMode"/> 管，不要塞进这里。
    /// </summary>
    public enum CloseBehavior
    {
        /// <summary>B1：完全退出。UI 退出，并让本次 UI 拉起的 Worker 一起退出。</summary>
        Exit = 0,

        /// <summary>B2：收托盘。销毁 MainWindow，UI 进程存活，托盘图标由 UI 持有。</summary>
        Tray = 1,

        /// <summary>B3：只留 Worker。UI 进程退出，Worker 常驻（无论由谁拉起）。</summary>
        WorkerOnly = 2
    }

    /// <summary>
    /// 一次关闭动作的判定结果。
    ///
    /// 「能不能关」「要不要 Shutdown」「要不要清 MainWindow」是三件互相独立的事，
    /// 而 WPF 的 Closing 事件里一旦写错顺序（比如先 Shutdown 再置空 MainWindow），
    /// 表现是「关掉窗口后进程还在」或「托盘点开弹出两个窗口」这类只能靠手工发现的故障。
    /// 抽成纯函数后，三种模式的结果可以在单元测试里逐个钉住。
    /// </summary>
    /// <param name="Cancel">是否取消本次关闭（当前三种模式都不取消，留给将来的确认弹窗）。</param>
    /// <param name="Shutdown">是否在关闭后结束整个应用（Exit / WorkerOnly 为 true）。</param>
    /// <param name="ClearMainWindow">
    /// 是否把 <c>Application.MainWindow</c> 置空。只有 Tray 需要：
    /// 托盘下次「打开」时要新建窗口，而 MainWindow 还指向已销毁的旧对象。
    /// </param>
    /// <param name="LogMessage">写进日志的一句话，三种模式必须互不相同（见下方策略注释）。</param>
    public readonly record struct CloseDecision(
        bool Cancel,
        bool Shutdown,
        bool ClearMainWindow,
        string LogMessage);

    /// <summary>
    /// 关闭行为的判定规则。纯函数，不碰任何 WPF 类型，因此可以被单元测试直接驱动。
    ///
    /// 两者都是「窗口关掉、UI 进程退出」，差别只在 <c>App.OnExit</c> 里要不要
    /// 给 Worker 发 shutdown。如果日志都写成「已退出」，用户排障时无法区分
    /// 「后台被我关了」和「后台还在跑」—— 而这正是这两个选项存在的唯一意义。
    ///
    /// <see cref="AutoStartMode.WorkerOnly"/>（开机只拉后台，不拉界面）与
    /// <see cref="CloseBehavior.WorkerOnly"/>（关窗口只留后台）是**两个不同维度**的设置。
    /// 日志里必须写清是哪一个，否则同一句「WorkerOnly」会指向两件不相干的事。
    /// </summary>
    public static class CloseBehaviorPolicy
    {
        /// <summary>
        /// 三档的中文说明。界面下拉与日志共用一份，避免两处文案漂移。
        /// 与 <c>AutoStartService.DescribeMode</c> 对称：一个描述「开机拉什么」，
        /// 一个描述「关窗口留什么」。
        /// </summary>
        public static string Describe(CloseBehavior behavior) => behavior switch
        {
            CloseBehavior.Exit => "完全退出",
            CloseBehavior.Tray => "收进托盘",
            CloseBehavior.WorkerOnly => "只留后台服务",
            _ => "未知行为"
        };

        public static CloseDecision Decide(CloseBehavior behavior) => behavior switch
        {
            CloseBehavior.Tray => new CloseDecision(
                Cancel: false,
                Shutdown: false,
                ClearMainWindow: true,
                LogMessage: "[关闭行为] Tray：窗口已销毁，托盘保留，UI 进程与 Worker 都不动"),

            CloseBehavior.WorkerOnly => new CloseDecision(
                Cancel: false,
                Shutdown: true,
                ClearMainWindow: false,
                LogMessage: "[关闭行为] WorkerOnly：UI 进程退出，Worker 常驻（OnExit 不发 shutdown）"),

            _ => new CloseDecision(
                Cancel: false,
                Shutdown: true,
                ClearMainWindow: false,
                LogMessage: "[关闭行为] Exit：UI 进程退出，OnExit 再决定是否让本次拉起的 Worker 一起退")
        };
    }
}
