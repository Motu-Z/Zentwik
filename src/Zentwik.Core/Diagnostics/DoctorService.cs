using Zentwik.Contracts.Dto;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Modules;
using Zentwik.Core.State;
using Zentwik.Platform;

namespace Zentwik.Core.Diagnostics;

public sealed record AgentRuntime(
    string Version,
    bool RunningAsService,
    bool Simulated,
    DateTimeOffset StartedAt);

public sealed class DoctorService(
    StateStore store,
    CapabilityService capabilities,
    DiagnosticLog log,
    AgentRuntime runtime,
    ISystemInfo system,
    Func<string?> activeScene)
{
    public DoctorDto Build()
    {
        var capabilityList = capabilities.Get();
        var capabilityDtos = capabilityList.Select(Zentwik.Contracts.Mapping.ToDto).ToArray();

        return new DoctorDto(
            Version: runtime.Version,
            ProtocolVersion: Zentwik.Contracts.Protocol.ProtocolInfo.Version,
            MachineName: system.MachineName,
            Elevated: system.IsElevated,
            RunningAsService: runtime.RunningAsService,
            Simulated: runtime.Simulated,
            StartedAt: runtime.StartedAt,
            UptimeSeconds: (long)(DateTimeOffset.UtcNow - runtime.StartedAt.ToUniversalTime()).TotalSeconds,
            DataDirectory: store.DataDirectory,
            StatePath: store.FilePath,
            LogDirectory: log.DirectoryPath,
            PlanCount: store.Current.Plans.Count,
            SceneCount: store.Current.Scenes.Count,
            LedgerEntryCount: store.Current.Ledger.Count,
            ActiveSceneId: activeScene(),
            Capabilities: capabilityDtos,
            Modules: ModuleCatalog.Describe(capabilityList, runtime.Simulated),
            RecentErrors: log.RecentErrors(20));
    }
}
