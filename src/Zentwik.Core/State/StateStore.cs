using System.Text.Json;
using Zentwik.Contracts.Json;
using Zentwik.Core.Diagnostics;
using Zentwik.Domain.Persistence;
using Zentwik.Domain.Settings;

namespace Zentwik.Core.State;

/// <summary>临时文件 + 原子替换；读取失败时保留原文件。</summary>
public sealed class StateStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly DiagnosticLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StateStore(string dataDirectory, DiagnosticLog log)
    {
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        _path = Path.Combine(dataDirectory, "state.json");
        _tempPath = _path + ".tmp";
        _log = log;
    }

    public StateDocument Current { get; private set; } = new();

    public string DataDirectory { get; }

    public string FilePath => _path;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                _log.Info($"状态文件不存在，使用空状态：{_path}");
                Current = new StateDocument();
                return;
            }

            var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            if (json.Contains("\"voltageOffsetMillivolts\"", StringComparison.Ordinal))
            {
                json = json.Replace("\"voltageOffsetMillivolts\"", "\"offset\"", StringComparison.Ordinal);
                _log.Info("状态文件含旧字段 voltageOffsetMillivolts，按 offset 读入。");
            }

            var document = JsonSerializer.Deserialize(json, ZentwikJson.Default.StateDocument);

            if (document is null)
            {
                throw new InvalidDataException("状态文件为空。");
            }

            if (document.SchemaVersion > StateDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"状态文件版本 {document.SchemaVersion} 高于当前支持 {StateDocument.CurrentSchemaVersion}，拒绝加载。");
            }

            // 旧文件可能缺少后加的集合字段，反序列化会得到 null；缺失按空集合处理，不当作损坏。
            Current = document with
            {
                Settings = document.Settings ?? new UserSettings(),
                Plans = document.Plans ?? [],
                Scenes = document.Scenes ?? [],
                Ledger = document.Ledger ?? [],
            };
            _log.Info($"已加载状态：方案 {Current.Plans.Count}，场景 {Current.Scenes.Count}，账本 {Current.Ledger.Count}。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            _log.Error($"读取状态失败，保留原文件：{ex.Message}");
            BackupCorruptFile();
            Current = new StateDocument();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>在锁内修改并落盘。修改函数返回 null 表示放弃写入。</summary>
    public async Task<StateDocument?> UpdateAsync(
        Func<StateDocument, StateDocument?> mutate,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = mutate(Current);
            if (next is null)
            {
                return null;
            }

            await WriteAtomicAsync(next, ct).ConfigureAwait(false);
            Current = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<StateDocument?> UpdateSettingsAsync(UserSettings settings, CancellationToken ct = default) =>
        UpdateAsync(current => current with { Settings = settings }, ct);

    private async Task WriteAtomicAsync(StateDocument document, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ZentwikJson.Default.StateDocument);

        // 落盘后再替换：断电时要么是旧文件，要么是新文件，不能留下半截内容。
        await using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(_tempPath, _path, overwrite: true);
    }

    /// <summary>状态文件损坏时留一份副本：后续保存会覆盖原文件，没有副本就无法人工恢复。</summary>
    private void BackupCorruptFile()
    {
        var backup = $"{_path}.corrupt";
        try
        {
            File.Copy(_path, backup, overwrite: true);
            _log.Warn($"原状态文件已备份为 {backup}。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"备份损坏的状态文件失败：{ex.Message}");
        }
    }
}
