namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 产品能力 ID。点分小写，前缀表示能力所属领域，不含厂商名与模块名。
    ///
    /// ID 是界面与自动化的稳定判据：一旦发布不得改名，新增能力只能追加。
    /// 界面不得据此推断「装了什么模块」，只能用来判断「这个动作能不能做」。
    /// </summary>
    public static class CapabilityIds
    {
        /// <summary>读取当前调校值（CO / FMax 或电压 / 倍频）。</summary>
        public const string TuningRead = "tuning.read";

        /// <summary>写入调校值。</summary>
        public const string TuningApply = "tuning.apply";

        /// <summary>调校值可作为方案保存并整体应用。</summary>
        public const string TuningPlan = "tuning.plan";

        /// <summary>恢复默认调校（基线回写）。</summary>
        public const string TuningBaselineRestore = "tuning.baseline-restore";

        /// <summary>提供核心组宽度提示（每 CCD 核数）。</summary>
        public const string TopologyGroupHint = "topology.group-hint";

        /// <summary>进程绑定与优先级调度。</summary>
        public const string SchedulingProcess = "scheduling.process";

        /// <summary>系统电源策略读写。</summary>
        public const string PowerPolicy = "power.policy";

        /// <summary>场景规则与自动切换。</summary>
        public const string ScenarioRules = "scenario.rules";

        /// <summary>日志与诊断导出。</summary>
        public const string DiagnosticsLogs = "diagnostics.logs";

        /// <summary>开机自启。</summary>
        public const string ShellAutoStart = "shell.autostart";
    }
}
