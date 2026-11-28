using Xunit;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Tests;

/// <summary>
/// 档位字符串与 Win32 优先级类的映射。EcoQoS 与 Idle 共用 Idle 类，只靠节流状态位区分，
/// 所以这里既测「EcoQoS 映射成 Idle 类 + eco=true」，也测回读描述不会把两者混为一谈。
/// </summary>
public sealed class ProcessPriorityTokensTests
{
    [Theory]
    [InlineData("High", 0x00000080u)]
    [InlineData("AboveNormal", 0x00008000u)]
    [InlineData("Normal", 0x00000020u)]
    [InlineData("BelowNormal", 0x00004000u)]
    [InlineData("Idle", 0x00000040u)]
    [InlineData("RealTime", 0x00000100u)]
    public void TryMap_Maps_Enum_Names(string token, uint expectedClass)
    {
        Assert.True(ProcessPriorityTokens.TryMap(token, out var priorityClass, out var eco));
        Assert.Equal(expectedClass, priorityClass);
        Assert.False(eco);
    }

    [Fact]
    public void TryMap_Is_Case_Insensitive_And_Trims()
    {
        Assert.True(ProcessPriorityTokens.TryMap("  abovenormal ", out var priorityClass, out _));
        Assert.Equal(0x00008000u, priorityClass);
    }

    [Fact]
    public void TryMap_EcoQoS_Is_Idle_Class_With_Throttling()
    {
        Assert.True(ProcessPriorityTokens.TryMap("EcoQoS", out var priorityClass, out var eco));
        Assert.Equal(0x00000040u, priorityClass);
        Assert.True(eco);
    }

    [Fact]
    public void TryMap_Rejects_Unknown_Token()
    {
        // 旧版界面文案带空格、用 Low 表示空闲；新版数据层只认枚举名，避免写下去再回读对不上。
        Assert.False(ProcessPriorityTokens.TryMap("Above Normal", out _, out _));
        Assert.False(ProcessPriorityTokens.TryMap("Low", out _, out _));
        Assert.False(ProcessPriorityTokens.TryMap("turbo", out _, out _));
        Assert.False(ProcessPriorityTokens.TryMap(null, out _, out _));
    }

    [Theory]
    [InlineData("High")]
    [InlineData("AboveNormal")]
    [InlineData("Normal")]
    [InlineData("BelowNormal")]
    [InlineData("Idle")]
    [InlineData("EcoQoS")]
    [InlineData("RealTime")]
    public void Describe_Round_Trips_Token(string token)
    {
        Assert.True(ProcessPriorityTokens.TryMap(token, out var priorityClass, out var eco));
        Assert.Equal(token, ProcessPriorityTokens.Describe(priorityClass, eco));
    }

    [Fact]
    public void Describe_Distinguishes_Idle_From_EcoQoS()
    {
        Assert.Equal("Idle", ProcessPriorityTokens.Describe(0x00000040u, eco: false));
        Assert.Equal("EcoQoS", ProcessPriorityTokens.Describe(0x00000040u, eco: true));
    }

    [Theory]
    [InlineData(4, "Idle")]
    [InlineData(6, "BelowNormal")]
    [InlineData(8, "Normal")]
    [InlineData(10, "AboveNormal")]
    [InlineData(13, "High")]
    [InlineData(24, "Normal")]
    public void DescribeBasePriority_Maps_Toolhelp_Values(int basePriority, string expected)
    {
        Assert.Equal(expected, ProcessPriorityTokens.DescribeBasePriority(basePriority));
    }
}
