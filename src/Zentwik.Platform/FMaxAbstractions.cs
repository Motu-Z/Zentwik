namespace Zentwik.Platform;

/// <summary>
/// AMD Boost 频率上限（FMax）读写接口。参数单位是 MHz 的**绝对频率**，由用户直接指定。
/// 它不是 Boost Clock Override / PBO 的 -1000…+200 MHz 有符号偏移参数（后者尚未实现），
/// 也不是 OC 定频（0x5F）或 CO（0x06/0x0A）。
/// </summary>
public interface IFMaxTuningProvider
{
    /// <summary>工具支持的绝对频率范围（MHz）。只用于应用时钳制输入，不是固件限制；真实生效值以写入后回读为准。</summary>
    const int MinSupportedMhz = 1000;
    const int MaxSupportedMhz = 7000;

    /// <summary>回读容差：固件按自身步进取整，差值超过该值视为写入未被接受。</summary>
    const int ToleranceMhz = 10;

    bool TryReadFMax(out int limitMhz, out string? error);

    FMaxWriteResult TryWriteFMax(int limitMhz, out string? error);
}

/// <summary>
/// FMax 写入结果。Unsupported 表示没有配置对应 SMU 写入命令（能力缺失），协调器按 Skipped 降级。
/// Failed 表示写入被拒、回读不符，或基线读为 0（基线未知，拒绝写入；0 不是"平台不支持"的证据）。
/// </summary>
public enum FMaxWriteResult
{
    Applied,
    Unsupported,
    Failed,
}

/// <summary>应用时把用户指定的绝对频率钳到工具支持范围；不因越界拒绝输入，也不宣称固件上下限。</summary>
public static class FMaxRange
{
    public static int Clamp(int requestedMhz) =>
        Math.Clamp(requestedMhz, IFMaxTuningProvider.MinSupportedMhz, IFMaxTuningProvider.MaxSupportedMhz);
}

public static class FMaxTolerance
{
    public static bool Within(string? actual, string? desired) =>
        int.TryParse(actual, out var left)
        && int.TryParse(desired, out var right)
        && Math.Abs(left - right) <= IFMaxTuningProvider.ToleranceMhz;
}
