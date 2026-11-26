namespace Zentwik.Platform;

/// <summary>
/// AMD Boost Limit (FMax) 读写接口。与 OC 定频（0x5F）及 CO（0x06/0x0A）严格区分。
/// </summary>
public interface IFMaxTuningProvider
{
    /// <summary>回读容差：固件按自身步进取整，差值超过该值视为写入未被接受。</summary>
    const int ToleranceMhz = 10;

    bool TryReadFMax(out int limitMhz, out string? error);

    FMaxWriteResult TryWriteFMax(int limitMhz, out string? error);
}

/// <summary>
/// FMax 写入结果。Unsupported 表示平台未实现该消息或基线读为 0（能力缺失），
/// 与写入被拒绝、回读不符的 Failed 分开表达，避免把能力缺失记录成写入失败。
/// </summary>
public enum FMaxWriteResult
{
    Applied,
    Unsupported,
    Failed,
}

public static class FMaxTolerance
{
    public static bool Within(string? actual, string? desired) =>
        int.TryParse(actual, out var left)
        && int.TryParse(desired, out var right)
        && Math.Abs(left - right) <= IFMaxTuningProvider.ToleranceMhz;
}
