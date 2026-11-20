using System;
using Microsoft.Win32;

namespace Zentwik.Core.Services
{
    /// <summary>处理器厂商。只用来给核心组取名字，不参与任何分区判断。</summary>
    public enum CpuVendor
    {
        Unknown = 0,
        Amd = 1,
        Intel = 2
    }

    /// <summary>读处理器厂商。分区靠 L3 缓存域观测，厂商只决定预置项怎么叫。</summary>
    public static class CpuVendorProbe
    {
        private const string KeyPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        public static CpuVendor Detect()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath);
                string? id = key?.GetValue("VendorIdentifier") as string;

                if (string.IsNullOrEmpty(id))
                    return CpuVendor.Unknown;

                if (id.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                    return CpuVendor.Amd;

                if (id.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    return CpuVendor.Intel;

                return CpuVendor.Unknown;
            }
            catch
            {
                return CpuVendor.Unknown;
            }
        }

        /// <summary>
        /// 核心组默认名。单组为 All Cores，AMD 为 CCD n，其余为 Group n。
        /// </summary>
        public static string GroupName(CpuVendor vendor, int index, int total) => total <= 1
            ? "All Cores"
            : vendor == CpuVendor.Amd
                ? $"CCD {index}"
                : $"Group {index}";
    }

    /// <summary>厂商在协议里的写法。线格式用小写字符串，两端共用一份换算。</summary>
    public static class CpuVendorNames
    {
        public const string Amd = "amd";
        public const string Intel = "intel";
        public const string Unknown = "unknown";

        public static string ToName(CpuVendor vendor) => vendor switch
        {
            CpuVendor.Amd => Amd,
            CpuVendor.Intel => Intel,
            _ => Unknown
        };

        public static CpuVendor Parse(string? name) => name?.Trim().ToLowerInvariant() switch
        {
            Amd => CpuVendor.Amd,
            Intel => CpuVendor.Intel,
            _ => CpuVendor.Unknown
        };
    }
}
