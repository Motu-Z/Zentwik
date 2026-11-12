using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zentwik.Core.Smu;

/// <summary>
/// PawnIO 设备句柄与模块调用。SMU 邮箱事务由驱动模块完成，这里只搬运缓冲。
/// </summary>
internal sealed class PawnIoDevice : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821u << 2);
    private const uint IoctlExecute = DeviceType | (0x841u << 2);
    private const int NameLength = 32;
    private const int EHandle = unchecked((int)0x80070006);

    private readonly SafeFileHandle _handle;

    private PawnIoDevice(SafeFileHandle handle) => _handle = handle;

    public bool IsOpen => !_handle.IsInvalid && !_handle.IsClosed;

    public static PawnIoDevice? TryLoad(string modulePath)
    {
        if (!File.Exists(modulePath))
            return null;

        nint raw = CreateFileW(
            @"\\?\GLOBALROOT\Device\PawnIO",
            0x80000000u | 0x40000000u,
            3u,
            0,
            3u,
            0,
            0);

        if (raw == 0 || raw == -1)
            return null;

        PawnIoDevice device = new(new SafeFileHandle(raw, ownsHandle: true));

        byte[] module;
        try
        {
            module = File.ReadAllBytes(modulePath);
        }
        catch (IOException)
        {
            device.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            device.Dispose();
            return null;
        }

        if (!DeviceIoControl(device._handle, IoctlLoadBinary, module, (uint)module.Length, null, 0, out _, 0))
        {
            device.Dispose();
            return null;
        }

        return device;
    }

    /// <summary>执行模块函数，返回 HRESULT；输出按 long 逐个回填。</summary>
    public int Execute(string function, ReadOnlySpan<long> input, Span<long> output, out int written)
    {
        written = 0;
        if (!IsOpen)
            return EHandle;

        byte[] inBuffer = new byte[(input.Length * 8) + NameLength];
        byte[] name = Encoding.ASCII.GetBytes(function);
        int nameLength = Math.Min(NameLength - 1, name.Length);
        name.AsSpan(0, nameLength).CopyTo(inBuffer);
        MemoryMarshal.AsBytes(input).CopyTo(inBuffer.AsSpan(NameLength));

        byte[] outBuffer = new byte[output.Length * 8];
        bool ok = DeviceIoControl(
            _handle,
            IoctlExecute,
            inBuffer,
            (uint)inBuffer.Length,
            outBuffer.Length == 0 ? null : outBuffer,
            (uint)outBuffer.Length,
            out uint returned,
            0);

        if (!ok)
            return Marshal.GetHRForLastWin32Error();

        int bytes = (int)Math.Min(returned, (uint)outBuffer.Length);
        bytes -= bytes % 8;
        MemoryMarshal.Cast<byte, long>(outBuffer.AsSpan(0, bytes)).CopyTo(output);
        written = bytes / 8;
        return 0;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(string name, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, uint inSize, byte[]? outBuffer, uint outSize, out uint returned, nint overlapped);
}
