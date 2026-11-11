using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Zentwik.Core.Services
{
    /// <summary>进程身份：PID + 映像名（不含路径）。</summary>
    internal readonly record struct ProcessIdentity(int Pid, string Name);

    /// <summary>
    /// 轻量进程名表：一次 Toolhelp32 快照只取 PID 与映像名，不对任何进程 OpenProcess。
    ///
    /// 取代 WMI 的 <c>__InstanceCreationEvent</c>。那条查询是「WMI 每 2 秒把
    /// Win32_Process 全表枚举一遍再 diff」，创建与删除两个订阅叠起来后
    /// WmiPrvSE 常驻吃 8% 单核，而任务管理器的搜索、服务页、启动页全排在
    /// 同一个 WMI 队列后面 —— 表现就是系统卡、任务管理器打字几秒才出结果。
    ///
    /// 本类 300 进程约 1ms，1 秒一次常驻占用不到 0.1%。
    /// </summary>
    internal static partial class NativeProcessList
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const int INVALID_HANDLE_VALUE = -1;
        private const int ExeNameChars = 260;

        /// <summary>
        /// Toolhelp 快照条目。szExeFile 用定长内联缓冲区：源生成的 P/Invoke
        /// 不支持字符串字段按值封送（SYSLIB1051）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            public fixed char szExeFile[ExeNameChars];
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 采一帧进程表并填进 <paramref name="sink"/>（先清空，复用同一个 List 避免每轮分配）。
        /// 失败返回空表，不抛。
        /// </summary>
        public static List<ProcessIdentity> Capture(List<ProcessIdentity> sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            sink.Clear();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(INVALID_HANDLE_VALUE))
                return sink;

            try
            {
                PROCESSENTRY32 entry = new() { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };

                if (!Process32FirstW(snapshot, ref entry))
                    return sink;

                do
                {
                    int pid = (int)entry.th32ProcessID;
                    if (pid <= 0)
                        continue;

                    string name = ReadExeName(ref entry);
                    if (name.Length == 0)
                        continue;

                    sink.Add(new ProcessIdentity(pid, name));
                }
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return sink;
        }

        /// <summary>读定长缓冲区里的进程名（以第一个 NUL 为界）。</summary>
        private static unsafe string ReadExeName(ref PROCESSENTRY32 entry)
        {
            fixed (char* raw = entry.szExeFile)
            {
                ReadOnlySpan<char> span = new(raw, ExeNameChars);
                int end = span.IndexOf('\0');
                return end < 0 ? new string(span) : new string(span[..end]);
            }
        }
    }
}
