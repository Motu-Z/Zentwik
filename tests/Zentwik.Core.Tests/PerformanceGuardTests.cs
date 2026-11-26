using Xunit;

namespace Zentwik.Core.Tests;

/// <summary>
/// 生产代码的性能禁则。这些 API 单看无害，实际代价与调用次数无关而在于全局影响：
/// WMI 会拉起 Winmgmt 并让调用方同步等待，旧版曾因调度用 WMI 查进程把系统拖到半崩；
/// PerformanceCounter 首次访问要加载计数器库；GetProcessesByName 每次枚举全表。
/// 约定一旦破，问题只会在用户机器上以"卡顿"出现，因此用测试钉住。
/// </summary>
public sealed class PerformanceGuardTests
{
    private static readonly string[] ForbiddenApis =
    [
        "System.Management",
        "ManagementObjectSearcher",
        "ManagementObject",
        "PerformanceCounter",
        "GetProcessesByName",
        "Process.GetProcesses",
        "ServiceController",
    ];

    [Fact]
    public void Production_code_avoids_globally_expensive_apis()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        Assert.True(Directory.Exists(sourceRoot), $"未找到源码目录：{sourceRoot}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (IsComment(line))
                {
                    continue;
                }

                foreach (var api in ForbiddenApis)
                {
                    if (line.Contains(api, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{lineNumber} 使用了 {api}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "生产代码出现高开销 API：\n" + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("*", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Zentwik.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未能从测试输出目录向上找到仓库根（Zentwik.slnx）。");
    }
}
