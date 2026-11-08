using System.Threading;
using Zentwik.Core.Services;

namespace Zentwik.Tests.Fakes
{
    /// <summary>
    /// 空壳 ISmuReader：默认 IsAvailable = false，写入返回「全成功」的空结果，不碰任何硬件。
    ///
    /// WaitForAvailability 与 IsAvailable 保持一致 —— 若声称可用却返回 false，
    /// TuningApplyService 会在等待阶段超时，用例要多跑 60 秒才失败。
    /// 需要走「真的下发」路径的用例把 <see cref="Available"/> 置 true。
    /// </summary>
    public sealed class FakeSmuReader : ISmuReader
    {
        /// <summary>是否声称 SMU 可写。默认 false（模拟没装驱动）。</summary>
        public bool Available { get; set; }

        public bool IsAvailable => Available;
        public string? LastError => Available ? null : "FakeSmuReader：测试不访问 SMU";
        public int CoreCount => 0;
        public uint CoresPerCcd => 0;
        public uint CoresPerCcdRaw => 0;
        public uint PeekCoresPerCcdRaw() => 0;
        public SmuSnapshot ReadSnapshot() => new() { Success = true };
        public SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins) => new() { AllSucceeded = true };
        public SmuFMaxResult WriteFMax(uint mhz) => new() { AllSucceeded = true };
        public bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default) => Available;
        public void Dispose() { }
    }
}
