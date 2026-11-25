using Zentwik.Contracts.Protocol;

namespace Zentwik.Agent;

public enum AgentMode
{
    Run,
    InstallService,
    UninstallService,
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

    public static AgentOptions Parse(string[] args)
    {
        var mode = AgentMode.Run;
        var serviceMode = false;
        var pipe = ProtocolInfo.PipeName;
        var serviceName = "ZentwikAgent";
        string? explicitDataDirectory = null;
        var fake = false;
        var echo = true;

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
        };
    }

    private static string DefaultDataDirectory(bool service) => Path.Combine(
        Environment.GetFolderPath(service ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.ApplicationData),
        "ZentwikNext");
}
