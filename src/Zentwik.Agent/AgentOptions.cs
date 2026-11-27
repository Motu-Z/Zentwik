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

    /// <summary>
    /// 旧数据目录（`ZentwikNext`）。只用于一次性搬迁：目标不存在时把旧目录内容搬过来。
    /// 显式指定 `--data-dir` 时为空，不做任何搬迁。
    /// </summary>
    public string? LegacyDataDirectory { get; init; }

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

        var dataDirectory = explicitDataDirectory ?? DefaultDataDirectory(privileged);

        return new AgentOptions
        {
            PipeName = pipe,
            DataDirectory = dataDirectory,
            LegacyDataDirectory = LegacyDataDirectoryOf(dataDirectory),
            ServiceName = serviceName,
            Mode = mode,
            ServiceMode = serviceMode,
            UseFake = fake,
            EchoLog = echo,
            EnableModules = modules,
        };
    }

    /// <summary>
    /// 服务/安装模式用 ProgramData，其余用 LocalAppData。
    /// 控制台模式不能落到 Roaming 的 Zentwik：那是旧版产品的配置目录
    /// （tuning.json、masks.json、schedules.json、ui.json、Logs 全在里面），同名会互相覆盖。
    /// </summary>
    private static string DefaultDataDirectory(bool service) => Path.Combine(
        Environment.GetFolderPath(service ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
        "Zentwik");

    /// <summary>
    /// 改名前的同级目录，仅作为搬迁来源；旧版产品从未用过这个名字，读它是安全的。
    /// 不能靠「是否显式传了 --data-dir」判断：服务注册时安装器总是显式传，那条路径会永远跳过搬迁。
    /// </summary>
    private static string? LegacyDataDirectoryOf(string dataDirectory)
    {
        var full = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(full), "Zentwik", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(full);
        return parent is null ? null : Path.Combine(parent, "ZentwikNext");
    }
}
