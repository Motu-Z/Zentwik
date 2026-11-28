using System.Runtime.InteropServices;

namespace Zentwik.Platform.Windows;

/// <summary>
/// 一帧进程快照的原始项。CreateTime 是进程创建时间（FILETIME），用于区分 PID 复用。
/// </summary>
internal readonly record struct RawProcessEntry(
    int ProcessId,
    string Name,
    long CreateTime,
    long CpuTime100Ns,
    int BasePriority);

/// <summary>
/// 用 NtQuerySystemInformation(SystemProcessInformation) 一次拿全表：PID、映像名、创建时间、
/// Kernel+User 时间、基础优先级。不对任何进程 OpenProcess，因此可以每轮全量采集。
/// </summary>
/// <remarks>
/// 只声明到 InheritedFromUniqueProcessId 为止；后面的线程数组不读，靠 NextEntryOffset 跨过。
/// 结构体必须保持 blittable 并靠自然对齐：x64 下 ImageName 在偏移 56、BasePriority 72、PID 80。
/// </remarks>
internal static partial class NativeProcessSnapshot
{
    private const int InitialBufferBytes = 1 << 20;
    private const int MaxBufferBytes = 64 << 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    public static IReadOnlyList<RawProcessEntry> Capture()
    {
        var size = InitialBufferBytes;

        for (var attempt = 0; attempt < 8 && size <= MaxBufferBytes; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buffer, (uint)size, out var needed);
                if (status is Native.StatusInfoLengthMismatch or Native.StatusBufferOverflow)
                {
                    // 系统在这两次调用之间又加了进程：按回报长度翻倍重试，不猜。
                    size = (int)Math.Max(needed, (uint)(size * 2));
                    continue;
                }

                if (!Native.IsSuccess(status))
                {
                    return [];
                }

                return Walk(buffer, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private static List<RawProcessEntry> Walk(nint buffer, int size)
    {
        var results = new List<RawProcessEntry>(512);
        var offset = 0;
        var entrySize = Marshal.SizeOf<SystemProcessInformation>();

        while (offset >= 0 && offset + entrySize <= size)
        {
            var entry = Marshal.PtrToStructure<SystemProcessInformation>(buffer + offset);
            var pid = (int)entry.UniqueProcessId;

            // PID 0 是 System Idle Process，不是可操作进程；名字为空同样跳过。
            if (pid > 0 && entry.ImageName.Length > 0 && entry.ImageName.Buffer != 0)
            {
                var name = Marshal.PtrToStringUni(entry.ImageName.Buffer, entry.ImageName.Length / 2);
                if (!string.IsNullOrEmpty(name))
                {
                    results.Add(new RawProcessEntry(
                        pid,
                        name,
                        entry.CreateTime,
                        entry.UserTime + entry.KernelTime,
                        entry.BasePriority));
                }
            }

            if (entry.NextEntryOffset == 0)
            {
                break;
            }

            offset += (int)entry.NextEntryOffset;
        }

        return results;
    }
}
