using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>
    /// 启动护栏：连续「写 SMU 后没走完」累计到阈值就拦下一次自动调校。
    /// </summary>
    /// <remarks>
    /// 这些用例把「蓝屏 / 断电」建模成「BeginWrite 之后不再 MarkHealthy，直接新建一个新实例」——
    /// 进程崩掉之后，下一次启动看到的正是这个状态。
    /// </remarks>
    public sealed class BootGuardTests
    {
        [Fact]
        public void FirstStart_isNotBlocked()
        {
            var paths = new FakeAppPaths();
            var guard = new BootGuard(new FakeLogService(), paths);

            BootGuardSnapshot snapshot = guard.Enter();

            Assert.False(snapshot.Blocked);
            Assert.Equal(0, snapshot.Failures);
            Assert.False(snapshot.RecoveredFromCrash);
        }

        [Fact]
        public void CleanWrite_doesNotCountAsFailure()
        {
            var paths = new FakeAppPaths();

            var first = new BootGuard(new FakeLogService(), paths);
            first.Enter();
            first.BeginWrite();
            first.MarkHealthy();

            var second = new BootGuard(new FakeLogService(), paths);
            BootGuardSnapshot snapshot = second.Enter();

            Assert.False(snapshot.Blocked);
            Assert.Equal(0, snapshot.Failures);
            Assert.False(snapshot.RecoveredFromCrash);
        }

        [Fact]
        public void CrashDuringWrite_countsOnce()
        {
            var paths = new FakeAppPaths();

            var crashed = new BootGuard(new FakeLogService(), paths);
            crashed.Enter();
            crashed.BeginWrite();

            // 机器在这个点蓝屏：MarkHealthy 从未执行。

            var next = new BootGuard(new FakeLogService(), paths);
            BootGuardSnapshot snapshot = next.Enter();

            Assert.False(snapshot.Blocked);
            Assert.Equal(1, snapshot.Failures);
            Assert.True(snapshot.RecoveredFromCrash);
        }

        [Fact]
        public void ThreeConsecutiveCrashes_blockTheNextStart()
        {
            var paths = new FakeAppPaths();

            for (int i = 0; i < BootGuard.MaxConsecutiveFailures; i++)
            {
                var crashed = new BootGuard(new FakeLogService(), paths);
                crashed.Enter();
                crashed.BeginWrite();
            }

            var guarded = new BootGuard(new FakeLogService(), paths);
            BootGuardSnapshot snapshot = guarded.Enter();

            Assert.True(snapshot.Blocked);
            Assert.Equal(BootGuard.MaxConsecutiveFailures, snapshot.Failures);
        }

        [Fact]
        public void ManualApply_clearsTheBlock()
        {
            var paths = new FakeAppPaths();

            for (int i = 0; i < BootGuard.MaxConsecutiveFailures; i++)
            {
                var crashed = new BootGuard(new FakeLogService(), paths);
                crashed.Enter();
                crashed.BeginWrite();
            }

            var blocked = new BootGuard(new FakeLogService(), paths);
            Assert.True(blocked.Enter().Blocked);

            // 用户在调校页手动点「应用参数」：写入成功即视为已确认这套参数能活。
            blocked.BeginWrite();
            blocked.MarkHealthy();

            var next = new BootGuard(new FakeLogService(), paths);
            BootGuardSnapshot snapshot = next.Enter();

            Assert.False(snapshot.Blocked);
            Assert.Equal(0, snapshot.Failures);
        }

        [Fact]
        public void Enter_clearsPending_soOneCrashCountsOnlyOnce()
        {
            var paths = new FakeAppPaths();

            var crashed = new BootGuard(new FakeLogService(), paths);
            crashed.Enter();
            crashed.BeginWrite();

            // 这次启动被判「上次没走完」，但它自己没写硬件 → 不该继续挂着同一个标记。
            var second = new BootGuard(new FakeLogService(), paths);
            second.Enter();

            var third = new BootGuard(new FakeLogService(), paths);
            BootGuardSnapshot snapshot = third.Enter();

            Assert.Equal(1, snapshot.Failures);
        }

        [Fact]
        public void FailedSmokeWrite_stillCountsAsHealthy()
        {
            var paths = new FakeAppPaths();

            // 写入被 SMU 拒绝（参数越界）≠ 砖。流程跑完就应当清零，否则
            // 「参数写错了三次」会被误判成「机器要砖了」。
            var guard = new BootGuard(new FakeLogService(), paths);
            guard.Enter();
            guard.BeginWrite();
            guard.MarkHealthy();

            var next = new BootGuard(new FakeLogService(), paths);

            Assert.Equal(0, next.Enter().Failures);
        }
    }
}
