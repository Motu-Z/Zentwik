using Xunit;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Tests;

/// <summary>
/// PM 表字段解码的边界。重点是「未传输时的残留位模式」：它解出来是 1e-23 这类极小的正数，
/// 用 <c>&gt; 0</c> 判断会被骗过、舍入成 0 后当成有效基线，从此不再触发强制传输（2026-11-28 实测踩过）。
/// </summary>
public sealed class AmdPmTableDecodeTests
{
    [Theory]
    [InlineData(1e-23f)]
    [InlineData(8.44401e-23f)]
    [InlineData(1.12659e-13f)]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(1001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Unfilled_Or_Out_Of_Range_Is_Null_Not_Zero(float raw) =>
        Assert.Null(AmdSmuTuningProvider.ToWatts(raw));

    [Theory]
    [InlineData(56f, 56)]
    [InlineData(100f, 100)]
    [InlineData(68f, 68)]
    [InlineData(30.8453f, 31)]
    [InlineData(44.5542f, 45)]
    [InlineData(1f, 1)]
    [InlineData(1000f, 1000)]
    public void Real_Reading_Rounds_To_Whole_Watts(float raw, int expected) =>
        Assert.Equal(expected, AmdSmuTuningProvider.ToWatts(raw));
}
