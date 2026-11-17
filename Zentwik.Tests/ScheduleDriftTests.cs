using System.Collections.Generic;
using System.Linq;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>读回比对用的期望值账本。</summary>
    public sealed class ScheduleDriftTests
    {
        [Fact]
        public void Note_keeps_the_latest_binding_for_a_pid()
        {
            ScheduleDriftTracker tracker = new();
            tracker.Note(10, "a.exe", 0x0FUL, byAffinityMask: false);
            tracker.Note(10, "a.exe", 0xF0UL, byAffinityMask: true);

            ScheduleDriftTracker.Entry entry = Assert.Single(tracker.Snapshot());

            Assert.Equal(0xF0UL, entry.Mask);
            Assert.True(entry.ByAffinityMask);
            Assert.Equal("a.exe", entry.Name);
        }

        [Fact]
        public void Forget_drops_the_pid()
        {
            ScheduleDriftTracker tracker = new();
            tracker.Note(10, "a.exe", 0x0FUL, byAffinityMask: false);
            tracker.Forget(10);

            Assert.Empty(tracker.Snapshot());
            Assert.Equal(0, tracker.Count);
        }

        [Fact]
        public void Prune_drops_processes_that_are_gone()
        {
            ScheduleDriftTracker tracker = new();
            tracker.Note(10, "a.exe", 0x0FUL, byAffinityMask: false);
            tracker.Note(11, "b.exe", 0xF0UL, byAffinityMask: false);

            tracker.PruneTo(new HashSet<int> { 11 });

            Assert.Equal(new[] { 11 }, tracker.Snapshot().Select(e => e.Pid).ToArray());
        }

        [Fact]
        public void Zero_mask_is_ignored()
        {
            ScheduleDriftTracker tracker = new();
            tracker.Note(0, "a.exe", 0x0FUL, byAffinityMask: false);
            tracker.Note(10, "a.exe", 0, byAffinityMask: false);

            Assert.Equal(0, tracker.Count);
        }

        [Fact]
        public void TryGet_matches_the_snapshot_and_drops_after_forget()
        {
            ScheduleDriftTracker tracker = new();
            tracker.Note(10, "a.exe", 0x0FUL, byAffinityMask: false);

            Assert.True(tracker.TryGet(10, out ScheduleDriftTracker.Entry entry));
            Assert.Equal(entry, Assert.Single(tracker.Snapshot()));

            tracker.Forget(10);

            Assert.False(tracker.TryGet(10, out _));
        }
    }
}
