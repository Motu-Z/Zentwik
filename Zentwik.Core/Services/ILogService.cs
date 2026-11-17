using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 日志服务：内存环形缓冲（供只读查询）+ 按天滚动的日志文件。
    ///
    /// 调用方可以在任意线程直接调用写方法，不需要任何 UI 线程调度器：
    /// 内存缓冲由内部锁保护，<see cref="EntryAdded"/> 在写入线程上同步触发，
    /// 订阅方自行负责切回自己的线程。
    /// </summary>
    public interface ILogService
    {
        /// <summary>
        /// 新增一条日志后触发，在写入线程上同步调用。
        ///
        /// 不在这里做线程封送：Core 不持有任何 UI 调度器，
        /// 需要回到界面线程是订阅方（View 层）的责任。
        /// </summary>
        event Action<LogEntry>? EntryAdded;

        /// <summary>是否记录 <see cref="LogLevel.Debug"/> 级详细日志。</summary>
        bool Verbose { get; set; }

        /// <summary>
        /// 暂停记录。暂停期间日志真的丢失，不进内存也不落盘。
        /// <see cref="LogLevel.Critical"/> 不受影响。
        /// </summary>
        bool Paused { get; set; }

        /// <summary>内存缓冲当前条数。</summary>
        int Count { get; }

        long CountInfo { get; }

        long CountWarn { get; }

        long CountError { get; }

        long CountCritical { get; }

        /// <summary>
        /// 本次运行累计写入内存缓冲的条数，含已被缓冲挤出窗口的；<see cref="Clear"/> 后归零。
        /// 界面右侧栏靠它显示总量 —— 缓冲只有 200 条，拿缓冲条数当总数会永远停在窗口大小上。
        /// </summary>
        long TotalCount { get; }

        long TotalInfo { get; }

        long TotalWarn { get; }

        long TotalError { get; }

        long TotalCritical { get; }

        /// <summary>
        /// 最近一条异常（错误优先，没有就退到警告），不受缓冲裁剪影响；<see cref="Clear"/> 后为 null。
        /// 界面「最新异常」靠它 —— 只看缓冲会在早期异常被挤出后变成「计数有、卡片空」。
        /// </summary>
        LogEntry? LastIssue { get; }

        string LogDirectory { get; }

        /// <summary>详细日志，仅在 <see cref="Verbose"/> 为真时写入。只落盘，不进内存缓冲。</summary>
        void Debug(string message);

        void Info(string message);

        void Warn(string message);

        void Error(string message);

        /// <summary>致命错误。写入后立刻冲刷磁盘 —— 走到这里时进程可能下一行就没了。</summary>
        void Critical(string message);

        /// <summary>清空内存缓冲、待写队列与磁盘文件。</summary>
        void Clear();

        /// <summary>把待写队列立刻刷到磁盘。退出前调用，避免丢最后几条。</summary>
        void Flush();

        /// <summary>
        /// 取当前内存日志的只读副本。导出、自检、界面列表刷新都必须走这个方法：
        /// 它返回的是一份快照，遍历期间后台继续写不会抛「集合已被修改」。
        /// </summary>
        IReadOnlyList<LogEntry> Snapshot();
    }
}
