namespace Zentwik.Core.Services
{
    /// <summary>AMD 功耗限制读数。单位为毫瓦，null 表示读不到。</summary>
    public sealed class PowerLimitSnapshot
    {
        /// <summary>该平台是否有可用的功耗命令组。</summary>
        public bool Supported { get; set; }

        /// <summary>是否真的读到了值。</summary>
        public bool Available { get; set; }

        public string? Error { get; set; }

        public int? StapmMilliWatts { get; set; }

        public int? FastMilliWatts { get; set; }

        public int? SlowMilliWatts { get; set; }

        public bool HasAny => StapmMilliWatts.HasValue || FastMilliWatts.HasValue || SlowMilliWatts.HasValue;
    }

    /// <summary>功耗写入结果；失败原因逐项给出，便于日志与界面直接显示。</summary>
    public sealed class PowerLimitWriteResult
    {
        public bool AllSucceeded { get; set; }

        public List<string> Failures { get; } = new();

        public string? Fatal { get; set; }
    }
}
