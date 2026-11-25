using System.Text;
using Zentwik.Contracts.Dto;

namespace Zentwik.Core.Diagnostics;

/// <summary>按天分文件；只记操作事实，不记完整配置内容。</summary>
public sealed class DiagnosticLog : IDisposable
{
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private const int RecentCapacity = 200;

    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly bool _echo;
    private readonly Queue<LogEntryDto> _recent = new(RecentCapacity);
    private StreamWriter? _writer;
    private DateOnly _currentDay;
    private int _disposed;

    public DiagnosticLog(string dataDirectory, bool echoToConsole = true)
    {
        _directory = Path.Combine(dataDirectory, "logs");
        _echo = echoToConsole;
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public string? CurrentFilePath => _writer?.BaseStream is { } stream
        ? (stream as FileStream)?.Name
        : null;

    public void Info(string message) => Write("info", message);

    public void Warn(string message) => Write("warn", message);

    public void Error(string message) => Write("error", message);

    public IReadOnlyList<LogEntryDto> Recent(int limit) => Recent(limit, null);

    public IReadOnlyList<LogEntryDto> RecentErrors(int limit) => Recent(limit, "error");

    private IReadOnlyList<LogEntryDto> Recent(int limit, string? level)
    {
        lock (_gate)
        {
            return _recent
                .Where(entry => level is null || entry.Level == level)
                .TakeLast(Math.Clamp(limit, 1, RecentCapacity))
                .ToArray();
        }
    }

    private void Write(string level, string message)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var timestamp = DateTimeOffset.Now;
        var line = $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} {level.ToUpperInvariant(),-5} {message}";

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _recent.Enqueue(new LogEntryDto(timestamp, level, message));
            while (_recent.Count > RecentCapacity)
            {
                _recent.Dequeue();
            }

            try
            {
                EnsureWriter();
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (_echo)
        {
            Console.WriteLine(line);
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _currentDay == today)
        {
            return;
        }

        _writer?.Dispose();
        _currentDay = today;
        var path = Path.Combine(_directory, $"agent-{today:yyyyMMdd}.log");
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxFileBytes)
        {
            File.Move(path, Path.Combine(_directory, $"agent-{today:yyyyMMdd}-{DateTime.Now:HHmmss}.log"), overwrite: true);
        }

        _writer = new StreamWriter(path, append: true, Encoding.UTF8);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
