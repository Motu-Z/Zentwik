using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Zentwik.Platform.Windows;

public sealed class WindowsSystemInfo : ISystemInfo
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public string MachineName => Environment.MachineName;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public int ProcessorCount => Environment.ProcessorCount;

    public int PhysicalCoreCount
    {
        get
        {
            uint length = 0;
            _ = Native.GetLogicalProcessorInformationEx(Native.RelationProcessorCore, 0, ref length);
            if (length == 0)
            {
                return ProcessorCount;
            }

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!Native.GetLogicalProcessorInformationEx(Native.RelationProcessorCore, buffer, ref length))
                {
                    return ProcessorCount;
                }

                var count = 0;
                var offset = 0;
                while (offset + 8 <= length)
                {
                    var size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size <= 0)
                    {
                        break;
                    }

                    if (Marshal.ReadInt32(buffer, offset) == Native.RelationProcessorCore)
                    {
                        count++;
                    }

                    offset += size;
                }

                return count > 0 ? count : ProcessorCount;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public int ProcessorGroups => (int)Native.GetActiveProcessorGroupCount();

    public string? CpuVendor => ReadProcessorValue("VendorIdentifier");

    public string? CpuName => ReadProcessorValue("ProcessorNameString");

    public string? CpuIdentifier => ReadProcessorValue("Identifier");

    private static string? ReadProcessorValue(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProcessorKey);
            return (key?.GetValue(name) as string)?.Trim();
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }
}
