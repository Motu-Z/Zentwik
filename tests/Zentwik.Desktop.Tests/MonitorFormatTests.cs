using System.Globalization;
using Xunit;
using Zentwik.Localization;

namespace Zentwik.Desktop.Tests;

/// <summary>
/// 监控页四个数值行的格式串。值来自本地化表，占位符写错会在第一帧采样时抛 FormatException，
/// 而冒烟测试不喂数据，抓不到这类错误。
/// </summary>
public sealed class MonitorFormatTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("en-US")]
    public void Monitor_Value_Formats_Accept_Sample_Numbers(string culture)
    {
        var catalog = LocalizationCatalog.CreateDefault();
        catalog.CurrentCulture = culture;

        Assert.Equal("71.4 °C", string.Format(CultureInfo.InvariantCulture, catalog["monitor.temperature_value"], 71.4));
        Assert.Equal("42.7 W", string.Format(CultureInfo.InvariantCulture, catalog["monitor.power_value"], 42.7));

        var clocks = string.Format(CultureInfo.CurrentCulture, catalog["monitor.clocks_value"], 4.21, 3.90, 4.65, 16);
        Assert.Contains("4.21", clocks);
        Assert.Contains("16", clocks);

        var utilization = string.Format(CultureInfo.CurrentCulture, catalog["monitor.utilization_value"], 11.4, 88.0, 32);
        Assert.Contains("11", utilization);
        Assert.Contains("32", utilization);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("en-US")]
    public void Monitor_And_Process_Captions_Exist(string culture)
    {
        var catalog = LocalizationCatalog.CreateDefault();
        catalog.CurrentCulture = culture;

        foreach (var key in new[]
                 {
                     "monitor.temperature",
                     "monitor.package_power",
                     "monitor.core_clocks",
                     "monitor.utilization",
                     "process.path",
                     "process.cpusets",
                     "process.cpu_percent",
                     "process.note",
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(catalog[key]));
        }
    }
}
