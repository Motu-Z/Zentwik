using System.Runtime.InteropServices;
using System.Text;

namespace Zentwik.Platform.Windows;

/// <summary>PawnIO 设备句柄。SMU 邮箱事务在驱动模块内完成，这里只搬运缓冲。</summary>
internal sealed class PawnIoSession : IDisposable
{
    // CTL_CODE(0xA1B2, 0x821/0x841): 0xA1B2 << 16 | function << 2
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821u << 2);
    private const uint IoctlExecute = DeviceType | (0x841u << 2);
    private const int NameLength = 32;
    private const int MaxArgs = 6;

    private nint _handle;

    private PawnIoSession(nint handle) => _handle = handle;

    public bool IsOpen => _handle != 0 && _handle != Native.InvalidHandleValue;

    public static PawnIoOpenResult Open(string? modulePath)
    {
        if (modulePath is null || !File.Exists(modulePath))
        {
            return new PawnIoOpenResult(null, HardwareAvailability.ModuleMissing,
                $"未找到 PawnIO 模块：{modulePath ?? "(未配置)"}。");
        }

        nint handle = Native.CreateFileW(
            @"\\?\GLOBALROOT\Device\PawnIO",
            Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite,
            0,
            Native.OpenExisting,
            0,
            0);

        if (handle == Native.InvalidHandleValue || handle == 0)
        {
            handle = Native.CreateFileW(
                @"\\.\PawnIO",
                Native.GenericRead | Native.GenericWrite,
                Native.FileShareRead | Native.FileShareWrite,
                0,
                Native.OpenExisting,
                0,
                0);
        }

        if (handle == Native.InvalidHandleValue || handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            var detail = error == 5
                ? "打开 PawnIO 设备被拒绝（需要管理员权限）。"
                : $"打开 PawnIO 设备失败，Win32 错误码 {error}。";
            return new PawnIoOpenResult(null, HardwareAvailability.DriverUnavailable, detail);
        }

        var session = new PawnIoSession(handle);
        byte[] module;
        try
        {
            module = File.ReadAllBytes(modulePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Dispose();
            return new PawnIoOpenResult(null, HardwareAvailability.ModuleMissing, $"读取模块失败：{ex.Message}");
        }

        if (!Native.DeviceIoControl(handle, IoctlLoadBinary, module, (uint)module.Length, null, 0, out _, 0))
        {
            var error = Marshal.GetLastWin32Error();
            session.Dispose();
            return new PawnIoOpenResult(null, HardwareAvailability.ProbeFailed,
                $"加载模块失败，Win32 错误码 {error}。签名版驱动只接受维护者签名的模块。");
        }

        return new PawnIoOpenResult(session, HardwareAvailability.Available, null);
    }

    public bool Execute(string function, ReadOnlySpan<long> input, Span<long> output, out int error)
    {
        error = 0;
        if (!IsOpen)
        {
            error = 6;
            return false;
        }

        var inBuffer = new byte[NameLength + (input.Length * 8)];
        var name = Encoding.ASCII.GetBytes(function);
        name.AsSpan(0, Math.Min(NameLength - 1, name.Length)).CopyTo(inBuffer);
        MemoryMarshal.AsBytes(input).CopyTo(inBuffer.AsSpan(NameLength));

        var outBuffer = output.Length == 0 ? null : new byte[output.Length * 8];
        var ok = Native.DeviceIoControl(
            _handle,
            IoctlExecute,
            inBuffer,
            (uint)inBuffer.Length,
            outBuffer,
            (uint)(outBuffer?.Length ?? 0),
            out var returned,
            0);

        if (!ok)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (outBuffer is not null)
        {
            var bytes = (int)Math.Min(returned, (uint)outBuffer.Length);
            bytes -= bytes % 8;
            MemoryMarshal.Cast<byte, long>(outBuffer.AsSpan(0, bytes)).CopyTo(output);
        }

        return true;
    }

    public void Dispose()
    {
        if (IsOpen)
        {
            Native.CloseHandle(_handle);
            _handle = 0;
        }
    }
}

internal readonly record struct PawnIoOpenResult(
    PawnIoSession? Session,
    HardwareAvailability Availability,
    string? Detail);
