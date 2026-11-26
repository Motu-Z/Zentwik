using Xunit;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Tests;

/// <summary>
/// 位域与单位口径全部对照 LibreHardwareMonitor 的 Amd17Cpu 与 Linux k10temp；
/// 实测值见研究仓库 knowledge/hardware/cpu/amd-power-limit.md §8.3。
/// </summary>
public sealed class AmdSensorDecoderTests
{
    [Fact]
    public void Temperature_Applies_Range_Select_Offset()
    {
        // 本机实测 0x757B0000：bit19 置位，939 * 0.125 - 49 = 68.375。
        Assert.Equal(68.375, AmdSensorDecoder.DecodeTemperatureCelsius(0x757B0000), 3);
    }

    [Fact]
    public void Temperature_Without_Offset_Flag_Is_Raw_Value()
    {
        Assert.Equal(100d, AmdSensorDecoder.DecodeTemperatureCelsius(800u << 21), 3);
    }

    [Fact]
    public void Temperature_TJunction_Select_Also_Applies_Offset()
    {
        var raw = (800u << 21) | 0x30000;
        Assert.Equal(51d, AmdSensorDecoder.DecodeTemperatureCelsius(raw), 3);
    }

    [Fact]
    public void Energy_Exponent_Comes_From_Bits_12_8()
    {
        Assert.Equal(16, AmdSensorDecoder.EnergyExponent((16u << 8) | 0x03));
    }

    [Fact]
    public void Energy_Delta_Wraps_As_Unsigned()
    {
        Assert.Equal(512u, AmdSensorDecoder.EnergyDelta(0xFFFFFF00, 0x00000100));
    }

    [Fact]
    public void Energy_To_Watts_Uses_Joule_Unit()
    {
        // 65536 计数 × 2^-16 焦耳 = 1 焦耳；1 秒内即 1 W。
        Assert.Equal(1d, AmdSensorDecoder.EnergyToWatts(65536, 16, TimeSpan.FromSeconds(1)), 6);
        Assert.Equal(1d, AmdSensorDecoder.EnergyToWatts(131072, 16, TimeSpan.FromSeconds(2)), 6);
    }

    [Fact]
    public void Zero_Window_Yields_Zero_Instead_Of_Infinity()
    {
        Assert.Equal(0d, AmdSensorDecoder.EnergyToWatts(65536, 16, TimeSpan.Zero));
    }

    [Fact]
    public void Cpu_Identifier_Parses_Decimal_Family_And_Model()
    {
        Assert.True(AmdCpuIdentifier.TryParse("AMD64 Family 25 Model 97 Stepping 2", out var family, out var model));
        Assert.Equal(0x19u, family);
        Assert.Equal(0x61u, model);
    }

    [Fact]
    public void Cpu_Identifier_Without_Family_Is_Rejected()
    {
        Assert.False(AmdCpuIdentifier.TryParse("AMD64 Processor", out _, out _));
        Assert.False(AmdCpuIdentifier.TryParse(null, out _, out _));
    }
}
