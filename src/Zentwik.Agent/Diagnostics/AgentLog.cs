using System.Text;

namespace Zentwik.Agent.Diagnostics;

/// <summary>按天分文件；只记操作事实，不记完整配置内容。</summary>
public sealed class AgentLog : IDisposable
{
    private const long MaxFileBytes = 8 * 1024 * 1024;

    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly bool _echo;
    private StreamWriter? _writer;
    private DateOnly _currentDay;
    private int _disposed;

    public AgentLog(string dataDirectory, bool echoToConsole = true)
    {
        _directory = Path.Combine(dataDirectory, "logs");
        _echo = echoToConsole;
        Directory.CreateDirectory(_directory);
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";

        lock (_gate)
        {
            try
            {
                EnsureWriter();
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            catch (IOException)
            {
                // 日志失败不得影响主流程。
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
