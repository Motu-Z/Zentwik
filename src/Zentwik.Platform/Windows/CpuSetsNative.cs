using System.Runtime.InteropServices;

namespace Zentwik.Platform.Windows;

internal static partial class CpuSetsNative
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessSetLimitedInformation = 0x2000;
    internal const uint ProcessSetInformation = 0x0200;
    internal const uint StillActive = 259;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessAffinityMask(nint process, out nint processMask, out nint systemMask);

    /// <summary>已退出的进程仍能开句柄、读到退出前的值，写 CPU Sets 还会返回成功；动之前必须判存活。</summary>
    internal static bool IsAlive(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return false;
        }

        try
        {
            return GetExitCodeProcess(handle, out var code) && code == StillActive;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>进程是否被外部约束在比系统更窄的硬亲和性上；此时写 CPU Sets 会出现"绑了没变化"。</summary>
    internal static bool HasNarrowAffinity(nint processHandle, out nint processMask, out nint systemMask)
    {
        processMask = 0;
        systemMask = 0;
        if (!GetProcessAffinityMask(processHandle, out processMask, out systemMask))
        {
            return false;
        }

        return systemMask != 0 && processMask != systemMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemCpuSetInformation(
        nint information,
        uint bufferLength,
        out uint returnedLength,
        nint process,
        uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessDefaultCpuSets(
        nint process,
        [Out] uint[]? cpuSetIds,
        uint cpuSetIdCount,
        out uint requiredIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDefaultCpuSets(
        nint process,
        uint[]? cpuSetIds,
        uint cpuSetIdCount);

    internal static bool TryGetSystemCpuSetMap(
        out Dictionary<int, uint> logicalToId,
        out Dictionary<uint, int> idToLogical,
        out string? error)
    {
        logicalToId = new Dictionary<int, uint>();
        idToLogical = new Dictionary<uint, int>();
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "CPU Sets 仅支持 Windows。";
            return false;
        }

        uint length = 0;
        _ = GetSystemCpuSetInformation(0, 0, out length, 0, 0);
        if (length == 0)
        {
            error = $"读取系统 CPU Sets 拓扑失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, length, out length, 0, 0))
            {
                error = $"读取系统 CPU Sets 拓扑失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            var offset = 0;
            while (offset + 16 <= length)
            {
                var size = Marshal.ReadInt32(buffer, offset);
                var type = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0 || offset + size > length)
                {
                    break;
                }

                // SYSTEM_CPU_SET_INFORMATION: Type == 0 (CpuSetInformation)
                // Offset 8: uint Id; Offset 12: ushort Group; Offset 14: byte LogicalProcessorIndex
                if (type == 0)
                {
                    var id = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                    var group = Marshal.ReadInt16(buffer, offset + 12);
                    var logicalIndex = Marshal.ReadByte(buffer, offset + 14) + (group * 64);
                    logicalToId[logicalIndex] = id;
                    idToLogical[id] = logicalIndex;
                }

                offset += size;
            }

            if (logicalToId.Count == 0)
            {
                error = "系统未返回可用的 CPU Set 条目。";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}