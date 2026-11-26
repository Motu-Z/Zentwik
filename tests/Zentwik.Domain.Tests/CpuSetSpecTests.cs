using Xunit;
using Zentwik.Domain.Plans;

namespace Zentwik.Domain.Tests;

public sealed class CpuSetSpecTests
{
    [Fact]
    public void TryParse_And_Format_Normalize_Ranges_And_Lists()
    {
        Assert.True(CpuSetSpec.TryParse("6, 0-3, 8-9, 2", out var indices, out var error), error);
        Assert.Equal(new[] { 0, 1, 2, 3, 6, 8, 9 }, indices);
        Assert.Equal("0,1,2,3,6,8,9", CpuSetSpec.Format(indices));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-1")]
    [InlineData("4-2")]
    [InlineData("0,,2")]
    [InlineData("abc")]
    public void TryParse_Rejects_Invalid_Specs(string spec)
    {
        Assert.False(CpuSetSpec.TryParse(spec, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ValidateAndResolve_Maps_Known_Indices_And_Rejects_Missing_Ones()
    {
        var map = new Dictionary<int, uint>
        {
            [0] = 256,
            [1] = 257,
            [2] = 258,
            [3] = 259,
        };

        Assert.True(CpuSetSpec.ValidateAndResolve([0, 2, 3], map, out var ids, out var error), error);
        Assert.Equal(new uint[] { 256u, 258u, 259u }, ids);

        Assert.False(CpuSetSpec.ValidateAndResolve([0, 7], map, out _, out var missingError));
        Assert.Contains("7", missingError ?? string.Empty);
    }
}