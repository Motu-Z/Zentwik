using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 从 Windows 处理器关系信息读物理核数与 SMT 宽度。
    ///
    /// 【为什么不用 SMU 的 CoreCount】
    /// <c>SmuReader.CoreCount</c> 只有在一次完整快照探测之后才有值；Worker 冷启动时
    /// 只访问 <c>IsAvailable</c>，不读快照，于是它是 0。靠它建掩码网格，
    /// 拿到的会是「逻辑处理器数 = 核数、每核 1 线程」——界面上 32 个互不相关的格子。
    ///
    /// 【为什么不用 Environment.ProcessorCount】
    /// 那是逻辑处理器数，且会被作业对象的 CPU 限制改写。掩码按逻辑处理器位排布，
    /// 需要的是「有几个物理核、每核几个线程」这层结构。
    ///
    /// 本方法不需要提权，也不碰 SMU：进程被绑核、SMU 驱动没装的情况下都能用。
    /// </summary>
    public static class CpuTopologyProbe
    {
        /// <summary>RelationProcessorCore。</summary>
        private const int RelationProcessorCore = 0;

        /// <summary>单个 PROCESSOR_RELATIONSHIP 里第一组亲和性掩码的偏移（仅 64 位）。</summary>
        private const int FirstGroupMaskOffset = 32;

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

                int offset = 0;
                int coreCount = 0;
                int threads = 0;

                // 变长记录：每条前面都是 Relationship(4) + Size(4)，按 Size 前进。
                while (offset + 8 <= length)
                {
                    int relationship = Marshal.ReadInt32(buffer, offset);
                    int size = Marshal.ReadInt32(buffer, offset + 4);

                    if (size <= 0)
                        break;

                    if (relationship == RelationProcessorCore)
                    {
                        coreCount++;

                        if (threads == 0)
                            threads = ReadThreadCount(buffer + offset);
                    }

                    offset += size;
                }

                if (coreCount == 0)
                    return false;

                cores = coreCount;
                threadsPerCore = threads > 0 ? threads : 1;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 单个物理核的线程数 = 第一组亲和性掩码里置位的处理器数。
        ///
        /// 布局：Relationship(4) + Size(4) + Flags(1) + EfficiencyClass(1) + Reserved(20) +
        /// GroupCount(2)，随后 GroupMask 数组按指针宽度对齐（64 位下从 32 开始）。
        /// 32 位进程的偏移不同，本工程只出 64 位产物；不满足时返回 0 由调用方按 1 处理。
        /// </summary>
        private static int ReadThreadCount(IntPtr entry)
        {
            if (IntPtr.Size != 8)
                return 0;

            ulong mask = (ulong)Marshal.ReadInt64(entry, FirstGroupMaskOffset);
            return BitOperations.PopCount(mask);
        }
    }
}
