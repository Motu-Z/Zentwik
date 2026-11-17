using System;
using System.Collections.Generic;
using System.Linq;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>场景调度去重键：进程集合含创建时间，PID 复用后必须重发。</summary>
    public sealed class ScenarioSchedulingKeyTests
    {
        private static Func<int, long> Ticks(params (int Pid, long Ticks)[] items)
        {
            Dictionary<int, long> map = items.ToDictionary(i => i.Pid, i => i.Ticks);
            return pid => map.TryGetValue(pid, out long ticks) ? ticks : 0;
        }

        [Fact]
        public void Same_processes_repeat_the_same_key()
        {
            Func<int, long> ticks = Ticks((100, 111), (200, 222));

            Assert.Equal(
                ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100, 200 }, ticks),
                ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100, 200 }, ticks));
        }

        /// <summary>PID 被复用：同号新进程的创建时间不同，不能被旧键挡掉。</summary>
        [Fact]
        public void Reused_pid_changes_the_key()
        {
            string oldKey = ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100 }, Ticks((100, 111)));
            string newKey = ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100 }, Ticks((100, 999)));

            Assert.NotEqual(oldKey, newKey);
        }

        [Fact]
        public void Different_process_sets_differ()
        {
            Func<int, long> ticks = Ticks((100, 111), (200, 222));

            Assert.NotEqual(
                ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100 }, ticks),
                ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100, 200 }, ticks));
        }

        [Fact]
        public void Process_order_does_not_matter()
        {
            Func<int, long> ticks = Ticks((100, 111), (200, 222));

            Assert.Equal(
                ScenarioService.SchedulingKey("m1", "game.exe", new[] { 200, 100 }, ticks),
                ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100, 200 }, ticks));
        }

        [Fact]
        public void Mask_and_argument_still_partition_the_key()
        {
            Func<int, long> ticks = Ticks((100, 111));

            Assert.NotEqual(
                ScenarioService.SchedulingKey("m1", "game.exe", new[] { 100 }, ticks),
                ScenarioService.SchedulingKey("m2", "game.exe", new[] { 100 }, ticks));
        }

        [Fact]
        public void Empty_set_ignores_instance_ticks()
        {
            Assert.Equal(
                "m1|game.exe",
                ScenarioService.SchedulingKey("m1", "game.exe", Array.Empty<int>(), _ => 0));
        }
    }
}
