using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// <see cref="ILogService"/> 的默认实现。
    ///
    /// 写入路径分两条：Debug 级只落盘（高频，进内存缓冲会挤掉真正要看的条目）；
    /// 其余级别先进内存环形缓冲，再由后台线程每 500ms 成批写文件 ——
    /// 每条日志都 CreateDirectory + AppendAllText 会让磁盘成为高频路径的瓶颈。
    ///
    /// 【本类不持有任何 UI 调度器】
    /// 内存缓冲由内部锁保护，<see cref="EntryAdded"/> 在写入线程上同步触发。
    /// 需要回到界面线程由订阅方自己负责 —— 后台进程里根本不存在那个线程。
    /// </summary>
    public sealed class LogService : ILogService, IDisposable
    {
        /// <summary>
        /// 历史日志文件名前缀。改名前的日志同样要参与留存清理，
        /// 否则它们会永远留在用户目录里，谁都不删。
        /// </summary>
        private static readonly string[] LegacyLogPrefixes = ["zenforge-"];

        private readonly LogOptions _options;

        /// <summary>本实例写入的文件前缀，已由 <see cref="LogOptions.Normalize"/> 补齐结尾连字符。</summary>
        private readonly string _filePrefix;

        private readonly TimeProvider _timeProvider;

        /// <summary>
        /// 内存日志环形缓冲。容量由 <see cref="LogOptions.MaxEntries"/> 决定，
        /// 超出后按 <see cref="LogOptions.TrimBatch"/> 成批丢弃头部。
        /// 用 Queue 而不是 List：丢掉的是最早的条目，只在一端进出，没有随机访问需求。
        /// </summary>
        private readonly Queue<LogEntry> _entries = new();

        /// <summary>
        /// 保护 <see cref="_entries"/> 与四个级别计数器。
        ///
        /// 采样线程、WMI 回调线程、管道服务线程都会并发写日志，
        /// 不加锁就是随机的「集合已被修改」异常。
        /// </summary>
        private readonly object _entriesGate = new();

        private readonly object _fileGate = new();

        private readonly Queue<LogEntry> _pendingFile = new();

        /// <summary>
        /// 落盘线程的唤醒信号。必须是 AutoResetEvent：ManualResetEventSlim 的 Set
        /// 若落在「Wait 已返回、Reset 未执行」的间隙里，信号会被吃掉，那批日志要多等一个周期。
        /// </summary>
        private readonly AutoResetEvent _fileSignal = new(false);

        /// <summary>
        /// 保护「把一批条目真正写进文件」这一段。
        ///
        /// 没有它，<see cref="Flush"/> 的语义是假的：待写队列被写盘线程抢先取走时，
        /// Flush 看到的队列是空的，立刻返回，而此时那一批还在另一个线程上没落盘。
        /// 调用方（退出前、崩溃兜底）拿到的「已刷完」就不再成立。
        /// </summary>
        private readonly object _writeGate = new();

        private readonly ManualResetEvent _stopSignal = new(false);

        private readonly Thread _writer;

        private bool _directoryReady;

        /// <summary>
        /// 日志「代号」。每次清空 +1，写盘线程落盘前核对：
        /// 对不上说明这批已被清掉，直接丢弃，别把旧日志写回刚删掉的文件。
        /// </summary>
        private int _generation;

        private long _countInfo;
        private long _countWarn;
        private long _countError;
        private long _countCritical;

        private int _paused;
        private int _verbose;
        private int _disposed;

        public LogService(LogOptions options, TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            _options = options.Normalize();
            _filePrefix = _options.FilePrefix;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _verbose = _options.Verbose ? 1 : 0;

            LogDirectory = _options.Directory;

            EnforceRetention(_options.RetentionDays);

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "Zentwik.LogFile"
            };

            _writer.Start();
        }

        public event Action<LogEntry>? EntryAdded;

        public bool Verbose
        {
            get => Volatile.Read(ref _verbose) == 1;
            set => Volatile.Write(ref _verbose, value ? 1 : 0);
        }

        public bool Paused
        {
            get => Volatile.Read(ref _paused) == 1;
            set => Volatile.Write(ref _paused, value ? 1 : 0);
        }

        public int Count
        {
            get
            {
                lock (_entriesGate)
                    return _entries.Count;
            }
        }

        public long CountInfo => Interlocked.Read(ref _countInfo);

        public long CountWarn => Interlocked.Read(ref _countWarn);

        public long CountError => Interlocked.Read(ref _countError);

        public long CountCritical => Interlocked.Read(ref _countCritical);

        public string LogDirectory { get; }

        public void Debug(string message)
        {
            if (Verbose)
                Write(LogLevel.Debug, message);
        }

        public void Info(string message) => Write(LogLevel.Info, message);

        public void Warn(string message) => Write(LogLevel.Warn, message);

        public void Error(string message) => Write(LogLevel.Error, message);

        public void Critical(string message)
        {
            Write(LogLevel.Critical, message);
            Flush();
        }

        public IReadOnlyList<LogEntry> Snapshot()
        {
            lock (_entriesGate)
                return [.. _entries];
        }

        public void Clear()
        {
            // 顺序不能反：先同步清队列并推进代号，再做内存清空与删文件。
            // 若把代号推进放到后台异步执行，写盘线程会趁这个窗口把队列里的
            // 旧日志写进刚被删掉的文件 —— 用户点的是「清空」，看到的是旧日志原封不动回来了。
            lock (_fileGate)
            {
                _pendingFile.Clear();
                _generation++;
            }

            ClearMemory();

            PurgeLogFilesAsync();
        }

        public void Flush()
        {
            try
            {
                FlushPendingFile();

                // 队列可能刚被写盘线程取走（那一批已经不在队列里），
                // 这时只刷队列等于什么都没做。等它把手上那批写完。
                lock (_writeGate)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 退出阶段不能因为日志抛异常
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _stopSignal.Set();

            try
            {
                _writer.Join(TimeSpan.FromSeconds(2));
            }
            catch (ThreadStateException)
            {
                // 线程尚未启动就退出，无需等待
            }

            Flush();

            _fileSignal.Dispose();
            _stopSignal.Dispose();
        }

        private void ClearMemory()
        {
            bool locked = false;

            try
            {
                Monitor.TryEnter(_entriesGate, 100, ref locked);

                if (!locked)
                {
                    // 拿不到锁就放弃清缓冲，队列与文件照清 —— 至少下次不会被旧日志淹掉。
                    // 这里刻意不写日志：写日志本身要拿同一把锁，在一个「已经等不到锁」的
                    // 分支里再去排队，等于把 100ms 变成无限。
                    System.Diagnostics.Debug.WriteLine("清空日志：等待内存缓冲超时，已跳过内存清空");
                    return;
                }

                _entries.Clear();
                Interlocked.Exchange(ref _countInfo, 0);
                Interlocked.Exchange(ref _countWarn, 0);
                Interlocked.Exchange(ref _countError, 0);
                Interlocked.Exchange(ref _countCritical, 0);
            }
            finally
            {
                if (locked)
                    Monitor.Exit(_entriesGate);
            }
        }

        private void PurgeLogFilesAsync()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(LogDirectory))
                        return;

                    // 按前缀白名单删：只删本实例前缀会漏掉 Worker / 旧版本写的文件，
                    // 那些文件的句柄不在本进程手里，删除不会失败。
                    foreach (string file in EnumerateLogFiles())
                        DeleteWithRetry(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 清盘失败不影响清缓冲
                }
            });
        }

        private static void DeleteWithRetry(string file)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.IO.File.Delete(file);
                    return;
                }
                catch (IOException)
                {
                    // 句柄还开着，给写盘线程一点时间收尾
                    Thread.Sleep(30);
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        private void EnforceRetention(int maxDays)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    return;

                DateTime cutoff = DateTime.Today.AddDays(-maxDays);

                foreach (string file in EnumerateLogFiles())
                {
                    if (TryParseLogDate(file, out DateTime date) && date < cutoff)
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 单个文件删不掉不影响其余留存清理
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 留存清理失败不影响启动
            }
        }

        /// <summary>
        /// 枚举本目录下所有属于本产品的日志文件：本实例前缀 + 历史前缀。
        ///
        /// 白名单而不是通配 <c>*.log</c>：日志目录是用户可见的普通目录，
        /// 别人放在里面的 .log 与清理无关，不能被留存策略连带删掉。
        /// </summary>
        private IEnumerable<string> EnumerateLogFiles()
        {
            foreach (string file in Directory.EnumerateFiles(LogDirectory, _filePrefix + "*.log"))
                yield return file;

            foreach (string prefix in LegacyLogPrefixes)
            {
                foreach (string file in Directory.EnumerateFiles(LogDirectory, prefix + "*.log"))
                    yield return file;
            }
        }

        /// <summary>
        /// 从 <c>{prefix}{yyyyMMdd}.log</c> 里切出日期段。
        ///
        /// 用最后一个连字符切分，而不是第一个：前缀本身可能不带连字符
        /// （<c>worker-</c> 与 <c>zentwik-</c> 都带，但不保证所有调用方都带），
        /// 日期段里又不可能出现连字符，从右往左找是唯一稳的切法。
        /// </summary>
        internal static bool TryParseLogDate(string file, out DateTime date)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int separator = name.LastIndexOf('-');

            if (separator < 0 || name.Length <= separator + 1)
            {
                date = default;
                return false;
            }

            return DateTime.TryParseExact(
                name[(separator + 1)..],
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private void Write(LogLevel level, string message)
        {
            // 暂停期间一条都不记。Critical 例外 —— 程序要坏了，
            // 这时静默丢日志等于让用户永远查不出原因。
            if (Paused && level != LogLevel.Critical)
                return;

            LogEntry entry = new()
            {
                Time = _timeProvider.GetLocalNow().DateTime,
                Level = level,
                // Debug 不截断：它只落盘，磁盘是事后排查的唯一凭据，保留原文。
                Message = level == LogLevel.Debug ? message : Clip(message)
            };

            // Debug 只落盘：不进内存缓冲，高频条目会把 200 条的窗口瞬间挤空。
            if (level == LogLevel.Debug)
            {
                EnqueueFile(entry);
                return;
            }

            AddEntry(entry);
        }

        private string Clip(string message)
        {
            if (message.Length <= _options.MaxMessageChars)
                return message;

            return string.Concat(
                message.AsSpan(0, _options.MaxMessageChars),
                "…(截断 ",
                (message.Length - _options.MaxMessageChars).ToString(CultureInfo.InvariantCulture),
                " 字符)");
        }

        private void AddEntry(LogEntry entry)
        {
            lock (_entriesGate)
            {
                _entries.Enqueue(entry);
                BumpCount(entry.Level, +1);

                // 攒够一批再丢头部，别每条日志都整体搬一次
                if (_entries.Count > _options.MaxEntries + _options.TrimBatch)
                {
                    for (int i = 0; i < _options.TrimBatch && _entries.Count > 0; i++)
                    {
                        // 丢弃时必须同步减计数，否则查询到的数字会和实际条数对不上
                        BumpCount(_entries.Peek().Level, -1);
                        _entries.Dequeue();
                    }
                }
            }

            // 订阅者回调放到锁外：处理器可能做重操作，没必要占着缓冲锁。
            // 订阅者异常不能冒泡回调用点 —— 日志遍布 WMI 回调与管道线程。
            EnqueueFile(entry);
            try
            {
                EntryAdded?.Invoke(entry);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] 日志订阅者处理失败：{ex.Message}");
            }
        }

        private void BumpCount(LogLevel level, int delta)
        {
            switch (level)
            {
                case LogLevel.Info: Interlocked.Add(ref _countInfo, delta); break;
                case LogLevel.Warn: Interlocked.Add(ref _countWarn, delta); break;
                case LogLevel.Error: Interlocked.Add(ref _countError, delta); break;
                case LogLevel.Critical: Interlocked.Add(ref _countCritical, delta); break;
            }
        }

        private void EnqueueFile(LogEntry entry)
        {
            lock (_fileGate)
            {
                _pendingFile.Enqueue(entry);

                // 只丢最旧的一条、不整队列清空：清空会让「刚发生了什么」
                // 这段最关键的上下文一起消失。
                while (_pendingFile.Count > _options.MaxPendingEntries)
                    _pendingFile.Dequeue();
            }

            try
            {
                _fileSignal.Set();
            }
            catch (ObjectDisposedException)
            {
                // 退出过程中队列已释放，最后几条日志丢弃即可
            }
        }

        private void WriterLoop()
        {
            WaitHandle[] handles = [_fileSignal, _stopSignal];

            while (true)
            {
                try
                {
                    // 最多等 500ms：即使没有任何 Set，也要把这段时间攒下的条目写掉
                    int index = WaitHandle.WaitAny(handles, 500);

                    if (index == 1 || Volatile.Read(ref _disposed) == 1)
                    {
                        FlushPendingFile();
                        return;
                    }

                    FlushPendingFile();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // 写盘线程不能因为异常死掉
                }
            }
        }

        private void FlushPendingFile()
        {
            LogEntry[] batch;
            int generation;

            lock (_fileGate)
            {
                if (_pendingFile.Count == 0)
                    return;

                batch = [.. _pendingFile];
                _pendingFile.Clear();
                generation = _generation;
            }

            // 取队列时还没清空、写的时候已被清空：这批属于上一代，
            // 写出去就是把刚删掉的日志又搬回磁盘。
            if (Volatile.Read(ref _generation) != generation)
                return;

            try
            {
                lock (_writeGate)
                {
                    // 目录只建一次：每条日志 CreateDirectory 是纯浪费
                    if (!_directoryReady)
                    {
                        Directory.CreateDirectory(LogDirectory);
                        _directoryReady = true;
                    }

                    // 按天分组：一次遍历算好日期键，避免 GroupBy 为每条日志
                    // 都分配分组对象、再把整批 StringBuilder 拷成一个大字符串。
                    Dictionary<string, List<LogEntry>> byDay = new(StringComparer.Ordinal);

                    foreach (LogEntry entry in batch)
                    {
                        string day = entry.Time.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        if (!byDay.TryGetValue(day, out List<LogEntry>? group))
                        {
                            group = new List<LogEntry>();
                            byDay.Add(day, group);
                        }
                        group.Add(entry);
                    }

                    foreach (KeyValuePair<string, List<LogEntry>> group in byDay)
                    {
                        StringBuilder builder = new();

                        foreach (LogEntry entry in group.Value)
                        {
                            builder.Append('[').Append(entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] [")
                                   .Append(entry.LevelText).Append("] ")
                                   .Append(entry.Message).Append(Environment.NewLine);
                        }

                        // FileShare.ReadWrite | FileShare.Delete：允许清空日志的后台任务与
                        // 资源管理器在我们写着的时候读、删。AppendAllText 默认只给 Read，
                        // 一旦用户点「清空日志」，那个文件就再也删不掉了。
                        using FileStream stream = new(
                            Path.Combine(LogDirectory, $"{_filePrefix}{group.Key}.log"),
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);

                        using StreamWriter writer = new(stream, new UTF8Encoding(false));
                        writer.Write(builder);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写盘失败绝不能影响主程序
            }
        }
    }
}
