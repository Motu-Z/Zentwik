using System.Text.Json;
using Zentwik.Agent.Diagnostics;
using Zentwik.Domain.Persistence;
using Zentwik.Domain.Settings;

namespace Zentwik.Agent.State;

/// <summary>临时文件 + 原子替换；读取失败时保留原文件。</summary>
public sealed class StateStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly AgentLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StateStore(string dataDirectory, AgentLog log)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "state.json");
        _tempPath = _path + ".tmp";
        _log = log;
    }

    public StateDocument Current { get; private set; } = new();

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

            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer
                .DeserializeAsync(stream, DomainJson.Default.StateDocument, ct)
                .ConfigureAwait(false);

            if (document is null)
            {
                throw new InvalidDataException("状态文件为空。");
            }

            if (document.SchemaVersion > StateDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"状态文件版本 {document.SchemaVersion} 高于当前支持 {StateDocument.CurrentSchemaVersion}，拒绝加载。");
            }

            Current = document;
            _log.Info($"已加载状态：方案 {Current.Plans.Count}，场景 {Current.Scenes.Count}，账本 {Current.Ledger.Count}。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            _log.Error($"读取状态失败，保留原文件：{ex.Message}");
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, DomainJson.Default.StateDocument);
        await File.WriteAllBytesAsync(_tempPath, bytes, ct).ConfigureAwait(false);
        File.Move(_tempPath, _path, overwrite: true);
    }
}
