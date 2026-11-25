using Xunit;
using Zentwik.Domain.Ownership;

namespace Zentwik.Domain.Tests;

public sealed class StateLedgerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Acquire_Records_PreviousValue_And_Commit_MarksApplied()
    {
        var ledger = new StateLedger();

        var outcome = ledger.Acquire("power.active_scheme", "scene.game", 10, "balanced-guid", "high-perf-guid");
        ledger.Commit("power.active_scheme", "high-perf-guid");

        Assert.Equal(AcquireOutcome.Acquired, outcome);
        var entry = ledger.Get("power.active_scheme");
        Assert.NotNull(entry);
        Assert.Equal("balanced-guid", entry.PreviousValue);
        Assert.Equal(LedgerStatus.Applied, entry.Status);
        Assert.True(entry.Restorable);
    }

    [Fact]
    public void Commit_With_UnexpectedActualValue_MarksFailed()
    {
        var ledger = new StateLedger();
        ledger.Acquire("cpu.core0.voltage", "scene.cool", 5, "-0", "-30");
        ledger.Commit("cpu.core0.voltage", "-10");

        Assert.Equal(LedgerStatus.Failed, ledger.Get("cpu.core0.voltage")!.Status);
    }

    [Fact]
    public void UserTakeover_Blocks_Scene_And_Removes_From_Revert_Plan()
    {
        var ledger = new StateLedger();
        ledger.Acquire("power.active_scheme", "scene.game", 10, "balanced", "high");
        ledger.Commit("power.active_scheme", "high");

        ledger.MarkUserTakeover("power.active_scheme", "custom");

        var second = ledger.Acquire("power.active_scheme", "scene.game", 10, "custom", "high");
        Assert.Equal(AcquireOutcome.DeniedUserOwned, second);
        Assert.Empty(ledger.PlanRevert("scene.game"));
    }

    [Fact]
    public void LowerPriority_Scene_Cannot_Override_Applied_HigherPriority()
    {
        var ledger = new StateLedger();
        ledger.Acquire("power.active_scheme", "scene.game", 20, "balanced", "high");
        ledger.Commit("power.active_scheme", "high");

        var outcome = ledger.Acquire("power.active_scheme", "scene.idle", 5, "high", "power-saver");

        Assert.Equal(AcquireOutcome.DeniedLowerPriority, outcome);
        Assert.Equal("high", ledger.Get("power.active_scheme")!.ActualValue);
    }

    [Fact]
    public void HigherPriority_Scene_Takeover_Keeps_Original_PreviousValue()
    {
        var ledger = new StateLedger();
        ledger.Acquire("power.active_scheme", "scene.idle", 5, "balanced", "saver");
        ledger.Commit("power.active_scheme", "saver");

        ledger.Acquire("power.active_scheme", "scene.game", 20, "saver", "high");
        ledger.Commit("power.active_scheme", "high");

        var entry = ledger.Get("power.active_scheme")!;
        Assert.Equal("balanced", entry.PreviousValue);
        Assert.Equal("scene.game", entry.Source);
        Assert.Equal(20, entry.Priority);
    }

    [Fact]
    public void Revert_Skips_Failed_Entries_And_Keeps_Them_Visible()
    {
        var ledger = new StateLedger();
        ledger.Acquire("cpu.core1.voltage", "scene.cool", 10, "0", "-20");
        ledger.Commit("cpu.core1.voltage", "-5");

        Assert.Empty(ledger.PlanRevert("scene.cool"));
        Assert.Equal(LedgerStatus.Failed, ledger.Get("cpu.core1.voltage")!.Status);
    }

    [Fact]
    public void MarkReverted_Failure_Keeps_Ownership_For_Retry()
    {
        var ledger = new StateLedger();
        ledger.Acquire("power.active_scheme", "scene.game", 10, "balanced", "high");
        ledger.Commit("power.active_scheme", "high");

        ledger.MarkReverted("power.active_scheme", "balanced", success: false, failure: "access denied");

        var entry = ledger.Get("power.active_scheme")!;
        Assert.Equal(OwnerKind.Scene, entry.Owner);
        Assert.True(entry.Restorable);
        Assert.Single(ledger.PlanRevert("scene.game"));
    }

    [Fact]
    public void ReleaseUserTakeover_Reopens_Scene_Ownership()
    {
        var ledger = new StateLedger();
        ledger.MarkUserTakeover("power.active_scheme", "custom");
        Assert.True(ledger.ReleaseUserTakeover("power.active_scheme"));

        var outcome = ledger.Acquire("power.active_scheme", "scene.game", 10, "custom", "high");
        Assert.Equal(AcquireOutcome.Acquired, outcome);
    }

    [Fact]
    public void Timestamps_Use_Injected_Clock()
    {
        var clock = new FixedClock(T0);
        var ledger = new StateLedger(clock);
        ledger.Acquire("power.active_scheme", "scene.game", 10, null, "high");

        Assert.Equal(T0, ledger.Get("power.active_scheme")!.UpdatedAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
