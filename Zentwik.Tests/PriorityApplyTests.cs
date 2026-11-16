using System;
using System.Diagnostics;
using Xunit;
using Zentwik.Core.Services;

namespace Zentwik.Tests
{
    /// <summary>
    /// 优先级下发只在本进程上验：改自己的优先级不需要额外权限，
    /// 也能同时证明 SetProcessInformation 的声明与结构体尺寸是对的。
    /// </summary>
    public sealed class PriorityApplyTests
    {
        [Fact]
        public void Options_EndsWithEcoQoS()
        {
            Assert.Equal(6, ProcessPriority.Options.Length);
            Assert.Equal("高", ProcessPriority.DisplayOf(ProcessPriority.Options[0]));
            Assert.Equal("效率模式", ProcessPriority.DisplayOf(ProcessPriority.Options[5]));
            Assert.Equal(ProcessPriority.Low, ProcessPriority.Options[4]);
        }

        [Fact]
        public void EcoQoS_AppliesAndNormalClears()
        {
            int pid = Environment.ProcessId;

            try
            {
                Assert.Equal(1, ProcessPriority.Apply(pid, ProcessPriority.EcoQoS).Ok);
            }
            finally
            {
                Assert.Equal(1, ProcessPriority.Apply(pid, ProcessPriority.Normal).Ok);
            }
        }

        [Fact]
        public void UnknownLabel_Fails()
        {
            PriorityApplyResult result = ProcessPriority.Apply(Environment.ProcessId, "Turbo");
            Assert.Equal(0, result.Ok);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
    }
}
