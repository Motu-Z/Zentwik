using Zentwik.Core.Services;

namespace Zentwik.Tests.Fakes
{
    /// <summary>
    /// CPU Sets 调度的测试替身：只记录调用，不做任何系统写入。
    ///
    /// <c>query-cpu-topology</c> 的逻辑分支依赖「逻辑核数 / CCD 数」，
    /// 而真实拓扑随开发机变化（16 核 / 32 线程只是本机形状）。
    /// 可配之后才能在同一套断言里同时覆盖「有 SMU 探测值」与「没有」两条路径。
    /// </summary>
    public sealed class FakeCpuSetScheduler : ICpuSetScheduler
    {
        public FakeCpuSetScheduler(int logicalProcessorCount = 32, int ccdCount = 2)
        {
            LogicalProcessorCount = logicalProcessorCount;
            CcdCount = ccdCount;
        }

        public int LogicalProcessorCount { get; set; }

        public int CcdCount { get; set; }

        /// <summary>按逻辑核数与组数等分出各组掩码，真实拓扑是硬件决定的，测试里只需形状对。</summary>
        public IReadOnlyList<CpuCoreGroup> CoreGroups => BuildGroups(LogicalProcessorCount, CcdCount);

        private static CpuCoreGroup[] BuildGroups(int logical, int count)
        {
            if (count <= 0)
                count = 1;

            int per = (logical + count - 1) / count;
            CpuCoreGroup[] groups = new CpuCoreGroup[count];

            for (int g = 0; g < count; g++)
            {
                int start = g * per;
                int end = Math.Min(start + per, logical);
                ulong mask = 0;

                for (int i = start; i < end && i < 64; i++)
                    mask |= 1UL << i;

                groups[g] = new CpuCoreGroup(g, mask);
            }

            return groups;
        }

        public string? LastTopologyError => null;

        /// <summary>成功绑定的掩码，按调用顺序记录。</summary>
        public List<ulong> Masks { get; } = new();

        /// <summary>成功绑定的 CCD，按调用顺序记录。</summary>
        public List<int> Ccds { get; } = new();

        public int ClearCount { get; private set; }

        public void ResetTopology()
        {
        }

        public CpuSetScheduleResult ScheduleToMask(int processId, ulong mask)
        {
            Masks.Add(mask);
            return new CpuSetScheduleResult { Success = true, AppliedCount = 1 };
        }

        public CpuSetScheduleResult ScheduleToCcd(int processId, int ccdIndex)
        {
            Ccds.Add(ccdIndex);
            return new CpuSetScheduleResult { Success = true, AppliedCount = 1, CcdIndex = ccdIndex };
        }

        public bool ClearDefaultCpuSets(int processId, out string? error)
        {
            ClearCount++;
            error = null;
            return true;
        }

        public bool IsCcdAvailable(int ccdIndex) => ccdIndex >= 0 && ccdIndex < CcdCount;
    }
}
