using Zentwik.Domain.Plans;

namespace Zentwik.Domain.Tests;

public sealed class CurveOptimizerConfigTests
{
    [Fact]
    public void Resolve_Follows_Auto_Over_PerCore_Over_Ccd_Over_Default_Precedence()
    {
        var plan = new CpuTuningPlan
        {
            Id = "co-layered",
            Name = "Layered CO",
            DefaultOffset = -10,
            CcdOffsets = new Dictionary<int, int>
            {
                [0] = -20,
                [1] = -15,
            },
            Cores =
            [
                new CoreTuning { CoreIndex = 0, Offset = -25 },
                new CoreTuning { CoreIndex = 2, Auto = true },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var resolved = CurveOptimizerConfig.Resolve(
            plan,
            [0, 1, 2, 3, 4, 5],
            core => core < 4 ? 0 : (core < 6 ? 1 : null));

        Assert.Equal(-25, resolved[0].EffectiveOffset);
        Assert.Equal(CoValueSource.PerCore, resolved[0].Source);

        Assert.Equal(-20, resolved[1].EffectiveOffset);
        Assert.Equal(CoValueSource.Ccd, resolved[1].Source);

        Assert.Null(resolved[2].EffectiveOffset);
        Assert.Equal(CoValueSource.AutoOverride, resolved[2].Source);

        Assert.Equal(-20, resolved[3].EffectiveOffset);
        Assert.Equal(CoValueSource.Ccd, resolved[3].Source);

        Assert.Equal(-15, resolved[4].EffectiveOffset);
        Assert.Equal(CoValueSource.Ccd, resolved[4].Source);

        Assert.Equal(-15, resolved[5].EffectiveOffset);
        Assert.Equal(CoValueSource.Ccd, resolved[5].Source);
    }

    [Fact]
    public void Resolve_Falls_Back_To_Default_When_No_Ccd_Match()
    {
        var plan = new CpuTuningPlan
        {
            Id = "co-default-fallback",
            Name = "Default Fallback",
            DefaultOffset = -12,
            CcdOffsets = new Dictionary<int, int> { [0] = -20 },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var resolved = CurveOptimizerConfig.Resolve(plan, [0, 4], core => core < 4 ? 0 : 1);

        Assert.Equal(-20, resolved[0].EffectiveOffset);
        Assert.Equal(CoValueSource.Ccd, resolved[0].Source);
        Assert.Equal(-12, resolved[1].EffectiveOffset);
        Assert.Equal(CoValueSource.Default, resolved[1].Source);
    }

    [Fact]
    public void AnalyzeAsymmetry_Distinguishes_Symmetric_And_Asymmetric_Plans()
    {
        var symmetric = new CpuTuningPlan
        {
            Id = "sym",
            Name = "Symmetric",
            DefaultOffset = -15,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var symSummary = CurveOptimizerConfig.AnalyzeAsymmetry(symmetric, [0, 1, 2, 3]);
        Assert.False(symSummary.IsAsymmetric);
        Assert.Equal(-15, symSummary.MinOffset);
        Assert.Equal(-15, symSummary.MaxOffset);

        var asymmetric = new CpuTuningPlan
        {
            Id = "asym",
            Name = "Asymmetric",
            DefaultOffset = -15,
            Cores =
            [
                new CoreTuning { CoreIndex = 0, Offset = -25 },
                new CoreTuning { CoreIndex = 1, Auto = true },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var asymSummary = CurveOptimizerConfig.AnalyzeAsymmetry(asymmetric, [0, 1, 2, 3]);
        Assert.True(asymSummary.IsAsymmetric);
        Assert.Equal(-25, asymSummary.MinOffset);
        Assert.Equal(-15, asymSummary.MaxOffset);
        Assert.Equal(1, asymSummary.AutoOverrideCount);

        var text = CurveOptimizerConfig.FormatPlanSummary(asymmetric);
        Assert.Contains("default=-15", text);
        Assert.Contains("C0:-25", text);
        Assert.Contains("C1:auto", text);
        Assert.Contains("[asymmetric -25..-15]", text);
    }
}