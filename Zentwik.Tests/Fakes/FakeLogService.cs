using Zentwik.Core.Services;

namespace Zentwik.Tests.Fakes
{
    /// <summary>只满足构造函数注入：日志不是被测对象，写进来的一律丢弃。</summary>
    public sealed class FakeLogService : ILogService
    {
        public event Action<LogEntry>? EntryAdded { add { } remove { } }
        public bool Verbose { get; set; }
        public bool Paused { get; set; }
        public int Count => 0;
        public long CountInfo => 0;
        public long CountWarn => 0;
        public long CountError => 0;
        public long CountCritical => 0;
        public long TotalCount => 0;
        public long TotalInfo => 0;
        public long TotalWarn => 0;
        public long TotalError => 0;
        public long TotalCritical => 0;
        public LogEntry? LastIssue => null;
        public string LogDirectory => string.Empty;
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Critical(string message) { }
        public void Clear() { }
        public void Flush() { }
        public IReadOnlyList<LogEntry> Snapshot() => Array.Empty<LogEntry>();
    }
}
