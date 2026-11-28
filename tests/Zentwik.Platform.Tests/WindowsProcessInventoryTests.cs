using System.Diagnostics;
using Xunit;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Tests;

/// <summary>
/// 全表进程快照的本机实测。这条测试会真的调 NtQuerySystemInformation 并解析结构体，
/// 用来兜住 x64 偏移与 UNICODE_STRING 指针这两处最容易错的地方——fake 测试发现不了。
/// 它不是真机闭环：只覆盖「本机当前用户会话读得到」这一种情形。
/// </summary>
public sealed class WindowsProcessInventoryTests
{
    [Fact]
    public void Snapshot_Reads_Own_Process_With_Name_Path_And_Cpu()
    {
        var inventory = new WindowsProcessInventory();
        var self = Environment.ProcessId;

        var entries = inventory.Snapshot([self]);

        Assert.NotEmpty(entries);
        var mine = Assert.Single(entries, entry => entry.ProcessId == self);
        Assert.False(string.IsNullOrWhiteSpace(mine.Name));
        Assert.NotNull(mine.ImagePath);
        Assert.Contains("Zentwik", mine.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(mine.CpuPercent);
        Assert.InRange(mine.CpuPercent!.Value, 0d, 100d);

        // 全表都该有 PID 与名字；占用率来自累计时间差分，同样每行都有。
        Assert.All(entries, static entry => Assert.True(entry.ProcessId > 0));
        Assert.All(entries, static entry => Assert.False(string.IsNullOrWhiteSpace(entry.Name)));
        Assert.All(entries, static entry => Assert.NotNull(entry.CpuPercent));

        // 系统进程（PID 4）不该被当成可操作进程过滤掉，但它的路径通常读不到。
        Assert.Contains(entries, static entry => entry.ProcessId == 4);
    }

    [Fact]
    public void Snapshot_Reuses_Cache_Within_Ttl()
    {
        var inventory = new WindowsProcessInventory();
        var first = inventory.Snapshot([]);
        var watch = Stopwatch.StartNew();
        var second = inventory.Snapshot([]);
        watch.Stop();

        Assert.Equal(first.Count, second.Count);

        // 2 秒缓存命中时不会再有全表采集，耗时应当远小于一次采集（含可能的 200ms 补基线）。
        Assert.True(watch.ElapsedMilliseconds < 150, $"第二次快照耗时 {watch.ElapsedMilliseconds} ms，缓存未生效。");
    }
}

/// <summary>
/// 档位读写的本机实测：对自建子进程设置 EcoQoS 再读回，验证「Idle 类 + 节流位」这条路径。
/// 只动本测试自己拉起的进程，不碰用户已调好的任何设置。
/// </summary>
public sealed class AffinitySchedulerEcoQoSTests
{
    [Fact]
    public void EcoQoS_Is_Readable_After_Apply_And_Cleared_By_Normal()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        Assert.NotNull(child);
        try
        {
            var scheduler = new AffinityScheduler();

            Assert.True(scheduler.TryApply(child!.Id, null, "EcoQoS", out var applyError), applyError);
            Assert.True(scheduler.TryRead(child.Id, out _, out var priority, out var readError), readError);
            Assert.Equal("EcoQoS", priority);

            // 换回普通档位必须把节流位清掉，否则读回来还是 EcoQoS。
            Assert.True(scheduler.TryApply(child.Id, null, "Normal", out applyError), applyError);
            Assert.True(scheduler.TryRead(child.Id, out _, out priority, out readError), readError);
            Assert.Equal("Normal", priority);
        }
        finally
        {
            if (!child!.HasExited)
            {
                child.Kill();
            }
        }
    }

    [Fact]
    public void Unknown_Token_Is_Rejected()
    {
        var scheduler = new AffinityScheduler();

        Assert.False(scheduler.TryApply(Environment.ProcessId, null, "Above Normal", out var error));
        Assert.Contains("未知进程优先级", error);
    }
}
