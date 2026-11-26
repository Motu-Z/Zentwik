using Zentwik.Contracts.Protocol;

namespace Zentwik.Agent;

public enum AgentMode
{
    Run,
    InstallService,
    UninstallService,
    StartService,
    StopService,
}

public sealed class AgentOptions
{
    public required string PipeName { get; init; }

    public required string DataDirectory { get; init; }

    public required string ServiceName { get; init; }

    public required AgentMode Mode { get; init; }

    public bool ServiceMode { get; init; }

    public bool UseFake { get; init; }

    public bool EchoLog { get; init; } = true;

    /// <summary>安装时一并启用的可选模块。服务启动前写入，避免与运行中的服务争状态文件。</summary>
    public IReadOnlyList<string> EnableModules { get; init; } = [];

    public static AgentOptions Parse(string[] args)
    {
        var mode = AgentMode.Run;
        var serviceMode = false;
        var pipe = ProtocolInfo.PipeName;
        var serviceName = "ZentwikAgent";
        string? explicitDataDirectory = null;
        var fake = false;
        var echo = true;
        var modules = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--service":
                    serviceMode = true;
                    break;
                case "--install-service":
                    mode = AgentMode.InstallService;
                    break;
                case "--uninstall-service":
                    mode = AgentMode.UninstallService;
                    break;
                case "--start-service":
                    mode = AgentMode.StartService;
                    break;
                case "--stop-service":
                    mode = AgentMode.StopService;
                    break;
                case "--pipe" when i + 1 < args.Length:
                    pipe = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    serviceName = args[++i];
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    explicitDataDirectory = args[++i];
                    break;
                case "--fake":
                    fake = true;
                    break;
                case "--enable-module" when i + 1 < args.Length:
                    modules.Add(args[++i]);
                    break;
                case "--quiet":
                    echo = false;
                    break;
            }
        }

        var privileged = serviceMode || mode != AgentMode.Run;

        return new AgentOptions
        {
            PipeName = pipe,
            DataDirectory = explicitDataDirectory ?? DefaultDataDirectory(privileged),
            ServiceName = serviceName,
            Mode = mode,
            ServiceMode = serviceMode,
            UseFake = fake,
            EchoLog = echo,
            EnableModules = modules,
        };
    }

    private static string DefaultDataDirectory(bool service) => Path.Combine(
        Environment.GetFolderPath(service ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.ApplicationData),
        "ZentwikNext");
}
