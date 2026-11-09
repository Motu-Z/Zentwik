using System.IO;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 日志服务的构造参数。
    ///
    /// 这份对象由宿主分两步填：先跑宿主的 configureLog 回调，回调里没给
    /// Directory 才回落到 <c>%AppData%\Zentwik\Logs</c>。init-only 会让第一步
    /// 无法改写目录 —— 回调能设上限、能开 Verbose，唯独改不了最该改的那一项。
    ///
    /// 上限值集中在这里，是为了让「内存天花板」可计算、可调整，而不是散落在写入路径上：
    /// 界面条数 × 单条字符上限（<see cref="MaxEntries"/> × <see cref="MaxMessageChars"/>）
    /// 才是真实的常驻内存上界。
    /// </summary>
    public sealed class LogOptions
    {
        /// <summary>日志目录。默认 <c>%AppData%\Zentwik\Logs</c>。</summary>
        public string Directory { get; set; } = string.Empty;

        /// <summary>
        /// 界面日志条数上限。超出后按 <see cref="TrimBatch"/> 成批丢弃头部条目。
        /// 程序常驻，集合只增不减会把内存吃光；翻历史是磁盘日志的职责。
        /// </summary>
        public int MaxEntries { get; set; } = 200;

        /// <summary>单次丢弃的批量。逐条删会让列表每条日志都整体重排一次。</summary>
        public int TrimBatch { get; set; } = 32;

        /// <summary>
        /// 单条消息进入内存集合时的长度上限。
        /// 条数上限管不了「一条多大」：一条未捕获异常堆栈就有几 KB。
        /// 只截内存不截落盘，磁盘是事后排查的唯一凭据。
        /// </summary>
        public int MaxMessageChars { get; set; } = 2000;

        /// <summary>
        /// 日志文件名前缀，实际文件名为 <c>{FilePrefix}{yyyyMMdd}.log</c>。
        ///
        /// 双进程架构下 UI 与 Worker 共用 <c>%AppData%\Zentwik\Logs</c> 一个目录。
        /// 若两边写同一个文件名，先打开的那个持有追加句柄，后者每次写入都要
        /// 撞 FileShare 竞争 —— 表现为随机丢日志或随机抛 IOException。
        /// 用前缀把两个进程的文件彻底分开：UI 用 <c>zentwik-</c>，Worker 用 <c>worker-</c>，
        /// 留存清理按前缀白名单同时收两条线。
        /// </summary>
        public string FilePrefix { get; set; } = "zentwik-";

        /// <summary>日志文件留存天数，超期的在启动时删除。</summary>
        public int RetentionDays { get; set; } = 14;

        /// <summary>待写队列容量上限，防止磁盘卡顿时积压成隐形内存泄漏。</summary>
        public int MaxPendingEntries { get; set; } = 800;

        /// <summary>初始是否记录 Debug 级。</summary>
        public bool Verbose { get; set; }

        internal LogOptions Normalize()
        {
            return new LogOptions
            {
                Directory = string.IsNullOrWhiteSpace(Directory) ? Path.GetTempPath() : Directory,
                FilePrefix = NormalizePrefix(FilePrefix),
                MaxEntries = MaxEntries > 0 ? MaxEntries : 200,
                TrimBatch = TrimBatch > 0 ? TrimBatch : 32,
                MaxMessageChars = MaxMessageChars > 0 ? MaxMessageChars : 2000,
                RetentionDays = RetentionDays > 0 ? RetentionDays : 14,
                MaxPendingEntries = MaxPendingEntries > 0 ? MaxPendingEntries : 800,
                Verbose = Verbose
            };
        }

        /// <summary>
        /// 前缀一律补上结尾的 <c>-</c>。文件名解析靠「最后一个连字符」切出日期段，
        /// 前缀不带连字符时 <c>worker20261101.log</c> 无从判断哪一段是日期，
        /// 留存清理会把这些文件当成永远不过期的垃圾留在目录里。
        /// </summary>
        private static string NormalizePrefix(string? prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return "zentwik-";

            string trimmed = prefix.Trim();
            return trimmed.EndsWith('-') ? trimmed : trimmed + "-";
        }
    }
}
