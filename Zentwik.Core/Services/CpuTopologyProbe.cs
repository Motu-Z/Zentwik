using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Zentwik.Core.Helpers;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 从 Windows 处理器关系信息读物理核数与 SMT 宽度。
    ///
    /// <c>SmuReader.CoreCount</c> 只有在一次完整快照探测之后才有值；Worker 冷启动时
    /// 只访问 <c>IsAvailable</c>，不读快照，于是它是 0。靠它建掩码网格，
    /// 拿到的会是「逻辑处理器数 = 核数、每核 1 线程」——界面上 32 个互不相关的格子。
    ///
    /// 那是逻辑处理器数，且会被作业对象的 CPU 限制改写。掩码按逻辑处理器位排布，
    /// 需要的是「有几个物理核、每核几个线程」这层结构。
    ///
    /// 本方法不需要提权，也不碰 SMU：进程被绑核、SMU 驱动没装的情况下都能用。
    /// </summary>
    public static class CpuTopologyProbe
    {
        private const int RelationProcessorCore = 0;

        /// <summary>单个 PROCESSOR_RELATIONSHIP 里第一组亲和性掩码的偏移（仅 64 位）。</summary>
        private const int FirstGroupMaskOffset = 32;

        /// <summary>单个 PROCESSOR_RELATIONSHIP 里 EfficiencyClass 的偏移（Flags 之后一字节）。</summary>
        private const int EfficiencyClassOffset = 9;

        /// <summary>处理器组个数（WORD，Reserved[20] 之后）。</summary>
        private const int GroupCountOffset = 30;

        /// <summary>每个 GROUP_AFFINITY 的长度：Mask(8) + GroupNumber(2) + Reserved(6)。</summary>
        private const int GroupAffinitySize = 16;

        /// <summary>一个物理核：线程掩码 + 效率类。效率类越大越偏性能。</summary>
        public readonly record struct CoreRelation(CoreMask Mask, byte EfficiencyClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationshipType,
            IntPtr buffer,
            ref int returnedLength);

        /// <summary>
        /// 读一次拓扑。失败返回 false，调用方自行退回单线程假设。
        /// </summary>
        public static bool TryRead(out int cores, out int threadsPerCore)
        {
            cores = 0;
            threadsPerCore = 0;

            if (!TryReadCores(out CoreRelation[] relations))
                return false;

            cores = relations.Length;
            threadsPerCore = Math.Max(1, relations[0].Mask.PopCount);
            return true;
        }

        /// <summary>
        /// 读每个物理核的线程掩码与效率类。混合架构靠效率类区分 P / E 核。
        /// </summary>
        public static bool TryReadCores(out CoreRelation[] cores)
        {
            cores = Array.Empty<CoreRelation>();

            int length = 0;

            // 第一次调用只为拿所需长度，必然返回 false。
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);

            if (length <= 0)
                return false;

            IntPtr buffer = Marshal.AllocHGlobal(length);

            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                    return false;

                List<CoreRelation> found = new();

                // 变长记录：每条前面都是 Relationship(4) + Size(4)，按 Size 前进。
                int offset = 0;

                while (offset + 8 <= length)
                {
                    int relationship = Marshal.ReadInt32(buffer, offset);
                    int size = Marshal.ReadInt32(buffer, offset + 4);

                    if (size <= 0)
                        break;

                    if (relationship == RelationProcessorCore)
                        AddCore(buffer, offset, found);

                    offset += size;
                }

                if (found.Count == 0)
                    return false;

                cores = found.ToArray();
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void AddCore(IntPtr buffer, int offset, List<CoreRelation> sink)
        {
            if (IntPtr.Size != 8)
                return;

            int groupCount = Marshal.ReadInt16(buffer, offset + GroupCountOffset);
            CoreMask mask = CoreMask.Zero;

            // 一个核的线程可能分在不同处理器组里（>64 逻辑处理器时最典型），
            // 每组都从位 0 起，必须按组号换算到全局位，否则会相撞。
            for (int i = 0; i < groupCount; i++)
            {
                int entry = offset + FirstGroupMaskOffset + (i * GroupAffinitySize);
                ulong groupMask = (ulong)Marshal.ReadInt64(buffer, entry);

                if (groupMask == 0)
                    continue;

                int groupNumber = Marshal.ReadInt16(buffer, entry + 8);
                mask |= CoreMask.FromGroupMask(groupNumber, groupMask);
            }

            // 全部落在 128 位以外（第三组及以后）时无法表达，与表达上限一致地跳过。
            if (mask.IsZero)
                return;

            sink.Add(new CoreRelation(mask, Marshal.ReadByte(buffer, offset + EfficiencyClassOffset)));
        }
    }
}
