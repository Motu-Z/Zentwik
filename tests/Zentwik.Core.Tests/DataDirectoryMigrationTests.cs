using Xunit;
using Zentwik.Core.State;

namespace Zentwik.Core.Tests;

public sealed class DataDirectoryMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Zentwik.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Migrates_When_Target_Has_No_State()
    {
        var source = Path.Combine(_root, "ZentwikNext");
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        File.WriteAllText(Path.Combine(source, "state.json"), """{"schemaVersion":1}""");
        File.WriteAllText(Path.Combine(source, "owner.sid"), "S-1-5-21");
        File.WriteAllText(Path.Combine(source, "logs", "agent.log"), "line");
        var target = Path.Combine(_root, "Zentwik");

        var outcome = DataDirectoryMigration.TryMigrate(source, target);

        Assert.True(outcome.Executed);
        Assert.Null(outcome.Error);
        Assert.Equal("""{"schemaVersion":1}""", File.ReadAllText(Path.Combine(target, "state.json")));
        Assert.Equal("S-1-5-21", File.ReadAllText(Path.Combine(target, "owner.sid")));
        Assert.True(File.Exists(Path.Combine(target, "logs", "agent.log")));

        // 旧目录保留，搬迁不是移动：用户回退旧版时数据还在。
        Assert.True(File.Exists(Path.Combine(source, "state.json")));
    }

    [Fact]
    public void Skips_When_Target_Already_Has_State()
    {
        var source = Path.Combine(_root, "ZentwikNext");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "state.json"), "old");

        var target = Path.Combine(_root, "Zentwik");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "state.json"), "current");

        var outcome = DataDirectoryMigration.TryMigrate(source, target);

        Assert.False(outcome.Executed);
        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "state.json")));
    }

    [Fact]
    public void Skips_When_Source_Missing()
    {
        var outcome = DataDirectoryMigration.TryMigrate(
            Path.Combine(_root, "ZentwikNext"),
            Path.Combine(_root, "Zentwik"));

        Assert.False(outcome.Executed);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void Skips_When_Source_Equals_Target()
    {
        var target = Path.Combine(_root, "Zentwik");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "owner.sid"), "S-1");

        var outcome = DataDirectoryMigration.TryMigrate(target, target);

        Assert.False(outcome.Executed);
        Assert.Equal("S-1", File.ReadAllText(Path.Combine(target, "owner.sid")));
    }

    [Fact]
    public void Merges_When_Target_Already_Has_Other_Files()
    {
        var source = Path.Combine(_root, "ZentwikNext");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "state.json"), "from-old");
        File.WriteAllText(Path.Combine(source, "owner.sid"), "old-owner");

        // 安装阶段会先在新目录写出 owner.sid；它不能让整个搬迁失败。
        var target = Path.Combine(_root, "Zentwik");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "owner.sid"), "new-owner");

        var outcome = DataDirectoryMigration.TryMigrate(source, target);

        Assert.True(outcome.Executed);
        Assert.Equal("from-old", File.ReadAllText(Path.Combine(target, "state.json")));
        Assert.Equal("new-owner", File.ReadAllText(Path.Combine(target, "owner.sid")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
