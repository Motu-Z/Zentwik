using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 界面设置的读写体（自启形态 / 关闭行为 / 三个后台开关 / 提示时长）。
    /// </summary>
    /// <remarks>
    /// null = 本次不改这一项。界面只发自己那份完整状态，但让字段可空是为了让
    /// 以后的单项修改（比如只拨一个开关）不必把其余项一起回写 ——
    /// 整份覆盖会把另一侧刚改过、界面还没读到的值冲掉。
    ///
    /// <c>AutoStartMode</c> / <c>CloseBehavior</c> 在线上是名称字面量。序号一旦调整，
    /// 老 UI 与新 Worker 之间的 1 会指向另一种行为，用户看到的是
    /// 「选了收进托盘、关窗口却直接退出」这类无法归因的错位。
    /// </remarks>
    public sealed class UiSettingsPayload
    {
        /// <summary>开机自启形态：None / WorkerOnly / WorkerWithTray / WorkerWithUI。</summary>
        [JsonPropertyName("autoStartMode")]
        public string? AutoStartMode { get; set; }

        /// <summary>关闭主窗口的行为：Exit / Tray / WorkerOnly。</summary>
        [JsonPropertyName("closeBehavior")]
        public string? CloseBehavior { get; set; }

        /// <summary>启动时自动应用调校（Worker 启动路径消费）。</summary>
        [JsonPropertyName("applyTuningOnStartup")]
        public bool? ApplyTuningOnStartup { get; set; }

        /// <summary>后台按规则自动调度新进程（ProcessWatcher 消费）。</summary>
        [JsonPropertyName("watchProcessBinding")]
        public bool? WatchProcessBinding { get; set; }

        /// <summary>出错时给提示（界面侧气泡消费）。</summary>
        [JsonPropertyName("notifyOnError")]
        public bool? NotifyOnError { get; set; }

        /// <summary>提示停留秒数（1–30）。</summary>
        [JsonPropertyName("toastSeconds")]
        public int? ToastSeconds { get; set; }

        /// <summary>启动异常自动拦截（Worker 启动路径与 TuningApplyService 消费）。</summary>
        [JsonPropertyName("smartAntiBrick")]
        public bool? SmartAntiBrick { get; set; }

        /// <summary>智能休眠感知：恢复后重下发调校（Worker 的休眠监听消费）。</summary>
        [JsonPropertyName("smartHibernateAware")]
        public bool? SmartHibernateAware { get; set; }
    }
}
