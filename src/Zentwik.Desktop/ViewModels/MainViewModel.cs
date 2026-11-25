using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Desktop.Services;
using Zentwik.Localization;

namespace Zentwik.Desktop.ViewModels;

/// <summary>不引用 WPF 控件类型，便于将来替换 UI 框架。</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int HistoryLength = 120;
    private const int SampleIntervalMs = 500;

    private readonly LocalizationCatalog _catalog;
    private readonly ThemeService _themes;
    private readonly SynchronizationContext _ui;
    private readonly Queue<double> _history = new();
    private readonly CancellationTokenSource _cts = new();

    private AgentConnection? _connection;
    private bool _suppressPersist = true;
    private bool _isConnected;
    private string _statusText;
    private double _cpuPercent;
    private double _memoryPercent;
    private int _processCount;
    private int _coreCount;
    private string _selectedLanguage;
    private string _selectedTheme;
    private string _selectedAccent;

    public MainViewModel(LocalizationCatalog catalog, ThemeService themes)
    {
        _catalog = catalog;
        _themes = themes;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _selectedLanguage = catalog.CurrentCulture;
        _selectedTheme = themes.Theme;
        _selectedAccent = themes.Accent;
        _statusText = catalog["status.connecting"];

        catalog.CultureChanged += OnCultureChanged;
    }

    /// <summary>采样历史更新。视图层据此重绘折线，视图模型不持有绘图对象。</summary>
    public event Action<double[]>? HistoryChanged;

    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    public ObservableCollection<PlanRow> Plans { get; } = [];

    public ObservableCollection<SceneRow> Scenes { get; } = [];

    public IReadOnlyList<string> Languages => LocalizationCatalog.SupportedCultures;

    public IReadOnlyList<string> Themes { get; } = ["dark", "light"];

    public IReadOnlyList<string> Accents { get; } = ["blue", "violet", "teal", "amber"];

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
            {
                UpdateStatusText();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string CpuText => $"{_cpuPercent:0}%";

    public string MemoryText => $"{_memoryPercent:0}%";

    public string ProcessText => _processCount.ToString();

    public string CoreText => _coreCount == 0 ? "—" : _coreCount.ToString();

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!Set(ref _selectedLanguage, value))
            {
                return;
            }

            _catalog.CurrentCulture = value;
            PersistSettingsAsync();
        }
    }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value))
            {
                return;
            }

            _themes.Apply(value, _selectedAccent);
            PersistSettingsAsync();
        }
    }

    public string SelectedAccent
    {
        get => _selectedAccent;
        set
        {
            if (!Set(ref _selectedAccent, value))
            {
                return;
            }

            _themes.Apply(_selectedTheme, value);
            PersistSettingsAsync();
        }
    }

    public async Task InitializeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var connection = await AgentConnection
                    .ConnectAsync(ProtocolInfo.PipeName, TimeSpan.FromSeconds(2), _cts.Token)
                    .ConfigureAwait(false);

                connection.EventReceived += OnEventReceived;
                connection.Closed += OnConnectionClosed;
                _connection = connection;

                var status = await connection.GetStatusAsync(_cts.Token).ConfigureAwait(false);
                var capabilities = await connection.ListCapabilitiesAsync(_cts.Token).ConfigureAwait(false);
                var plans = await connection.ListPlansAsync(_cts.Token).ConfigureAwait(false);
                var scenes = await connection.ListScenesAsync(_cts.Token).ConfigureAwait(false);
                var settings = await connection.GetSettingsAsync(_cts.Token).ConfigureAwait(false);

                await connection.SubscribeTelemetryAsync(SampleIntervalMs, _cts.Token).ConfigureAwait(false);

                _ui.Post(_ =>
                {
                    Replace(Capabilities, capabilities.Select(static c => new CapabilityRow(c.Id, c.State, c.Detail ?? string.Empty)));
                    Replace(Plans, plans.Select(static p => new PlanRow(p.Kind.ToString(), p.Name, $"rev {p.Revision}")));
                    Replace(Scenes, scenes.Select(static s => new SceneRow(s.Name, $"P{s.Priority}", string.Join(" · ", s.TargetPlanIds))));
                    _coreCount = status.ProcessorCount;
                    Raise(nameof(CoreText));
                    ApplySettings(settings);
                    IsConnected = true;
                }, null);

                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IpcTransportException or IOException)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void OnEventReceived(ResponseEnvelope envelope)
    {
        if (envelope.Op != Events.TelemetrySample || envelope.Data is not { } data)
        {
            return;
        }

        var sample = data.Deserialize(ZentwikJson.Default.TelemetryDto);
        if (sample is null)
        {
            return;
        }

        _ui.Post(_ => ApplySample(sample), null);
    }

    private void ApplySample(TelemetryDto sample)
    {
        _cpuPercent = sample.CpuTotalPercent;
        _memoryPercent = sample.MemoryTotalBytes == 0
            ? 0d
            : (1d - (double)sample.MemoryAvailableBytes / sample.MemoryTotalBytes) * 100d;
        _processCount = sample.ProcessCount;
        _coreCount = sample.ProcessorCount;

        Raise(nameof(CpuText));
        Raise(nameof(MemoryText));
        Raise(nameof(ProcessText));
        Raise(nameof(CoreText));

        _history.Enqueue(_cpuPercent);
        while (_history.Count > HistoryLength)
        {
            _history.Dequeue();
        }

        HistoryChanged?.Invoke([.. _history]);
    }

    private void ApplySettings(SettingsDto settings)
    {
        _suppressPersist = true;
        try
        {
            _catalog.CurrentCulture = LocalizationCatalog.Normalize(settings.Language);
            _selectedLanguage = _catalog.CurrentCulture;
            _selectedTheme = settings.Theme;
            _selectedAccent = settings.Accent;
            _themes.Apply(settings.Theme, settings.Accent);

            Raise(nameof(SelectedLanguage));
            Raise(nameof(SelectedTheme));
            Raise(nameof(SelectedAccent));
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    private void PersistSettingsAsync()
    {
        if (_suppressPersist)
        {
            return;
        }

        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        var patch = new SettingsPatch(_selectedLanguage, _selectedTheme, _selectedAccent, null);
        _ = Task.Run(async () =>
        {
            try
            {
                await connection.SetSettingsAsync(patch, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
            {
                // 设置写入失败不回滚界面：用户看到的值就是他刚选的，
                // 未持久化的事实记在代理日志里，下次启动会以磁盘值为准。
            }
        });
    }

    private void OnConnectionClosed(Exception? error)
    {
        _ = error;
        _ui.Post(_ =>
        {
            IsConnected = false;
            _connection = null;
        }, null);

        if (!_cts.IsCancellationRequested)
        {
            _ = Task.Run(InitializeAsync);
        }
    }

    private void OnCultureChanged()
    {
        _ui.Post(_ => UpdateStatusText(), null);
    }

    private void UpdateStatusText()
    {
        StatusText = IsConnected ? _catalog["status.connected"] : _catalog["status.disconnected"];
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}

public sealed record CapabilityRow(string Id, string State, string Detail);

public sealed record PlanRow(string Kind, string Name, string Caption);

public sealed record SceneRow(string Name, string Priority, string Targets);
