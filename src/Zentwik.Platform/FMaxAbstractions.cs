namespace Zentwik.Platform;

/// <summary>
/// AMD Boost Limit (FMax) 读写接口。与 OC 定频（0x5F）及 CO（0x06/0x0A）严格区分。
/// </summary>
public interface IFMaxTuningProvider
{
    bool TryReadFMax(out int limitMhz, out string? error);

    bool TryWriteFMax(int limitMhz, out string? error);
}
