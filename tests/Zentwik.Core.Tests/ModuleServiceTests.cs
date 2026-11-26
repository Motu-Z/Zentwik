using Xunit;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Modules;
using Zentwik.Core.Plans;
using Zentwik.Core.State;

namespace Zentwik.Core.Tests;

public sealed class ModuleServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "zentwik-module-tests", Guid.NewGuid().ToString("N"));

    private readonly DiagnosticLog _log;
    private readonly StateStore _store;

    public ModuleServiceTests()
    {
        _log = new DiagnosticLog(_directory, echoToConsole: false);
        _store = new StateStore(_directory, _log);
        _store.LoadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    [Fact]
    public void Optional_Module_Is_Disabled_By_Default()
    {
        var service = new ModuleService(_store, _log);

        Assert.False(service.IsEnabled(ModuleIds.PowerLimits));
        Assert.True(service.IsEnabled("monitoring"));
    }

    [Fact]
    public async Task Enable_Then_Disable_Is_Persisted()
    {
        var service = new ModuleService(_store, _log);

        await service.EnableAsync(ModuleIds.PowerLimits);
        Assert.True(service.IsEnabled(ModuleIds.PowerLimits));

        var reloaded = new StateStore(_directory, _log);
        await reloaded.LoadAsync();
        Assert.True(new ModuleService(reloaded, _log).IsEnabled(ModuleIds.PowerLimits));

        await service.DisableAsync(ModuleIds.PowerLimits);
        Assert.False(service.IsEnabled(ModuleIds.PowerLimits));
    }

    [Fact]
    public async Task BuiltIn_Module_Cannot_Be_Disabled()
    {
        var service = new ModuleService(_store, _log);

        var error = await Assert.ThrowsAsync<CoreException>(() => service.DisableAsync("monitoring"));

        Assert.Equal(ErrorCodes.ModuleNotOptional, error.Code);
    }

    [Fact]
    public async Task Unknown_Module_Is_Rejected()
    {
        var service = new ModuleService(_store, _log);

        var error = await Assert.ThrowsAsync<CoreException>(() => service.EnableAsync("not-a-module"));

        Assert.Equal(ErrorCodes.ModuleNotFound, error.Code);
    }
}
