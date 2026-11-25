using Zentwik.Platform;

namespace Zentwik.Contracts.Tests;

internal sealed class TestSystemInfo : ISystemInfo
{
    public string MachineName => "test-machine";

    public bool IsElevated => false;

    public int ProcessorCount => 4;

    public int ProcessorGroups => 1;

    public string? CpuVendor => "TestVendor";

    public string? CpuName => "Test CPU";

    public string? CpuIdentifier => "Test Family 0 Model 0";
}
