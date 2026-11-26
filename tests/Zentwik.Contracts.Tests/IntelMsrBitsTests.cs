using Xunit;
using Zentwik.Platform.Windows;

namespace Zentwik.Contracts.Tests;

public sealed class IntelMsrBitsTests
{
    [Fact]
    public void VoltageOffset_EncodesAndDecodesBack()
    {
        foreach (var millivolts in new[] { -150, -100, -50, -30, -10, -1, 0, 1, 10, 50, 100 })
        {
            var command = IntelMsrBits.BuildWriteCommand(millivolts);
            var decoded = IntelMsrBits.DecodeVoltageOffset(command);

            Assert.InRange(decoded, millivolts - 1, millivolts + 1);
        }
    }

    [Fact]
    public void WriteCommand_PutsCommandWordInHighBits()
    {
        var command = IntelMsrBits.BuildWriteCommand(-25);

        Assert.Equal(IntelMsrBits.WriteCommand, (uint)(command >> 32));
        Assert.Equal(IntelMsrBits.OffsetCode(-25) & 0x7FF, (command >> 21) & 0x7FF);
    }

    [Fact]
    public void ReadCommand_CarriesPlaneBits()
    {
        Assert.Equal((long)(IntelMsrBits.ReadCommand | IntelMsrBits.CachePlane) << 32, IntelMsrBits.BuildReadCommand(IntelMsrBits.CachePlane));
        Assert.Equal(0L, IntelMsrBits.BuildReadCommand() & 0xFFFF_FFFFL);
    }

    [Fact]
    public void BusyFlag_IsTheSignBit()
    {
        // 命令字最高位即忙位：写入命令发出后由硬件清零。
        Assert.True(IntelMsrBits.IsBusy(IntelMsrBits.BuildWriteCommand(-25)));
        Assert.True(IntelMsrBits.IsBusy(IntelMsrBits.BuildReadCommand()));
        Assert.False(IntelMsrBits.IsBusy((long)0x11 << 32));
    }

    [Fact]
    public void VoltageOffset_UsesSignedElevenBits()
    {
        var minusOne = (0x7FFL << 21);
        var minusHundred = (unchecked((long)(-102)) & 0x7FF) << 21;

        Assert.Equal(-1, IntelMsrBits.DecodeVoltageOffset(minusOne));
        Assert.Equal(-100, IntelMsrBits.DecodeVoltageOffset(minusHundred));
    }

    [Fact]
    public void TurboRatio_ReadsLowAndHighFields()
    {
        var msr = 0x2AL << 56 | 0x0CL << 32 | 0x26L;

        Assert.Equal(0x26, IntelMsrBits.DecodeTurboRatio(msr, 1));
        Assert.Equal(0x0C, IntelMsrBits.DecodeTurboRatio(msr, 5));
        Assert.Equal(0x2A, IntelMsrBits.DecodeTurboRatio(msr, 8));
    }

    [Fact]
    public void TurboRatio_ClampsCoreCount()
    {
        var msr = 0x31L << 56 | 0x2FL;

        Assert.Equal(0x2F, IntelMsrBits.DecodeTurboRatio(msr, 0));
        Assert.Equal(0x31, IntelMsrBits.DecodeTurboRatio(msr, 9));
    }
}
