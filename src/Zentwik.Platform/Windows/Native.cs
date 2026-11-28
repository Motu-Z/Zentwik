using System.Runtime.InteropServices;

namespace Zentwik.Platform.Windows;

/// <summary>Win32 导入集中处。全部使用 LibraryImport（源生成），AOT 下不需要运行时代码生成。</summary>
internal static partial class Native
{
    public const uint ErrorSuccess = 0;

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    /// <summary>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION，每个逻辑处理器一份，计数单位 100 ns。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessorPerformance
    {
        public long IdleTime;

        /// <summary>KernelTime 含 IdleTime，算占用率时必须减掉。</summary>
        public long KernelTime;

        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    internal const int SystemProcessorPerformanceInformation = 8;

    /// <summary>全表进程信息：PID、映像名、创建时间、Kernel+User 时间、基础优先级，一次系统调用。</summary>
    internal const int SystemProcessInformation = 5;

    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    internal const int StatusBufferOverflow = unchecked((int)0xC0000023);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(
        int systemInformationClass,
        nint buffer,
        uint length,
        out uint returnLength);

    internal static bool IsSuccess(int status) => status >= 0;

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "GlobalMemoryStatusEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetActiveProcessorGroupCount();

    internal const int RelationProcessorCore = 0;
    internal const int RelationCache = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLogicalProcessorInformationEx(
        int relationshipType,
        nint buffer,
        ref uint length);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial UIntPtr SetThreadAffinityMask(nint thread, UIntPtr mask);

    internal const int ThreadPriorityNormal = 0;

    internal const int ThreadPriorityHighest = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetThreadPriority(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadPriority(nint thread, int priority);

    internal static readonly nint InvalidHandleValue = -1;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        nint device,
        uint code,
        byte[]? inBuffer,
        uint inSize,
        byte[]? outBuffer,
        uint outSize,
        out uint returned,
        nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientSessionId(nint pipe, out uint sessionId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PowerReadFriendlyName(
        nint rootPowerKey,
        in Guid schemeGuid,
        nint subGroupGuid,
        nint powerSettingGuid,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerEnumerate(
        nint rootPowerKey,
        nint schemeGuid,
        nint subGroupGuid,
        uint accessor,
        uint index,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadDescription(
        nint rootPowerKey,
        in Guid schemeGuid,
        nint subGroupGuid,
        nint powerSettingGuid,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadACValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupGuid,
        in Guid settingGuid,
        out uint value);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadDCValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupGuid,
        in Guid settingGuid,
        out uint value);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteACValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupGuid,
        in Guid settingGuid,
        uint value);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteDCValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupGuid,
        in Guid settingGuid,
        uint value);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerDuplicateScheme(
        nint rootPowerKey,
        in Guid sourceSchemeGuid,
        out nint destinationSchemeGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteFriendlyName(
        nint rootPowerKey,
        in Guid schemeGuid,
        nint subGroupOfPowerSettingsGuid,
        nint powerSettingGuid,
        nint buffer,
        uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteDescription(
        nint rootPowerKey,
        in Guid schemeGuid,
        nint subGroupOfPowerSettingsGuid,
        nint powerSettingGuid,
        nint buffer,
        uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerDeleteScheme(nint rootPowerKey, in Guid schemeGuid);
}
