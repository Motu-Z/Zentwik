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
