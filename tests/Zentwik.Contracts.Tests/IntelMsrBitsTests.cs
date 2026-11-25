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
            var encoded = IntelMsrBits.EncodeVoltageOffset(0, millivolts);
            var decoded = IntelMsrBits.DecodeVoltageOffset(encoded);

            Assert.InRange(decoded, millivolts - 1, millivolts + 1);
        }
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
    public void VoltageOffset_KeepsUnrelatedBits()
    {
        const long other = 0x0000_0000_00AB_CDEF;
        const long field = 0x7FFL << 21;

        var encoded = IntelMsrBits.EncodeVoltageOffset(other, -25);

        Assert.Equal(other & ~field, encoded & ~field);
        Assert.InRange(IntelMsrBits.DecodeVoltageOffset(encoded), -26, -24);
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
