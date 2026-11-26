using System.Runtime.InteropServices;

namespace Zentwik.Platform.Windows;

/// <summary>
/// 轻量进程名表：一次 Toolhelp 快照只取 PID 与映像名，不对任何进程 OpenProcess。
/// 逐进程读名字或创建时间都要开句柄，几百个进程每轮就是几百次系统调用。
/// </summary>
internal static partial class NativeProcessList
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const int InvalidHandleValue = -1;
    private const int ExeNameChars = 260;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        public fixed char ExeFile[ExeNameChars];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>采一帧进程表；只填 PID 与映像名，失败返回空表不抛。</summary>
    public static List<(int ProcessId, string Name)> Capture()
    {
        var results = new List<(int, string)>(256);
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == 0 || snapshot == InvalidHandleValue)
        {
            return results;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return results;
            }

            do
            {
                var pid = (int)entry.ProcessId;
                if (pid <= 0)
                {
                    continue;
                }

                var name = ReadExeName(ref entry);
                if (name.Length > 0)
                {
                    results.Add((pid, name));
                }
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return results;
    }

    private static unsafe string ReadExeName(ref ProcessEntry32 entry)
    {
        fixed (char* raw = entry.ExeFile)
        {
            var span = new ReadOnlySpan<char>(raw, ExeNameChars);
            var end = span.IndexOf('\0');
            return end < 0 ? new string(span) : new string(span[..end]);
        }
    }
}
